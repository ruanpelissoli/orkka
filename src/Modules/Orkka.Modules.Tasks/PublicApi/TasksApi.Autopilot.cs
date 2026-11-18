namespace Orkka.Modules.Tasks.PublicApi;

using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Tasks.Contracts;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// Autopilot's read/write surface for the Worker sweep. Everything here is scoped to a session,
/// because the session is the window the cap and the counters are defined over.
/// </summary>
public sealed partial class TasksApi
{
    /// <summary>Statuses that mean the task can still move without anyone touching it.</summary>
    private static readonly TaskStatus[] ProgressingStatuses =
        [TaskStatus.Active, TaskStatus.InQueue];

    /// <summary>Terminal failures. A task here has stopped and will not resume on its own.</summary>
    private static readonly TaskStatus[] FailedStatuses =
        [TaskStatus.Blocked, TaskStatus.ReasoningTimeout, TaskStatus.OutOfTokens];

    /// <summary>Waiting on a person (or their runner) — the "look at these first" bucket.
    /// Explicit, never a residual subtraction: the residual absorbed every unmodelled state
    /// (a reset task back in Draft, Resetting itself) and inflated the count permanently.</summary>
    private static readonly TaskStatus[] ParkedForHumanStatuses =
        [TaskStatus.WaitingAnswer, TaskStatus.WaitingPlanApproval, TaskStatus.WaitingCodeReview,
         TaskStatus.WaitingQaReview, TaskStatus.WaitingUserVerification, TaskStatus.WaitingForRunner];

    /// <summary>How many terminal outcomes the consecutive-failure check looks back over.</summary>
    private const int TerminalLookback = 5;

    public async Task<Ulid?> StartAutopilotSessionAsync(
        Ulid orgId, Ulid projectId, string startedByUserId, decimal? capUsd,
        bool answerEveryQuestion, CancellationToken ct = default)
    {
        // Checked first for a clean "already running" answer; the unique index is what actually
        // guarantees it, and catching its violation below is what makes two simultaneous
        // clicks safe rather than merely unlikely.
        var alreadyOpen = await _db.AutopilotSessions
            .AsNoTracking()
            .AnyAsync(s => s.ProjectId == projectId && s.OrgId == orgId
                && s.StopReason == AutopilotStopReason.Running, ct);

        if (alreadyOpen)
            return null;

        var session = new Domain.AutopilotSession
        {
            Id = Ulid.NewUlid(),
            OrgId = orgId,
            ProjectId = projectId,
            StartedByUserId = startedByUserId,
            StartedAt = DateTimeOffset.UtcNow,
            CapUsd = capUsd,
            AnswerEveryQuestion = answerEveryQuestion,
        };

        _db.AutopilotSessions.Add(session);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The index rejected it: another request opened a session between the check above
            // and this save. The caller wanted "autopilot is on", and it is.
            _db.Entry(session).State = EntityState.Detached;
            return null;
        }

        return session.Id;
    }

    public async Task<AutopilotStatus> GetAutopilotStatusAsync(
        Ulid orgId, Ulid projectId, CancellationToken ct = default)
    {
        // Newest session, open or not. A closed one still answers the question the board asks
        // in the morning: what happened while I was away?
        var session = await _db.AutopilotSessions
            .AsNoTracking()
            .Where(s => s.OrgId == orgId && s.ProjectId == projectId)
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync(ct);

        var draftingRemaining = await _db.Tasks
            .AsNoTracking()
            .CountAsync(t => t.OrgId == orgId && t.ProjectId == projectId
                && t.CurrentLane == Lane.Drafting && t.Status == TaskStatus.Draft
                && !t.IsArchived && t.RepoId != null && t.ResetRequestedAt == null, ct);

        if (session is null)
        {
            return new AutopilotStatus(
                Running: false, SessionId: null, StartedAt: null, StoppedAt: null,
                StopReason: null, CapUsd: null, AnswerEveryQuestion: false, SpentUsd: 0,
                TasksSubmitted: 0, TasksCompleted: 0, TasksFailed: 0,
                TasksInFlight: 0, TasksWaitingOnYou: 0,
                DraftingRemaining: draftingRemaining);
        }

        var progress = await GetAutopilotProgressAsync(session.Id, ct);
        var running = session.StopReason == AutopilotStopReason.Running;

        return new AutopilotStatus(
            Running: running,
            SessionId: session.Id,
            StartedAt: session.StartedAt,
            StoppedAt: session.StoppedAt,
            StopReason: running ? null : session.StopReason.ToString(),
            CapUsd: session.CapUsd,
            AnswerEveryQuestion: session.AnswerEveryQuestion,
            SpentUsd: session.SpentUsd,
            TasksSubmitted: session.TasksSubmitted,
            TasksCompleted: progress.Completed,
            TasksFailed: progress.Failed,
            TasksInFlight: progress.Progressing,
            TasksWaitingOnYou: progress.ParkedForHuman,
            DraftingRemaining: draftingRemaining);
    }

    public async Task<IReadOnlyList<OpenAutopilotSession>> ListOpenAutopilotSessionsAsync(
        CancellationToken ct = default)
        => await _db.AutopilotSessions
            .AsNoTracking()
            .IgnoreQueryFilters()  // cross-tenant: the Worker has no user context
            .Where(s => s.StopReason == AutopilotStopReason.Running)
            .OrderBy(s => s.StartedAt)
            .Select(s => new OpenAutopilotSession(
                s.Id, s.OrgId, s.ProjectId, s.StartedByUserId, s.StartedAt,
                s.CapUsd, s.SpentUsd, s.TasksSubmitted, s.UpdatedAt))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<OpenAutopilotSession>> ListDrainingAutopilotSessionsAsync(
        CancellationToken ct = default)
    {
        // "Still draining" = any session task can still move, or moved after the session row
        // was last refreshed (a refresh that changes a counter restamps UpdatedAt, so once
        // one lands after the last task settles, the session drops out of this list). The
        // 24h floor bounds the no-op case — a task edit that changes no counter would
        // otherwise keep the session matching forever, since the inequality-guarded update
        // never restamps.
        var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
        return await _db.AutopilotSessions
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(s => s.StopReason != AutopilotStopReason.Running
                && _db.Tasks.Any(t => t.AutopilotSessionId == s.Id
                    && (ProgressingStatuses.Contains(t.Status)
                        || (t.UpdatedAt > s.UpdatedAt && t.UpdatedAt > cutoff))))
            .OrderBy(s => s.StartedAt)
            .Select(s => new OpenAutopilotSession(
                s.Id, s.OrgId, s.ProjectId, s.StartedByUserId, s.StartedAt,
                s.CapUsd, s.SpentUsd, s.TasksSubmitted, s.UpdatedAt))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AutopilotCandidate>> ListAutopilotCandidatesAsync(
        Ulid orgId, Ulid projectId, int limit, CancellationToken ct = default)
        => await _db.Tasks
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(t => t.OrgId == orgId
                && t.ProjectId == projectId
                && t.CurrentLane == Lane.Drafting
                && t.Status == TaskStatus.Draft
                && !t.IsArchived
                // A task with no repo cannot be submitted at all — SubmitTask refuses it, and
                // there is no repo picker on the task page to fix it afterwards.
                && t.RepoId != null
                && t.ResetRequestedAt == null)
            // Blockers go first, always. Submitting a dependent while the task it waits on is
            // still in Drafting parks it in the queue forever — and, because queued work counts
            // as in-flight, can consume the headroom the blocker needed to be submitted at all.
            .Where(Features.Tasks.TaskOrderingQueries.AllBlockersSubmitted)
            // A milestone that waits on another must not have its tasks submitted while that
            // one is still being planned — submitted-but-waiting work counts as in-flight and
            // eats the headroom the blocking milestone needs (the same trap AllBlockersSubmitted
            // exists for, one level up). Once every milestone it waits on is past planning, its
            // tasks are submitted to be PLANNED; the queue banks the plan and holds the build
            // until the milestone ships (MilestoneDependencyQueries.PlanAheadAcrossGate).
            .Where(Features.Milestones.MilestoneDependencyQueries.AutopilotMaySubmit)
            // Milestone position first: the roadmap is the order the user asked for. Tasks with
            // no milestone are the backlog and sort last, which is what "backlog" means.
            .OrderBy(t => t.Milestone == null ? 1 : 0)
            .ThenBy(t => t.Milestone!.Position)
            // Ranked, never the raw enum — the column is a string and would sort Critical last.
            .ThenByDescending(Features.Tasks.TaskOrderingQueries.PriorityRank)
            .ThenBy(t => t.Number)
            .Take(limit)
            .Select(t => new AutopilotCandidate(
                t.Id, t.RepoId, t.MilestoneId, t.Number, t.Priority))
            .ToListAsync(ct);

    public async Task<AutopilotProgress> GetAutopilotProgressAsync(
        Ulid sessionId, CancellationToken ct = default)
    {
        // Archived rows stay in the read: archiving a shipped task must not make yesterday's
        // "12 shipped" read 11 today. Individual buckets decide below what archiving means
        // for them.
        var rows = await _db.Tasks
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(t => t.AutopilotSessionId == sessionId)
            .Select(t => new { t.Status, t.UpdatedAt, t.IsArchived })
            .ToListAsync(ct);

        var progressing = rows.Count(r => !r.IsArchived && ProgressingStatuses.Contains(r.Status));
        // Shipped is shipped — archived completions keep counting.
        var completed = rows.Count(r => r.Status == TaskStatus.Completed);
        // Failures and parks stop counting when archived: archiving is exactly how a user
        // dismisses one they've decided not to act on.
        var failed = rows.Count(r => !r.IsArchived && FailedStatuses.Contains(r.Status));
        var parked = rows.Count(r => !r.IsArchived && ParkedForHumanStatuses.Contains(r.Status));
        var waitingForRunner = rows.Count(r => !r.IsArchived && r.Status == TaskStatus.WaitingForRunner);

        // Newest terminal outcomes first, as a success/failure sequence. Computed here rather
        // than carried between polls so a Worker restart cannot lose the count. Archived
        // failures drop out — dismissing them is human intervention, which ends the streak.
        var recentlyTerminal = rows
            .Where(r => r.Status == TaskStatus.Completed
                || (!r.IsArchived && FailedStatuses.Contains(r.Status)))
            .OrderByDescending(r => r.UpdatedAt)
            .Take(TerminalLookback)
            .Select(r => r.Status == TaskStatus.Completed)
            .ToList();

        return new AutopilotProgress(progressing, parked, completed, failed, recentlyTerminal, waitingForRunner);
    }

    public async Task<bool> HasMilestoneWorkInFlightAsync(Ulid projectId, CancellationToken ct = default)
    {
        // Spelled out rather than Contains() over the status arrays: the column is a
        // string-converted enum, and an explicit OR translates on every provider.
        var taskLanding = await _db.Tasks
            .AsNoTracking()
            .AnyAsync(t => t.ProjectId == projectId && !t.IsArchived && t.MilestoneId != null
                && t.ResetRequestedAt == null
                && (t.Status == TaskStatus.Active
                    || t.Status == TaskStatus.InQueue
                    || t.Status == TaskStatus.WaitingForRunner
                    // Finished, PR open, merge still to come — the sweep's to land.
                    || (t.Status == TaskStatus.Completed && !t.IsMerged && t.PrUrl != null && t.PrClosedAt == null)), ct);
        if (taskLanding)
            return true;

        return await _db.Milestones
            .AsNoTracking()
            .AnyAsync(m => m.ProjectId == projectId
                && (m.ShipState == MilestoneShipState.Pending || m.ShipState == MilestoneShipState.Shipping), ct);
    }

    public async Task<IReadOnlyList<Ulid>> ListAutopilotSessionTaskIdsAsync(
        Ulid sessionId, CancellationToken ct = default)
        => await _db.Tasks
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(t => t.AutopilotSessionId == sessionId)
            .Select(t => t.Id)
            .ToListAsync(ct);

    /// <summary>
    /// Scale of AutopilotSession.SpentUsd (2 decimals). Callers hand in a sum of
    /// UsageRecord.CostTotal, which carries 8 — sub-cent digits the session does not keep.
    /// Rounding before the inequality guard reads the value is what makes "did anything change?"
    /// a question about the session rather than about precision: an 8-decimal sum never equals
    /// the 2-decimal value already stored, so every sweep rewrote the row, restamped UpdatedAt
    /// and broadcast AutopilotChanged to a board nobody was watching. Rounding here, once, is
    /// what makes the guard compare exactly what the next read returns.
    /// </summary>
    private const int SpentUsdScale = 2;

    public async Task UpdateAutopilotSessionAsync(
        Ulid sessionId, decimal spentUsd, int tasksSubmitted, int tasksCompleted,
        int tasksFailed, int consecutiveFailures, CancellationToken ct = default)
    {
        spentUsd = Math.Round(spentUsd, SpentUsdScale, MidpointRounding.AwayFromZero);

        // The inequality guard is what keeps UpdatedAt meaningful: an idle sweep writes nothing,
        // so "UpdatedAt is old" genuinely means "nothing has happened", which is exactly what
        // the drained check's quiet period needs to be able to trust.
        var rows = await _db.AutopilotSessions
            .IgnoreQueryFilters()
            .Where(s => s.Id == sessionId
                && (s.SpentUsd != spentUsd
                    || s.TasksSubmitted != tasksSubmitted
                    || s.TasksCompleted != tasksCompleted
                    || s.TasksFailed != tasksFailed
                    || s.ConsecutiveFailures != consecutiveFailures))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.SpentUsd, spentUsd)
                .SetProperty(x => x.TasksSubmitted, tasksSubmitted)
                .SetProperty(x => x.TasksCompleted, tasksCompleted)
                .SetProperty(x => x.TasksFailed, tasksFailed)
                .SetProperty(x => x.ConsecutiveFailures, consecutiveFailures)
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow),
                ct);

        // Counter movement used to be broadcast only at stop, so an open popover's numbers
        // froze at page-load values for the whole session. Composed by hand (not via
        // GetAutopilotStatusAsync) because the Worker has no user context for the tenant
        // query filters.
        if (rows == 0)
            return;

        var session = await _db.AutopilotSessions
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(s => s.Id == sessionId)
            .Select(s => new { s.OrgId, s.ProjectId, s.StartedAt, s.StoppedAt, s.StopReason, s.CapUsd, s.AnswerEveryQuestion })
            .FirstOrDefaultAsync(ct);
        if (session is null)
            return;

        var progress = await GetAutopilotProgressAsync(sessionId, ct);
        var draftingRemaining = await _db.Tasks
            .AsNoTracking()
            .IgnoreQueryFilters()
            .CountAsync(t => t.OrgId == session.OrgId && t.ProjectId == session.ProjectId
                && t.CurrentLane == Lane.Drafting && t.Status == TaskStatus.Draft
                && !t.IsArchived && t.RepoId != null && t.ResetRequestedAt == null, ct);

        var running = session.StopReason == AutopilotStopReason.Running;
        await taskNotifier.SendAutopilotChangedAsync(session.ProjectId, new AutopilotChangedEvent(
            session.ProjectId,
            new AutopilotStatus(
                Running: running,
                SessionId: sessionId,
                StartedAt: session.StartedAt,
                StoppedAt: session.StoppedAt,
                StopReason: running ? null : session.StopReason.ToString(),
                CapUsd: session.CapUsd,
                AnswerEveryQuestion: session.AnswerEveryQuestion,
                SpentUsd: spentUsd,
                TasksSubmitted: tasksSubmitted,
                TasksCompleted: progress.Completed,
                TasksFailed: progress.Failed,
                TasksInFlight: progress.Progressing,
                TasksWaitingOnYou: progress.ParkedForHuman,
                DraftingRemaining: draftingRemaining),
            DateTimeOffset.UtcNow));
    }

    public async Task<bool> StopAutopilotSessionAsync(
        Ulid sessionId, AutopilotStopReason reason, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        // Guarded on still-Running so exactly one replica wins, and so a user's manual stop
        // cannot be overwritten by a sweep deciding the session drained a moment later.
        var session = await _db.AutopilotSessions
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);

        if (session is null)
            return false;

        var rows = await _db.AutopilotSessions
            .IgnoreQueryFilters()
            .Where(s => s.Id == sessionId && s.StopReason == AutopilotStopReason.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.StopReason, reason)
                .SetProperty(x => x.StoppedAt, now)
                .SetProperty(x => x.UpdatedAt, now),
                ct);

        if (rows == 0)
            return false;

        // ExecuteUpdate bypasses the change tracker, so the event the notification handler
        // reacts to has to be published by hand — the same rule MarkTasksOutOfTokensAsync
        // follows. Published only by the winner of the guarded update above, so a session can
        // never notify twice.
        var progress = await GetAutopilotProgressAsync(sessionId, ct);

        await _eventBus.PublishAsync(new Orkka.SharedKernel.Events.Catalog.AutopilotSessionStopped(
            sessionId, session.OrgId, session.ProjectId, session.StartedByUserId,
            reason.ToString(), progress.Completed, progress.Failed, progress.ParkedForHuman,
            session.SpentUsd), ct);

        return true;
    }
}
