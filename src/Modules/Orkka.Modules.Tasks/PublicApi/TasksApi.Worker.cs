namespace Orkka.Modules.Tasks.PublicApi;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Utilities;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// Worker/Execution-module surface of the Tasks API (P10). Query bodies are ported verbatim
/// from the former Worker TaskOrchestrator/TaskQueueProcessor direct-DbContext access; each
/// write method performs exactly one SaveChangesAsync so the original atomicity units hold.
/// </summary>
public sealed partial class TasksApi
{
    private static readonly JsonSerializerOptions PlanJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    // ─────────────────────────────────────────────────────────────────────────
    // Reads
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<TaskSnapshot?> GetTaskSnapshotAsync(Ulid taskId, CancellationToken ct = default)
    {
        var task = await _db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId, ct);
        return task is null ? null : ToSnapshot(task, await AutopilotAnswersEveryQuestionAsync(task, ct));
    }

    public async Task<IReadOnlyList<TaskAttachmentInfo>> ListTaskAttachmentsAsync(
        Ulid taskId, CancellationToken ct = default)
    {
        var attachments = await _db.TaskAttachments
            .AsNoTracking()
            .Where(a => a.TaskId == taskId)
            .OrderBy(a => a.CreatedAt)
            .Select(a => new { a.FileName, a.FileType, a.FileSize, a.FileUrl })
            .ToListAsync(ct);

        return attachments
            .Select(a => Features.TaskAttachments.AttachmentBlobName.FromUrl(a.FileUrl) is { } blobName
                ? new TaskAttachmentInfo(a.FileName, a.FileType, a.FileSize, blobName)
                : null)
            .Where(a => a is not null)
            .Select(a => a!)
            .ToList();
    }

    public async Task<IReadOnlyList<QueuedTaskInfo>> ListQueuedTasksAsync(CancellationToken ct = default)
        => await _db.Tasks
            .AsNoTracking()
            .Where(t => t.Status == TaskStatus.InQueue && !t.IsArchived && t.ResetRequestedAt == null)
            // An interactive session is driven by a human, never by this sweep. The flag is
            // cleared by CloseInteractiveSessionAsync the moment a session ends, so a finished
            // session's task re-enters this queue like any other — only a LIVE session is
            // excluded here.
            .Where(t => !t.IsInteractive)
            // Roadmap order first, matching ListAutopilotCandidatesAsync: milestone position
            // is the sequence the user asked for, and it must survive into pickup — priority
            // used to sort first here, so one Critical backlog task pre-empted every task of
            // the active milestone. Backlog (no milestone) sorts last, which is what
            // "backlog" means. The milestone join survives re-queues, unlike QueuePosition.
            .OrderBy(t => t.Milestone == null ? 1 : 0)
            .ThenBy(t => t.Milestone!.Position)
            // Ranked, never the raw enum: Priority is stored as a string, so ordering by the
            // property sorts alphabetically and puts Critical LAST.
            .ThenByDescending(Features.Tasks.TaskOrderingQueries.PriorityRank)
            .ThenBy(t => t.QueuePosition)
            .ThenBy(t => t.CreatedAt)
            .Select(t => new QueuedTaskInfo(
                t.Id, t.OrgId, t.ProjectId, t.RepoId, t.RunnerUserId ?? t.CreatedByUserId,
                t.CreatedByUserId, t.CurrentLane, t.SkipPlanning,
                t.AutopilotSessionId != null))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<InteractiveSessionInfo>> ListInteractiveSessionsAwaitingPickupAsync(
        CancellationToken ct = default)
        => await _db.Tasks
            .AsNoTracking()
            .Where(t => t.IsInteractive
                && t.InteractiveSessionStartedAt != null
                && !t.IsArchived
                && t.ResetRequestedAt == null)
            // Oldest session first: a user whose session has been unattended longest is the one
            // currently staring at a chat that is not answering.
            .OrderBy(t => t.InteractiveSessionStartedAt)
            .Select(t => new InteractiveSessionInfo(
                t.Id, t.OrgId, t.ProjectId, t.RepoId,
                t.InteractiveSessionStartedAt!.Value, t.RunnerUserId ?? t.CreatedByUserId,
                t.LeaseOwner, t.LeaseExpiresAt))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<InteractiveUserMessage>> ListUnansweredInteractiveMessagesAsync(
        Ulid taskId, DateTimeOffset? answeredThrough, CancellationToken ct = default)
        => await _db.TaskMessages
            .AsNoTracking()
            .Where(m => m.TaskId == taskId
                && m.Role == "user"
                && (answeredThrough == null || m.CreatedAt > answeredThrough))
            .OrderBy(m => m.CreatedAt)
            .Select(m => new InteractiveUserMessage(m.Content, m.CreatedAt))
            .ToListAsync(ct);

    public async Task SetInteractiveAnsweredThroughAsync(
        Ulid taskId, DateTimeOffset answeredThrough, CancellationToken ct = default)
        => await _db.Tasks
            // Never backwards: a retried or out-of-order turn must not un-answer messages that a
            // later turn already handled.
            .Where(t => t.Id == taskId
                && (t.InteractiveAnsweredThroughAt == null
                    || t.InteractiveAnsweredThroughAt < answeredThrough))
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.InteractiveAnsweredThroughAt, answeredThrough)
                .SetProperty(t => t.UpdatedAt, DateTimeOffset.UtcNow),
                ct);

    public async Task<bool> TrySetGeneratedSessionTitleAsync(
        Ulid taskId, string title, CancellationToken ct = default)
    {
        // Guarded in the WHERE clause, not read-then-write: the user can rename a session from
        // the task page at the same moment the first turn is titling it, and losing that race
        // should mean the generated title is dropped, never that it replaces what they typed.
        var updated = await _db.Tasks
            .Where(t => t.Id == taskId
                && t.IsInteractive
                && t.Title == InteractiveSessionDefaults.UntitledSession)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Title, title)
                .SetProperty(t => t.UpdatedAt, DateTimeOffset.UtcNow),
                ct);

        return updated > 0;
    }

    public async Task CloseInteractiveSessionAsync(Ulid taskId, CancellationToken ct = default)
    {
        var projectId = await _db.Tasks.AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => (Ulid?)t.ProjectId)
            .FirstOrDefaultAsync(ct);

        // IsInteractive is cleared here, at the one place every session ends, because from
        // this moment the task belongs to the ordinary pipeline. Leaving the flag set was the
        // bug that stranded every finished session: the finish path moves the task to
        // CodeReview/InQueue, but the queue sweep excludes interactive tasks — so the review
        // never ran, the task never reached Done, and no retry path could claim it either.
        // (Sessions still only ever start fresh; the false→true direction stays immutable.)
        await _db.Tasks
            .Where(t => t.Id == taskId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.IsInteractive, false)
                .SetProperty(t => t.InteractiveSessionStartedAt, (DateTimeOffset?)null)
                .SetProperty(t => t.InteractiveFinishRequestedAt, (DateTimeOffset?)null)
                // The durable "was a session" marker — IsInteractive no longer survives.
                .SetProperty(t => t.InteractiveSessionEndedAt, DateTimeOffset.UtcNow)
                .SetProperty(t => t.HasAgentWorking, false)
                .SetProperty(t => t.UpdatedAt, DateTimeOffset.UtcNow),
                ct);

        // The session banner is server-rendered off InteractiveSessionStartedAt; without a
        // broadcast it keeps saying "Session live" after the session is gone.
        if (projectId is { } pid)
            await taskNotifier.SendTaskUpdatedAsync(pid, new TaskUpdatedEvent(
                taskId, Title: null, Priority: null, Status: null, CurrentLane: null,
                IsArchived: null, DateTimeOffset.UtcNow));
    }

    /// <summary>How long a task stays parked waiting for its dependency analysis. Generous
    /// next to a single utility-model call, and short enough that a worker dying mid-analysis
    /// costs a delay rather than a stuck task.</summary>
    private static readonly TimeSpan DependencyAnalysisWindow = TimeSpan.FromMinutes(5);

    public async Task<IReadOnlySet<Ulid>> GetTasksWithUnmetDependenciesAsync(
        IReadOnlyCollection<Ulid> taskIds, CancellationToken ct = default)
    {
        var unmet = await _db.TaskDependencies
            .AsNoTracking()
            .Where(d => taskIds.Contains(d.TaskId))
            .Where(Features.Tasks.TaskDependencyQueries.IsUnmet)
            .Select(d => d.TaskId)
            .Distinct()
            .ToListAsync(ct);

        // Milestone-level edges, which the candidate query also gates on. Repeated here
        // because that query only covers SUBMISSION: a task already in the queue when its
        // milestone gained an edge (a hand-submitted task, or an edge added later) would
        // otherwise run straight past it.
        // Selected as the COMPLEMENT of the unblocked set rather than as a negated predicate:
        // the gate is an Expression, and negating it inline (or compiling it) would not
        // translate — EF would throw rather than quietly run it in memory.
        // An autopilot task's PLANNING run may go ahead of its milestone's gate (see
        // MilestoneDependencyQueries.PlanAheadAcrossGate); its build may not.
        var milestoneUnblocked = await _db.Tasks
            .AsNoTracking()
            .Where(t => taskIds.Contains(t.Id))
            .Where(Features.Milestones.MilestoneDependencyQueries.MayBePickedUp)
            .Select(t => t.Id)
            .ToListAsync(ct);

        var milestoneBlocked = taskIds.Except(milestoneUnblocked).ToList();

        // A task whose dependency analysis is still in flight has no edges yet. Calling it
        // ready would let a whole milestone start at once — precisely what the edges exist to
        // prevent — so it is held until the analysis lands or the window lapses.
        var cutoff = DateTimeOffset.UtcNow - DependencyAnalysisWindow;
        var analyzing = await _db.Tasks
            .AsNoTracking()
            .Where(t => taskIds.Contains(t.Id)
                && t.DependencyAnalysisStartedAt != null
                && t.DependencyAnalysisStartedAt > cutoff)
            .Select(t => t.Id)
            .ToListAsync(ct);

        return unmet.Concat(analyzing).Concat(milestoneBlocked).ToHashSet();
    }

    public async Task<IReadOnlySet<Ulid>> GetMilestoneHeldTaskIdsAsync(
        IReadOnlyCollection<Ulid> taskIds, CancellationToken ct = default)
    {
        if (taskIds.Count == 0)
            return new HashSet<Ulid>();

        // The complement of the unblocked set, for the same reason as above: the gate is an
        // Expression and negating it inline does not translate.
        var unblocked = await _db.Tasks
            .AsNoTracking()
            .Where(t => taskIds.Contains(t.Id))
            .Where(Features.Milestones.MilestoneDependencyQueries.MilestoneUnblocked)
            .Select(t => t.Id)
            .ToListAsync(ct);
        var existing = await _db.Tasks
            .AsNoTracking()
            .Where(t => taskIds.Contains(t.Id))
            .Select(t => t.Id)
            .ToListAsync(ct);

        return existing.Except(unblocked).ToHashSet();
    }

    public async Task<bool> IsTaskMilestoneHeldAsync(Ulid taskId, CancellationToken ct = default)
        => (await GetMilestoneHeldTaskIdsAsync([taskId], ct)).Count > 0;

    public async Task<IReadOnlyList<ActiveTaskInfo>> ListActiveTasksAsync(
        Ulid projectId, CancellationToken ct = default)
        => await _db.Tasks
            .AsNoTracking()
            .Where(t => t.ProjectId == projectId && t.Status == TaskStatus.Active && !t.IsArchived)
            // Interactive sessions are excluded from concurrency accounting entirely.
            //
            // MaxParallelTasks budgets how much AUTONOMOUS work a project runs at once. A session
            // is human-paced and mostly idle — it spends its life waiting for someone to type —
            // so counting it consumes capacity nobody is using. On the common setting of 1 it is
            // worse than wasteful: opening a session would stop the pipeline dead for as long as
            // the session stayed open.
            //
            // This also removes the session's repo from the same-repo conflict set, deliberately.
            // Leaving it in would make the exclusion pointless for single-repo projects, which
            // are most of them. The two branches can conflict at merge like any other pair, and
            // MergeResolution already handles that.
            .Where(t => !t.IsInteractive)
            .Select(t => new ActiveTaskInfo(
                t.Id, t.RepoId, t.CurrentLane, t.SkipPlanning, t.AutopilotSessionId != null))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ResumableTaskInfo>> ListResumableTasksAsync(
        IReadOnlyCollection<Lane> lanes, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        return await _db.Tasks
            .AsNoTracking()
            .Where(t => t.HasAgentWorking
                && t.Status == TaskStatus.Active
                && lanes.Contains(t.CurrentLane)
                && !t.IsArchived
                && t.ResetRequestedAt == null
                && (t.LeaseExpiresAt == null || t.LeaseExpiresAt < now))
            .Select(t => new ResumableTaskInfo(t.Id, t.CurrentLane))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<PrCandidateTask>> ListTasksAwaitingPrAsync(CancellationToken ct = default)
        => await _db.Tasks
            .AsNoTracking()
            .Where(t => (t.CurrentLane == Lane.CodeReview || t.CurrentLane == Lane.Done)
                && t.PrUrl == null
                && t.BranchName != null
                && t.RepoId != null
                && !t.IsArchived
                && t.ResetRequestedAt == null)
            .Select(t => new PrCandidateTask(
                t.Id, t.OrgId, t.ProjectId, t.RepoId, t.BranchName, t.Title, t.Description, t.Type,
                t.MilestoneId, t.BaseBranch))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<MergeCandidateTask>> ListTasksAwaitingMergeAsync(CancellationToken ct = default)
        => await _db.Tasks
            .AsNoTracking()
            // A pending resolution strategy is itself a merge intent (a user's merge
            // click that hit conflicts arms it, from any lane) — include those tasks
            // even before they reach Done, or the resolver would never run for them.
            // CompletedAt means the task reached Done at some point, so review is behind it
            // — it is set on the way in and cleared only when a human moves the task back out
            // through the board. That is what keeps a task MergeFailureLane demoted to code
            // review in the sweep: without it, demoting a task would also end the retries that
            // are the only way it ever merges.
            .Where(t => (t.CurrentLane == Lane.Done
                    || t.MergeResolutionStrategy != null
                    || t.CompletedAt != null)
                && t.PrUrl != null
                && !t.IsMerged
                && t.RepoId != null
                && !t.IsArchived
                // A PR closed without merging is never going to merge. Watching it forever
                // would re-hit the GitHub API every cycle for an answer that cannot change.
                && t.PrClosedAt == null
                // Never merge a PR the user is discarding — reset must win this race.
                && t.ResetRequestedAt == null)
            .OrderBy(t => t.CompletedAt ?? t.UpdatedAt) // Oldest completed first
            .Select(t => new MergeCandidateTask(
                t.Id, t.OrgId, t.ProjectId, t.RepoId, t.PrUrl, t.MergeResolutionStrategy, t.GroupId,
                true, t.BaseBranch, t.CurrentLane, t.Status,
                t.MergeFailureReason != null, t.MergeFailureKind))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<MergeCandidateTask>> ListTasksForForgeReconciliationAsync(
        CancellationToken ct = default)
        => await _db.Tasks
            .AsNoTracking()
            // Exactly the open-PR tasks ListTasksAwaitingMergeAsync does NOT watch: still
            // mid-pipeline (not Done, never completed, no armed resolution). Their PR can
            // still merge or close directly on the forge, and when the webhook misses that,
            // nothing else would ever notice — the task sat in CodeReview forever while its
            // dependents waited on a merge that had already happened.
            .Where(t => t.CurrentLane != Lane.Done
                && t.CompletedAt == null
                && t.MergeResolutionStrategy == null
                && t.PrUrl != null
                && !t.IsMerged
                && t.RepoId != null
                && !t.IsArchived
                && t.PrClosedAt == null
                && t.ResetRequestedAt == null)
            .Select(t => new MergeCandidateTask(
                t.Id, t.OrgId, t.ProjectId, t.RepoId, t.PrUrl, null, t.GroupId, true, t.BaseBranch))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<MergeCandidateTask>> ListRecentlyMergedTasksAsync(
        DateTimeOffset since, CancellationToken ct = default)
        => await _db.Tasks
            .AsNoTracking()
            .Where(t => t.IsMerged
                && t.RepoId != null
                && (t.CompletedAt ?? t.UpdatedAt) >= since)
            .Select(t => new MergeCandidateTask(
                t.Id, t.OrgId, t.ProjectId, t.RepoId, t.PrUrl,
                null, null,
                // Mirrors MarkTaskMergedAsync: a task under a Branch milestone that HAS an
                // integration branch merged into that branch, so the default branch never moved
                // and there is nothing here for a deployment watch to wait on. A Trunk milestone
                // never has a branch, but the mode is checked explicitly so the invariant is
                // stated rather than incidental.
                t.MilestoneId == null
                    || !_db.Milestones.Any(m => m.Id == t.MilestoneId
                        && m.IntegrationMode == MilestoneIntegrationMode.Branch
                        && m.BranchName != null)))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ShippedMilestoneInfo>> ListRecentlyShippedMilestonesAsync(
        DateTimeOffset since, CancellationToken ct = default)
        => await _db.Milestones
            .AsNoTracking()
            // Trunk milestones read Shipped the moment they complete, but every task under them
            // already deployed on its own merge — a milestone-level deploy here would rebuild an
            // unchanged default branch.
            .Where(m => m.ShipState == MilestoneShipState.Shipped
                && m.IntegrationMode == MilestoneIntegrationMode.Branch
                && m.ShippedAt != null
                && m.ShippedAt >= since)
            .Select(m => new ShippedMilestoneInfo(
                m.Id, m.OrgId, m.ProjectId, m.Title, m.ShippedAt!.Value))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ConversationMessage>> GetConversationMessagesAsync(
        Ulid taskId, DateTimeOffset? since = null, CancellationToken ct = default,
        DateTimeOffset? until = null)
        => await _db.TaskMessages
            .AsNoTracking()
            .Where(m => m.TaskId == taskId)
            .Where(m => m.Role != "agent" || m.MessageType == MessageType.Question)
            .Where(m => since == null || m.CreatedAt > since)
            .Where(m => until == null || m.CreatedAt < until)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new ConversationMessage(m.Role, m.MessageType, m.Content))
            .ToListAsync(ct);

    public async Task<ExecutionSessionInfo?> GetLatestExecutionSessionAsync(
        Ulid taskId, Lane lane, CancellationToken ct = default)
        => await _db.TaskExecutions
            .AsNoTracking()
            .Where(e => e.TaskId == taskId
                && e.Lane == lane
                // Failed counts: a run cut off mid-flight is the one whose transcript is
                // most worth resuming. Running is excluded — that session is still live.
                && (e.Status == ExecutionStatus.Completed || e.Status == ExecutionStatus.Failed)
                && e.SessionId != null
                && e.CompletedAt != null)
            .OrderByDescending(e => e.CompletedAt)
            .Select(e => new ExecutionSessionInfo(
                e.SessionId!, e.CompletedAt!.Value, e.Status == ExecutionStatus.Failed,
                e.StartedAt))
            .FirstOrDefaultAsync(ct);

    public async Task<ApprovedPlanInfo?> GetLatestApprovedPlanAsync(
        Ulid taskId, CancellationToken ct = default)
    {
        var content = await _db.TaskPlans
            .AsNoTracking()
            .Where(p => p.TaskId == taskId && p.Status == PlanStatus.Approved)
            .OrderByDescending(p => p.Version)
            .Select(p => p.Content)
            .FirstOrDefaultAsync(ct);

        return content is null
            ? null
            : new ApprovedPlanInfo(content.Summary, JsonSerializer.Serialize(content, PlanJsonOptions));
    }

    public async Task<Ulid?> FindTaskIdByBranchAsync(
        string branchName, IReadOnlyCollection<Ulid> repoIds, CancellationToken ct = default)
    {
        if (repoIds.Count == 0) return null;

        // The repo check happens in memory, not in SQL: RepoId is a converted Ulid? column,
        // and a collection parameter over that converter has thrown at query build before
        // (for plain and nullable element types alike). A branch name matches a
        // handful of tasks at most, so the narrow projection costs nothing.

        // The ULID convention names the task directly, but the id still has to be verified
        // against the delivering repos: branch names are chosen by whoever pushes to the
        // repo, so an unscoped parse would let a crafted branch in one repo complete an
        // unrelated org's task. Reset branches (…-r{n}) fail the parse and fall through to
        // the persisted-name lookup, which already matches them.
        const string prefix = "orkka/task-";
        if (branchName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && Ulid.TryParse(branchName[prefix.Length..], out var parsed))
        {
            var byId = await _db.Tasks
                .AsNoTracking()
                .Where(t => t.Id == parsed && t.RepoId != null)
                .Select(t => new { t.Id, t.RepoId })
                .FirstOrDefaultAsync(ct);
            return byId is not null && byId.RepoId is { } byIdRepo && repoIds.Contains(byIdRepo)
                ? byId.Id
                : null;
        }

        // A Story's members share one branch: the member still in flight (not landed, not
        // archived), earliest in story order, is the one the branch belongs to right now.
        // Everything else keeps the newest-first rule.
        var candidates = await _db.Tasks
            .AsNoTracking()
            .Where(t => t.BranchName == branchName && t.RepoId != null)
            .OrderBy(t => t.IsMerged || t.IsArchived)
            .ThenBy(t => t.StoryOrder ?? 0)
            .ThenByDescending(t => t.CreatedAt)
            .Select(t => new { t.Id, t.RepoId })
            .Take(32)
            .ToListAsync(ct);
        return candidates
            .FirstOrDefault(c => c.RepoId is { } repo && repoIds.Contains(repo))?.Id;
    }

    public async Task SetTaskMergeConflictsAsync(
        Ulid taskId, string conflictsJson, CancellationToken ct = default)
    {
        var task = await _db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null || task.IsMerged) return;

        // A pending human choice rides inside the snapshot — don't clobber it.
        if (task.MergeResolutionStrategy is not null) return;
        if (task.MergeConflictsJson == conflictsJson) return;

        task.MergeConflictsJson = conflictsJson;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<string?> ClearTaskMergeResolutionAsync(Ulid taskId, CancellationToken ct = default)
    {
        var task = await _db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return null;

        // Called at the end of every attempt, including the automatic pass that armed no
        // strategy — so nothing-to-do is the common case and must not cost a write.
        if (task.MergeResolutionStrategy is null
            && task.MergeResolutionGuidance is null
            && task.MergeResolutionStartedAt is null
            && task.QueuedMergeStrategy is null)
            return null;

        // A follow-up the user queued mid-run takes over now. Not when the task merged: the
        // conflict it was meant to steer no longer exists, and running it would resolve
        // nothing at the cost of a full agent run.
        var promoted = !task.IsMerged ? task.QueuedMergeStrategy : null;
        if (promoted is not null)
        {
            task.MergeResolutionStrategy = promoted;
            task.MergeResolutionGuidance = task.QueuedMergeGuidance;
            task.MergeResolutionStartedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            task.MergeResolutionStrategy = null;
            task.MergeResolutionGuidance = null;
            task.MergeResolutionStartedAt = null;
        }
        task.QueuedMergeStrategy = null;
        task.QueuedMergeGuidance = null;

        await _db.SaveChangesAsync(ct);
        return promoted;
    }

    public async Task MarkMergeResolutionStartedAsync(Ulid taskId, CancellationToken ct = default)
    {
        var task = await _db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null || task.MergeResolutionStartedAt is not null) return;

        task.MergeResolutionStartedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Queue claiming / marking
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<bool> TryClaimTaskAsync(
        Ulid taskId, string leaseOwner, TimeSpan leaseDuration, CancellationToken ct = default)
    {
        // Use optimistic concurrency — only update if still InQueue
        var now = DateTimeOffset.UtcNow;
        var rows = await _db.Tasks
            .Where(t => t.Id == taskId && t.Status == TaskStatus.InQueue && t.ResetRequestedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, TaskStatus.Active)
                .SetProperty(t => t.HasAgentWorking, true)
                .SetProperty(t => t.LeaseOwner, leaseOwner)
                .SetProperty(t => t.LeaseExpiresAt, now + leaseDuration)
                .SetProperty(t => t.UpdatedAt, now),
                ct);

        return rows > 0;
    }

    public Task<bool> TryAutoSubmitAsync(Ulid taskId, Ulid sessionId, CancellationToken ct = default)
        => _submissions.TryAutoSubmitAsync(taskId, sessionId, ct);

    public async Task<bool> TryAcquireTaskLeaseAsync(
        Ulid taskId, string leaseOwner, TimeSpan leaseDuration, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = await _db.Tasks
            .Where(t => t.Id == taskId
                && (t.LeaseOwner == null || t.LeaseOwner == leaseOwner || t.LeaseExpiresAt < now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.LeaseOwner, leaseOwner)
                .SetProperty(t => t.LeaseExpiresAt, now + leaseDuration),
                ct);

        return rows > 0;
    }

    public async Task ExtendTaskLeasesAsync(
        IReadOnlyCollection<Ulid> taskIds, string leaseOwner, TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await _db.Tasks
            .Where(t => taskIds.Contains(t.Id) && t.LeaseOwner == leaseOwner)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.LeaseExpiresAt, now + leaseDuration), ct);
    }

    public async Task ReleaseTaskLeaseAsync(
        Ulid taskId, string leaseOwner, CancellationToken ct = default)
        => await _db.Tasks
            .Where(t => t.Id == taskId && t.LeaseOwner == leaseOwner)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.LeaseOwner, (string?)null)
                .SetProperty(t => t.LeaseExpiresAt, (DateTimeOffset?)null), ct);

    public async Task MarkTasksOutOfTokensAsync(
        IReadOnlyCollection<Ulid> taskIds, CancellationToken ct = default)
    {
        // ExecuteUpdate bypasses the change tracker, so TasksDbContext's save interception
        // never sees this transition — capture the affected rows and publish manually.
        var affected = await _db.Tasks.AsNoTracking()
            .Where(t => taskIds.Contains(t.Id) && t.Status == TaskStatus.InQueue)
            .Select(t => new { t.Id, t.OrgId, t.ProjectId, t.Number, t.Title, t.CreatedByUserId })
            .ToListAsync(ct);

        await _db.Tasks
            .Where(t => taskIds.Contains(t.Id) && t.Status == TaskStatus.InQueue)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TaskStatus.OutOfTokens), ct);

        foreach (var t in affected)
        {
            await _eventBus.PublishAsync(new Orkka.SharedKernel.Events.Catalog.TaskStatusChanged(
                t.Id, t.OrgId, t.ProjectId, t.Number, t.Title, t.CreatedByUserId,
                TaskStatus.OutOfTokens.ToString(), ActorUserId: null), ct);
            // The bus event above feeds the bell notification, not the board — the card
            // needs its own broadcast to leave the queue visually.
            await taskNotifier.SendStatusChangedAsync(t.ProjectId, new StatusChangedEvent(
                t.Id, TaskStatus.OutOfTokens.ToString(), DateTimeOffset.UtcNow));
        }
    }

    public async Task MarkTasksWaitingForRunnerAsync(
        IReadOnlyCollection<Ulid> taskIds, CancellationToken ct = default)
    {
        // Mirror of MarkTasksOutOfTokensAsync: ExecuteUpdate bypasses save interception,
        // so publish TaskStatusChanged manually per affected row.
        var affected = await _db.Tasks.AsNoTracking()
            .Where(t => taskIds.Contains(t.Id) && t.Status == TaskStatus.InQueue)
            .Select(t => new { t.Id, t.OrgId, t.ProjectId, t.Number, t.Title, t.CreatedByUserId })
            .ToListAsync(ct);

        // UpdatedAt is stamped so "how long has this been parked" is answerable. Nothing else
        // touches a task while it waits for a runner, which makes it a reliable parked-since
        // clock.
        var parkedAt = DateTimeOffset.UtcNow;
        await _db.Tasks
            .Where(t => taskIds.Contains(t.Id) && t.Status == TaskStatus.InQueue)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, TaskStatus.WaitingForRunner)
                .SetProperty(t => t.UpdatedAt, parkedAt), ct);

        foreach (var t in affected)
        {
            await _eventBus.PublishAsync(new Orkka.SharedKernel.Events.Catalog.TaskStatusChanged(
                t.Id, t.OrgId, t.ProjectId, t.Number, t.Title, t.CreatedByUserId,
                TaskStatus.WaitingForRunner.ToString(), ActorUserId: null), ct);
            // Same as MarkTasksOutOfTokensAsync: the bus event is the bell, this is the board.
            await taskNotifier.SendStatusChangedAsync(t.ProjectId, new StatusChangedEvent(
                t.Id, TaskStatus.WaitingForRunner.ToString(), DateTimeOffset.UtcNow));
        }
    }

    public async Task<IReadOnlyList<AutoRecoverableBlockedTask>> ListAutoRecoverableBlockedTasksAsync(
        DateTimeOffset failedBefore, int maxRounds, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        return await _db.Tasks
            .AsNoTracking()
            .Where(t => t.Status == TaskStatus.Blocked
                && !t.IsArchived
                && t.ResetRequestedAt == null
                && !t.IsInteractive
                && t.AutoRecoveryCount < maxRounds
                // A live lease means a run is still winding down somewhere — whatever state
                // it leaves behind is the one to classify, not this snapshot of it.
                && (t.LeaseOwner == null || t.LeaseExpiresAt == null || t.LeaseExpiresAt <= now))
            .Select(t => new
            {
                t.Id, t.OrgId, t.ProjectId, t.Title, t.AutoRecoveryCount,
                // The newest failed run is the failure the task is blocked ON. Older errors
                // may be a different story entirely (a real defect the human already retried
                // past), so only this one is offered for classification. Two scalar
                // subqueries, not one row-valued one: SQLite has no lateral join.
                LastFailureAt = t.Executions
                    .Where(e => e.Status == ExecutionStatus.Failed && e.CompletedAt != null)
                    .Max(e => e.CompletedAt),
                LastError = t.Executions
                    .Where(e => e.Status == ExecutionStatus.Failed && e.CompletedAt != null)
                    .OrderByDescending(e => e.CompletedAt)
                    .Select(e => e.ErrorMessage)
                    .FirstOrDefault(),
            })
            .Where(x => x.LastFailureAt != null && x.LastFailureAt <= failedBefore)
            .OrderBy(x => x.Id)
            .Select(x => new AutoRecoverableBlockedTask(
                x.Id, x.OrgId, x.ProjectId, x.Title, x.AutoRecoveryCount,
                x.LastError))
            .ToListAsync(ct);
    }

    public async Task<bool> TryAutoRecoverBlockedTaskAsync(
        Ulid taskId, int maxRounds, CancellationToken ct = default)
    {
        var task = await _db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null
            || task.Status != TaskStatus.Blocked
            || task.IsArchived
            || task.ResetRequestedAt != null
            || task.IsInteractive
            || task.AutoRecoveryCount >= maxRounds)
            return false;

        // The same reset the Retry endpoint performs: back to InQueue in the current lane.
        // The next run resumes the failed execution's CLI session, so no work is lost.
        task.AutoRecoveryCount++;
        task.Status = TaskStatus.InQueue;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> HasCrossRepoDependentsAsync(Ulid taskId, CancellationToken ct = default)
    {
        var repoId = await _db.Tasks.AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => t.RepoId)
            .FirstOrDefaultAsync(ct);

        if (repoId is null)
            return false;

        // Dependents = rows pointing AT this task. A different RepoId is what makes the
        // contract worth writing down: same-repo dependents can read the code directly.
        return await _db.TaskDependencies.AsNoTracking()
            .AnyAsync(d => d.DependsOnTaskId == taskId
                && !d.Task.IsArchived
                && d.Task.RepoId != null
                && d.Task.RepoId != repoId, ct);
    }

    public async Task SetApiSurfaceAsync(
        Ulid taskId, string? apiSurface, CancellationToken ct = default)
        => await _db.Tasks
            .Where(t => t.Id == taskId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ApiSurface, apiSurface), ct);

    public async Task<TaskGroupContract?> GetGroupContractAsync(
        Ulid taskId, CancellationToken ct = default)
    {
        var groupId = await _db.Tasks.AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => t.GroupId)
            .FirstOrDefaultAsync(ct);

        if (groupId is not { } id)
            return null;

        // A Story has no cross-repo contract: its members build on each other's pushed code
        // (see BuildStoryContextSectionAsync), not on an agreed interface.
        var group = await _db.TaskGroups.AsNoTracking()
            .Where(g => g.Id == id && g.Kind != TaskGroupKind.Story)
            .Select(g => new { g.Id, g.Title, g.Contract })
            .FirstOrDefaultAsync(ct);

        if (group is null)
            return null;

        var members = await _db.Tasks.AsNoTracking()
            .Where(t => t.GroupId == id && t.Id != taskId)
            .OrderBy(t => t.Number)
            .Select(t => new { t.Number, t.Title })
            .ToListAsync(ct);

        return new TaskGroupContract(
            group.Id, group.Title, group.Contract,
            members.Select(m => $"#{m.Number} — {m.Title}").ToList());
    }

    public async Task<IReadOnlyList<TaskGroupMergeState>> GetGroupMergeStatesAsync(
        IReadOnlyCollection<Ulid> groupIds, CancellationToken ct = default)
    {
        if (groupIds.Count == 0)
            return [];

        var groups = await _db.TaskGroups.AsNoTracking()
            .Where(g => groupIds.Contains(g.Id))
            .Select(g => new { g.Id, g.ProjectId, g.MergePolicy })
            .ToListAsync(ct);

        if (groups.Count == 0)
            return [];

        // The group-id membership test is done IN MEMORY, after a SQL filter on the groups'
        // projects. Never `ids.Contains(t.GroupId!.Value)` in the query: GroupId is Ulid?,
        // its column converter is the nullable one, and the collection parameter for a Ulid
        // list against it has failed to build ("Expression of type 'Ulid' cannot be
        // used for parameter of type 'Nullable<Ulid>'"); a Ulid? list fails the other way
        // (no JSON collection reader for nullable structs with a converter). That translation
        // threw the moment the first grouped task reached the merge sweep (prod, 2026-09-15),
        // and since the sweep runs before queue pickup in the poll cycle, every project's
        // Ready lane starved. Same shape as the milestone/message lookups elsewhere;
        // GroupMergeStateQueryTests runs this against the real database.
        var ids = groups.Select(g => g.Id).ToHashSet();
        var projectIds = groups.Select(g => g.ProjectId).Distinct().ToList();

        var members = (await _db.Tasks.AsNoTracking()
            .Where(t => projectIds.Contains(t.ProjectId) && t.GroupId != null && !t.IsArchived)
            .Select(t => new
            {
                GroupId = t.GroupId!.Value,
                t.Id,
                t.Number,
                t.Status,
                t.CurrentLane,
                t.PrUrl,
                t.IsMerged,
                t.PrClosedAt,
                t.MergeFailureReason,
                t.MergeFailureKind,
                t.CompletedAt,
            })
            .ToListAsync(ct))
            .Where(m => ids.Contains(m.GroupId))
            .ToList();

        // In-group edges only. An edge to a task outside the group orders when work STARTS and
        // says nothing about landing order here.
        var edges = (await _db.TaskDependencies.AsNoTracking()
            .Where(d => d.Task.GroupId != null
                && projectIds.Contains(d.Task.ProjectId)
                && d.Task.GroupId == d.DependsOnTask.GroupId)
            .Select(d => new { GroupId = d.Task.GroupId!.Value, d.TaskId, d.DependsOnTaskId })
            .ToListAsync(ct))
            .Where(e => ids.Contains(e.GroupId))
            .Select(e => new GroupEdge(e.TaskId, e.DependsOnTaskId))
            .ToList();

        var membersByGroup = members.GroupBy(m => m.GroupId)
            .ToDictionary(g => g.Key, g => g.ToList());

        List<TaskGroupMergeState> states = [];

        foreach (var group in groups)
        {
            var groupMembers = membersByGroup.GetValueOrDefault(group.Id) ?? [];

            string? blockedBy = null;

            if (groupMembers.Count == 0)
            {
                // No members left — every one was deleted or archived. Nothing to gate, and
                // reporting "ready" would be meaningless; report blocked so nothing lands.
                blockedBy = "This group has no tasks left.";
            }
            else
            {
                // A member a merge-failure notice demoted out of Done (CompletedAt set, a
                // failure recorded) is FINISHED — it belongs to the stuck list below, not here.
                // Counting it as unfinished made the gate wait on the very task the resolver
                // had just fixed (prod, 2026-09-16: "Waiting on #18 (CodeReview)" for #18).
                var unfinished = groupMembers
                    .Where(m => m.Status != TaskStatus.Completed && m.CurrentLane != Lane.Done
                        && !(m.MergeFailureReason != null && m.CompletedAt != null))
                    .ToList();

                if (unfinished.Count > 0)
                {
                    blockedBy = "Waiting on "
                        + string.Join(", ", unfinished
                            .OrderBy(m => m.Number)
                            .Select(m => $"#{m.Number} ({m.CurrentLane})"));
                }
                else
                {
                    // Finished, but not landable: a PR that was closed without merging is never
                    // going to land, and a TERMINAL merge failure needs a human. Either way the
                    // group must not ship its other halves and call itself done. A failure still
                    // on the retry path (Unknown, EnvironmentBlocked — "runner not connected" at
                    // the one attempt made before it reconnected, prod 2026-09-16) is not stuck:
                    // the sweep retries it every cycle, and calling the group stuck for it held
                    // a member whose merge would have succeeded on the very next try.
                    var stuck = groupMembers
                        .Where(m => !m.IsMerged
                            && (m.PrClosedAt != null || MergeFailureKinds.IsTerminal(m.MergeFailureKind)))
                        .ToList();

                    if (stuck.Count > 0)
                    {
                        blockedBy = "Cannot merge "
                            + string.Join(", ", stuck.OrderBy(m => m.Number).Select(m => $"#{m.Number}"));
                    }
                }
            }

            states.Add(new TaskGroupMergeState(
                group.Id,
                group.MergePolicy,
                Ready: blockedBy is null,
                MergeOrder: OrderForMerge(groupMembers.Select(m => m.Id).ToList(), edges),
                BlockedBy: blockedBy));
        }

        return states;
    }

    /// <summary>
    /// Topological-ish ordering by blocker depth: a member with no in-group blockers first, then
    /// whatever depends on it. Ties break on the task id so the order is stable across cycles.
    ///
    /// Depth is computed iteratively and capped by the member count, so a cycle — which the
    /// split cannot produce, but a human adding edges by hand can — degrades to an arbitrary
    /// but stable order instead of looping forever.
    /// </summary>
    private sealed record GroupEdge(Ulid TaskId, Ulid DependsOnTaskId);

    private static List<Ulid> OrderForMerge(List<Ulid> memberIds, IEnumerable<GroupEdge> edges)
    {
        var blockers = memberIds.ToDictionary(id => id, _ => new List<Ulid>());
        foreach (var edge in edges)
        {
            if (blockers.TryGetValue(edge.TaskId, out var list)
                && blockers.ContainsKey(edge.DependsOnTaskId))
                list.Add(edge.DependsOnTaskId);
        }

        var depth = memberIds.ToDictionary(id => id, _ => 0);
        for (var pass = 0; pass < memberIds.Count; pass++)
        {
            var changed = false;
            foreach (var id in memberIds)
            {
                var candidate = blockers[id].Count == 0 ? 0 : blockers[id].Max(b => depth[b]) + 1;
                if (candidate > depth[id])
                {
                    depth[id] = candidate;
                    changed = true;
                }
            }
            if (!changed)
                break;
        }

        return memberIds.OrderBy(id => depth[id]).ThenBy(id => id).ToList();
    }

    public async Task RefreshGroupMergeStatusAsync(Ulid groupId, CancellationToken ct = default)
    {
        var group = await _db.TaskGroups.FirstOrDefaultAsync(g => g.Id == groupId, ct);
        // A Story's members land on its shared branch one by one; the story settles when its
        // forge pull requests merge (or it lands in a folder) — never from member merges.
        if (group is null || group.Kind == TaskGroupKind.Story)
            return;

        var members = await _db.Tasks.AsNoTracking()
            .Where(t => t.GroupId == groupId && !t.IsArchived)
            .Select(t => new { t.IsMerged, t.PrClosedAt, t.MergeFailureKind })
            .ToListAsync(ct);

        if (members.Count == 0)
            return;

        var merged = members.Count(m => m.IsMerged);
        if (merged == 0)
            return;

        // Same rule as the gate: only a terminal failure makes a half-landed group stuck.
        var stuck = members.Any(m => !m.IsMerged
            && (m.PrClosedAt != null || MergeFailureKinds.IsTerminal(m.MergeFailureKind)));

        group.Status = merged == members.Count
            ? TaskGroupStatus.Merged
            : stuck
                ? TaskGroupStatus.PartiallyMerged
                : TaskGroupStatus.AwaitingMerge;

        if (group.Status is TaskGroupStatus.Merged or TaskGroupStatus.PartiallyMerged)
            group.SettledAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
    }

    public async Task<string> RequestGroupRepoAsync(
        Ulid taskId, string repoName, string reason, CancellationToken ct = default)
    {
        var name = repoName.Trim();
        if (name.Length == 0)
            return "A repository name is required.";

        var task = await _db.Tasks.AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => new { t.GroupId, t.RepoId })
            .FirstOrDefaultAsync(ct);

        if (task?.GroupId is not { } groupId)
            return "This task is not part of a cross-repo task, so there is no group to add a "
                + "repository to. Ask the human to create the work as a separate task instead.";

        var group = await _db.TaskGroups.FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group is null)
            return "This task's cross-repo group no longer exists.";

        var updated = RepoRequests.TryAdd(group.RepoRequestsJson, new RepoRequest(
            name, reason.Trim(), taskId.ToString(), DateTimeOffset.UtcNow, RepoRequests.Pending));

        if (updated is null)
            return $"A request for \"{name}\" is already open on this cross-repo task — a human "
                + "will decide. Carry on with what you can do in your own repository.";

        group.RepoRequestsJson = updated;
        await _db.SaveChangesAsync(ct);

        return $"Recorded a request to add \"{name}\" to this cross-repo task. A human has to "
            + "approve it before any work is filed there, so do NOT try to change that "
            + "repository yourself — it is not checked out here. Carry on with your own "
            + "repository's half, and if you are genuinely blocked without it, end your run "
            + "with a <question> saying so.";
    }

    public async Task AppendGroupAsBuiltAsync(
        Ulid groupId, int taskNumber, string taskTitle, string apiSurface,
        CancellationToken ct = default)
    {
        var group = await _db.TaskGroups.FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group is null)
            return;

        var heading = $"### As built — #{taskNumber} {taskTitle}";

        // Idempotent: a re-entered Developing phase publishes its surface again, and a contract
        // that grew a duplicate section every review bounce would push the real one out of the
        // prompt budget.
        if (group.Contract?.Contains(heading, StringComparison.Ordinal) == true)
            return;

        // Appended, never substituted. The agreed text is what the OTHER members were given and
        // may already have built against; replacing it would silently move the target under them.
        // Recording both is what makes a divergence visible instead of authoritative.
        group.Contract = string.IsNullOrWhiteSpace(group.Contract)
            ? $"{heading}\n\n{apiSurface}"
            : $"{group.Contract}\n\n{heading}\n\n{apiSurface}";

        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<BlockerApiContract>> ListBlockerApiContractsAsync(
        Ulid taskId, CancellationToken ct = default)
    {
        var repoId = await _db.Tasks.AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => t.RepoId)
            .FirstOrDefaultAsync(ct);

        return await _db.TaskDependencies.AsNoTracking()
            .Where(d => d.TaskId == taskId
                && d.DependsOnTask.ApiSurface != null
                // Other repos only — a same-repo blocker's code is already on this task's disk.
                && d.DependsOnTask.RepoId != null
                && d.DependsOnTask.RepoId != repoId)
            .OrderBy(d => d.DependsOnTask.Number)
            .Select(d => new BlockerApiContract(
                d.DependsOnTask.Id, d.DependsOnTask.Number, d.DependsOnTask.Title,
                d.DependsOnTask.RepoId, d.DependsOnTask.ApiSurface!))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<DocSyncCandidateTask>> ListTasksAwaitingDocSyncAsync(
        DateTimeOffset completedSince, int maxAttempts, DateTimeOffset runningStaleBefore,
        CancellationToken ct = default)
        => await _db.Tasks
            .AsNoTracking()
            .Where(t => t.CurrentLane == Lane.Done
                && t.Status == TaskStatus.Completed
                // Archived is deliberately NOT excluded, unlike the PR and resume sweeps.
                // Archiving is a board-tidying gesture, and dragging a task to Done archives it
                // outright — filtering on it would silently skip every manually-completed task
                // and every finished one somebody cleaned off the board.
                && t.ResetRequestedAt == null
                // Repo work is documented only once it has actually landed. A repo-less task
                // (research, a chat-created write-up) has nothing to merge, so Done is settled.
                && (t.RepoId == null || t.IsMerged)
                && t.DocSyncAttempts < maxAttempts
                && (t.DocSyncStatus == DocSyncStatus.None
                    || t.DocSyncStatus == DocSyncStatus.Failed
                    || (t.DocSyncStatus == DocSyncStatus.Running && t.UpdatedAt < runningStaleBefore))
                && t.CompletedAt >= completedSince)
            .OrderBy(t => t.CompletedAt)
            .Select(t => new DocSyncCandidateTask(
                t.Id, t.OrgId, t.ProjectId, t.Title, t.CreatedByUserId))
            .ToListAsync(ct);

    public async Task<bool> TryClaimDocSyncAsync(
        Ulid taskId, DateTimeOffset runningStaleBefore, CancellationToken ct = default)
    {
        // The listing races other replicas; this update is the authoritative claim — exactly
        // one matches. Attempts increments here rather than on completion so a run that dies
        // without ever reporting back still counts against the retry cap.
        var rows = await _db.Tasks
            .Where(t => t.Id == taskId
                && (t.DocSyncStatus == DocSyncStatus.None
                    || t.DocSyncStatus == DocSyncStatus.Failed
                    || (t.DocSyncStatus == DocSyncStatus.Running && t.UpdatedAt < runningStaleBefore)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.DocSyncStatus, DocSyncStatus.Running)
                .SetProperty(t => t.DocSyncAttempts, t => t.DocSyncAttempts + 1)
                .SetProperty(t => t.UpdatedAt, DateTimeOffset.UtcNow),
                ct);

        return rows > 0;
    }

    public async Task<TaskChangeSummary?> GetTaskChangeSummaryAsync(
        Ulid taskId, CancellationToken ct = default)
    {
        var snapshots = await _db.CodeChangeSnapshots
            .AsNoTracking()
            .Where(s => s.TaskId == taskId)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new { s.FilesJson, s.FileCount, s.Additions, s.Deletions })
            .ToListAsync(ct);

        if (snapshots.Count == 0)
            return null;

        // Paths come from every cycle: a task that bounced Developing → Review → Developing
        // touched files in the earlier round that the newest snapshot's diff no longer shows,
        // and those are exactly the ones a stale document is most likely to describe.
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
        {
            foreach (var path in ParseSnapshotPaths(snapshot.FilesJson))
            {
                if (seen.Add(path))
                    paths.Add(path);
            }
        }

        var newest = snapshots[0];
        return new TaskChangeSummary(paths, newest.FileCount, newest.Additions, newest.Deletions);
    }

    /// <summary>Paths out of a snapshot's JSON file list. Malformed or absent JSON yields
    /// nothing — a documentation pass without file paths is degraded, never broken.</summary>
    private static IEnumerable<string> ParseSnapshotPaths(string? filesJson)
    {
        if (string.IsNullOrWhiteSpace(filesJson))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(filesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            return [.. doc.RootElement.EnumerateArray()
                .Select(e => e.TryGetProperty("path", out var p) ? p.GetString() : null)
                .OfType<string>()];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async Task CompleteDocSyncAsync(
        Ulid taskId, DocSyncStatus status, CancellationToken ct = default)
        => await _db.Tasks
            .Where(t => t.Id == taskId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.DocSyncStatus, status)
                .SetProperty(t => t.DocSyncCompletedAt, DateTimeOffset.UtcNow)
                .SetProperty(t => t.UpdatedAt, DateTimeOffset.UtcNow),
                ct);

    public async Task<IReadOnlyList<AutoArchiveCandidateTask>> ListTasksAwaitingAutoArchiveAsync(
        DateTimeOffset completedBefore, int limit, CancellationToken ct = default)
        => await _db.Tasks
            .AsNoTracking()
            .Where(AutoArchiveEligible)
            // CompletedAt is the clock, not UpdatedAt: a merge poller touch, a doc-sync claim
            // or a late timeline message all bump UpdatedAt without the task having left Done,
            // and would keep resetting the two weeks on work nobody looked at again.
            .Where(t => t.CompletedAt < completedBefore)
            .OrderBy(t => t.CompletedAt)
            .Take(limit)
            .Select(t => new AutoArchiveCandidateTask(
                t.Id, t.OrgId, t.ProjectId, t.Number, t.Title, t.CompletedAt!.Value))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<AutoArchiveCandidateTask>> ArchiveTasksAsync(
        IReadOnlyCollection<Ulid> taskIds, CancellationToken ct = default)
    {
        if (taskIds.Count == 0) return [];

        // Tracked rather than ExecuteUpdate: the caller notifies per task, so it needs to know
        // which rows it actually moved. The eligibility predicate is re-applied here, which is
        // what makes a lost race a no-op instead of an archive of work that has been reopened.
        var tasks = await _db.Tasks
            .Where(t => taskIds.Contains(t.Id))
            .Where(AutoArchiveEligible)
            .ToListAsync(ct);

        if (tasks.Count == 0) return [];

        var now = DateTimeOffset.UtcNow;
        foreach (var task in tasks)
        {
            task.IsArchived = true;
            task.ArchivedAt = now;
            task.Status = TaskStatus.Archived;
        }

        await _db.SaveChangesAsync(ct);

        return [.. tasks.Select(t => new AutoArchiveCandidateTask(
            t.Id, t.OrgId, t.ProjectId, t.Number, t.Title, t.CompletedAt!.Value))];
    }

    /// <summary>
    /// A Done task the auto-archive sweep may take, age aside. Shared by the listing and the
    /// write so the two can never disagree about what "settled" means.
    ///
    /// The settled arm is the load-bearing one: <see cref="ListTasksAwaitingMergeAsync"/> skips
    /// archived tasks, so archiving a Done task whose PR has not landed would stop the merge
    /// poller from ever landing it. A closed PR is terminal — that poller has already given up
    /// on it — so those are safe to archive.
    /// </summary>
    private static readonly System.Linq.Expressions.Expression<Func<TaskEntity, bool>> AutoArchiveEligible =
        t => t.CurrentLane == Lane.Done
            && t.Status == TaskStatus.Completed
            && !t.IsArchived
            && t.CompletedAt != null
            && t.ResetRequestedAt == null
            && (t.RepoId == null || t.IsMerged || t.PrClosedAt != null);

    public Task<int> RequeueWaitingForRunnerTasksAsync(
        Ulid orgId, string runnerUserId, CancellationToken ct = default)
        => RequeueParkedTasksAsync(
            t => t.OrgId == orgId && t.RunnerUserId == runnerUserId, ct);

    public Task<int> RequeueAllWaitingForRunnerTasksAsync(CancellationToken ct = default)
        => RequeueParkedTasksAsync(_ => true, ct);

    /// <summary>Shared body of the two unpark sweeps — <paramref name="owner"/> narrows to
    /// one runner identity (hosted) or matches everything (Orkka Local).</summary>
    private async Task<int> RequeueParkedTasksAsync(
        System.Linq.Expressions.Expression<Func<TaskEntity, bool>> owner, CancellationToken ct)
    {
        var affected = await _db.Tasks.AsNoTracking()
            .Where(owner)
            .Where(t => t.Status == TaskStatus.WaitingForRunner && !t.IsArchived)
            .Select(t => new { t.Id, t.OrgId, t.ProjectId, t.Number, t.Title, t.CreatedByUserId })
            .ToListAsync(ct);
        if (affected.Count == 0)
            return 0;

        var ids = affected.Select(t => t.Id).ToList();
        await _db.Tasks
            .Where(t => ids.Contains(t.Id) && t.Status == TaskStatus.WaitingForRunner)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TaskStatus.InQueue), ct);

        foreach (var t in affected)
        {
            await _eventBus.PublishAsync(new Orkka.SharedKernel.Events.Catalog.TaskStatusChanged(
                t.Id, t.OrgId, t.ProjectId, t.Number, t.Title, t.CreatedByUserId,
                TaskStatus.InQueue.ToString(), ActorUserId: null), ct);
        }
        return affected.Count;
    }

    /// <summary>Fails the execution but PARKS the task as WaitingForRunner instead of
    /// terminally blocking it — used when a UserRunner task's runner went away mid-run.
    /// Phase re-entry is the established crash-recovery model, so the reconnect sweep
    /// re-queues the task and the phase simply runs again.</summary>
    public async Task<ExecutionSnapshot?> ParkExecutionWaitingForRunnerAsync(
        Ulid taskId, Ulid executionId, string reason,
        int promptTokens = 0, int completionTokens = 0, string? sessionId = null,
        CancellationToken ct = default)
    {
        var task = await _db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        var execution = await _db.TaskExecutions.FirstOrDefaultAsync(e => e.Id == executionId, ct);
        if (task is null || execution is null) return null;

        execution.Status = ExecutionStatus.Failed;
        execution.ErrorMessage = reason;
        execution.CompletedAt = DateTimeOffset.UtcNow;
        // The runner went away, not the session: keep its id so the reconnect sweep
        // resumes that transcript instead of re-running the phase from scratch.
        if (!string.IsNullOrWhiteSpace(sessionId))
            execution.SessionId = sessionId;

        if (promptTokens > 0 || completionTokens > 0)
        {
            execution.PromptTokens = promptTokens;
            execution.CompletionTokens = completionTokens;
            execution.TokensUsed = promptTokens + completionTokens;
            task.TotalTokensUsed += execution.TokensUsed;
        }

        task.Status = TaskStatus.WaitingForRunner;
        task.HasAgentWorking = false;

        await _db.SaveChangesAsync(ct);
        return ToSnapshot(execution);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Executions & phase transitions
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<ExecutionStart?> StartExecutionAsync(
        Ulid taskId, Lane lane, Ulid? agentId,
        bool moveTaskToLane, bool setStartedAt, CancellationToken ct = default)
    {
        var task = await _db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return null;

        var fromLane = task.CurrentLane;
        if (moveTaskToLane)
            task.CurrentLane = lane;
        task.Status = TaskStatus.Active;
        if (setStartedAt)
            task.StartedAt ??= DateTimeOffset.UtcNow;
        task.CurrentAgentId = agentId;

        // A leftover Running execution here means a previous run never reached a
        // terminal state (worker crash/restart) — close it so history stays truthful.
        var staleExecutions = await _db.TaskExecutions
            .Where(e => e.TaskId == taskId && e.Status == ExecutionStatus.Running)
            .ToListAsync(ct);
        foreach (var stale in staleExecutions)
        {
            stale.Status = ExecutionStatus.Failed;
            stale.ErrorMessage = "Interrupted — superseded by a new run (worker restart or retry).";
            stale.CompletedAt = DateTimeOffset.UtcNow;
        }

        var execution = new TaskExecution
        {
            TaskId = task.Id,
            OrgId = task.OrgId,
            AgentId = agentId,
            Lane = lane,
            Status = ExecutionStatus.Running,
            StartedAt = DateTimeOffset.UtcNow
        };
        _db.TaskExecutions.Add(execution);
        await _db.SaveChangesAsync(ct);

        return new ExecutionStart(
            execution.Id, fromLane, ToSnapshot(task, await AutopilotAnswersEveryQuestionAsync(task, ct)));
    }

    public async Task<ExecutionSnapshot?> CompleteExecutionAsync(
        Ulid taskId, Ulid executionId, int promptTokens, int completionTokens,
        string? agentLog = null, string? sessionId = null, CancellationToken ct = default)
    {
        var task = await _db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        var execution = await _db.TaskExecutions.FirstOrDefaultAsync(e => e.Id == executionId, ct);
        if (task is null || execution is null) return null;

        if (agentLog is not null)
            execution.AgentLog = agentLog;
        if (!string.IsNullOrWhiteSpace(sessionId))
            execution.SessionId = sessionId;
        execution.Status = ExecutionStatus.Completed;
        execution.CompletedAt = DateTimeOffset.UtcNow;
        execution.PromptTokens = promptTokens;
        execution.CompletionTokens = completionTokens;
        execution.TokensUsed = promptTokens + completionTokens;

        task.TotalTokensUsed += execution.TokensUsed;

        await _db.SaveChangesAsync(ct);
        return ToSnapshot(execution);
    }

    public async Task SaveCodeChangeSnapshotAsync(
        Ulid taskId, Ulid? executionId, Ulid repoId, Lane lane,
        string branchName, string baseBranch, string diff, bool isTruncated,
        bool onlyIfMissing = false, CancellationToken ct = default)
    {
        var task = await _db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null)
            return;

        if (onlyIfMissing && await _db.CodeChangeSnapshots
                .AnyAsync(s => s.TaskId == taskId && s.BranchName == branchName, ct))
            return;

        var diffHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(diff)));

        // The run is over: its live mirror is superseded by this permanent record, whether or
        // not the record turns out to be a duplicate below.
        await _db.CodeChangeSnapshots
            .Where(s => s.TaskId == taskId && s.IsLive)
            .ExecuteDeleteAsync(ct);

        // A re-entered phase that changed nothing must not duplicate rows.
        var latestHash = await _db.CodeChangeSnapshots
            .Where(s => s.TaskId == taskId && !s.IsLive)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => s.DiffHash)
            .FirstOrDefaultAsync(ct);
        if (diffHash == latestHash)
            return;

        var (additions, deletions, files) = UnifiedDiffStats.Parse(diff);

        _db.CodeChangeSnapshots.Add(new TaskCodeChangeSnapshot
        {
            TaskId = taskId,
            OrgId = task.OrgId,
            ExecutionId = executionId,
            RepoId = repoId,
            Lane = lane,
            BranchName = branchName,
            BaseBranch = baseBranch,
            IsTruncated = isTruncated,
            DiffHash = diffHash,
            FileCount = files.Count,
            Additions = additions,
            Deletions = deletions,
            FilesJson = JsonSerializer.Serialize(
                files.Select(f => new { path = f.Path, status = f.Status, additions = f.Additions, deletions = f.Deletions }))
        });

        // Fill the producing execution's FilesChanged — the evidence checklist and execution
        // history read it, and until now nothing ever wrote it. PlannedFileChange requires all
        // three properties, so description must be present even when empty.
        if (executionId is { } execId)
        {
            var execution = await _db.TaskExecutions.FirstOrDefaultAsync(e => e.Id == execId, ct);
            if (execution is not null)
                execution.FilesChanged = JsonSerializer.Serialize(
                    files.Select(f => new { path = f.Path, action = f.Status, description = "" }));
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task<WorkspaceChangedEvent?> UpsertLiveCodeChangeSnapshotAsync(
        Ulid taskId, Ulid? executionId, Ulid repoId, Lane lane,
        string branchName, string baseBranch, string diff, bool isTruncated,
        CancellationToken ct = default)
    {
        var task = await _db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null)
            return null;

        var diffHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(diff)));

        var live = await _db.CodeChangeSnapshots
            .FirstOrDefaultAsync(s => s.TaskId == taskId && s.IsLive, ct);
        if (live is not null && live.DiffHash == diffHash)
            return null;

        var (additions, deletions, files) = UnifiedDiffStats.Parse(diff);
        var filesJson = JsonSerializer.Serialize(
            files.Select(f => new { path = f.Path, status = f.Status, additions = f.Additions, deletions = f.Deletions }));

        if (live is null)
        {
            live = new TaskCodeChangeSnapshot
            {
                TaskId = taskId,
                OrgId = task.OrgId,
                RepoId = repoId,
                IsLive = true,
                Revision = 0,
            };
            _db.CodeChangeSnapshots.Add(live);
        }

        live.ExecutionId = executionId;
        live.Lane = lane;
        live.BranchName = branchName;
        live.BaseBranch = baseBranch;
        live.IsTruncated = isTruncated;
        live.DiffHash = diffHash;
        live.FileCount = files.Count;
        live.Additions = additions;
        live.Deletions = deletions;
        live.FilesJson = filesJson;
        live.Revision += 1;

        await _db.SaveChangesAsync(ct);

        return new WorkspaceChangedEvent(
            taskId, live.Id, live.Revision, branchName, baseBranch,
            files.Count, additions, deletions, isTruncated,
            files.Select(f => new WorkspaceChangedFile(f.Path, f.Status, f.Additions, f.Deletions)).ToList(),
            DateTimeOffset.UtcNow);
    }

    public async Task<PlanSaveResult?> SavePlanAndCompleteExecutionAsync(
        Ulid taskId, Ulid executionId, int promptTokens, int completionTokens,
        string planContentJson, CancellationToken ct = default)
    {
        var task = await _db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        var execution = await _db.TaskExecutions.FirstOrDefaultAsync(e => e.Id == executionId, ct);
        if (task is null || execution is null) return null;

        var content = JsonSerializer.Deserialize<PlanContent>(planContentJson, PlanJsonOptions)
            ?? throw new InvalidOperationException("Plan content JSON deserialized to null.");

        var plan = new TaskPlan
        {
            TaskId = taskId,
            OrgId = task.OrgId,
            Version = await _db.TaskPlans.CountAsync(p => p.TaskId == taskId, ct) + 1,
            Content = content,
            Status = PlanStatus.PendingReview
        };
        _db.TaskPlans.Add(plan);

        execution.Status = ExecutionStatus.Completed;
        execution.CompletedAt = DateTimeOffset.UtcNow;
        execution.PromptTokens = promptTokens;
        execution.CompletionTokens = completionTokens;
        execution.TokensUsed = promptTokens + completionTokens;

        task.TotalTokensUsed += execution.TokensUsed;

        // Plan insert + execution completion + task counters flush in ONE save — the same
        // atomic unit the orchestrator's tracked-context planning phase had.
        await _db.SaveChangesAsync(ct);

        return new PlanSaveResult(plan.Version, ToSnapshot(execution));
    }

    public async Task<ExecutionSnapshot?> FailExecutionAsync(
        Ulid taskId, Ulid executionId, string error,
        int promptTokens = 0, int completionTokens = 0, string? sessionId = null,
        CancellationToken ct = default)
    {
        var task = await _db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        var execution = await _db.TaskExecutions.FirstOrDefaultAsync(e => e.Id == executionId, ct);
        if (task is null || execution is null) return null;

        execution.Status = ExecutionStatus.Failed;
        execution.ErrorMessage = error;
        execution.CompletedAt = DateTimeOffset.UtcNow;
        // Kept for the same reason a completed run's session is kept: whatever re-enters
        // this lane next should continue the transcript, not pay for it twice.
        if (!string.IsNullOrWhiteSpace(sessionId))
            execution.SessionId = sessionId;

        // Tokens burned before the failure are still paid to Anthropic — record them.
        if (promptTokens > 0 || completionTokens > 0)
        {
            execution.PromptTokens = promptTokens;
            execution.CompletionTokens = completionTokens;
            execution.TokensUsed = promptTokens + completionTokens;
            task.TotalTokensUsed += execution.TokensUsed;
        }

        task.Status = TaskStatus.Blocked;
        task.HasAgentWorking = false;

        await _db.SaveChangesAsync(ct);
        return ToSnapshot(execution);
    }

    public async Task MarkExecutionFailedAsync(
        Ulid taskId, Ulid executionId, string error, CancellationToken ct = default)
    {
        var execution = await _db.TaskExecutions
            .FirstOrDefaultAsync(e => e.Id == executionId && e.TaskId == taskId, ct);
        if (execution is null) return;

        execution.Status = ExecutionStatus.Failed;
        execution.ErrorMessage = error;
        execution.CompletedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<TaskMessageSnapshot?> AddTaskMessageAsync(
        Ulid taskId, string role, string content, MessageType messageType,
        string? metadataJson = null, CancellationToken ct = default)
    {
        var taskRef = await _db.Tasks.AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => new { t.OrgId, t.ProjectId })
            .FirstOrDefaultAsync(ct);
        if (taskRef is null) return null;

        var message = new TaskMessage
        {
            TaskId = taskId,
            OrgId = taskRef.OrgId,
            Role = role,
            Content = content,
            MessageType = messageType,
            Metadata = metadataJson
        };

        _db.TaskMessages.Add(message);
        await _db.SaveChangesAsync(ct);

        // Broadcast here, at the single write path, so no caller can persist a message the
        // task page never sees. The visible symptom of a skipped broadcast is nasty: the UI
        // infers "agent is working" from the last message's role, so an unbroadcast system
        // message after a user message leaves the thinking spinner running forever.
        await taskNotifier.SendNewMessageAsync(taskId, new NewMessageEvent(
            taskId, message.Id, message.Role, message.Content,
            message.MessageType.ToString(), message.SenderId, message.CreatedAt));

        return new TaskMessageSnapshot(
            message.Id, message.TaskId, taskRef.ProjectId, message.Role, message.Content,
            message.MessageType, message.SenderId, message.CreatedAt);
    }

    public async Task<Lane?> MoveTaskToPendingQuestionsAsync(
        Ulid taskId, Lane sourceLane, CancellationToken ct = default)
    {
        var task = await _db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return null;

        var fromLane = task.CurrentLane;
        task.CurrentLane = Lane.PendingQuestions;
        task.Status = TaskStatus.WaitingAnswer;
        task.QuestionSourceLane = sourceLane;
        task.HasAgentWorking = false;

        await _db.SaveChangesAsync(ct);
        return fromLane;
    }

    public async Task<bool> RequeueTaskForAnswerAsync(
        Ulid taskId, Lane lane, CancellationToken ct = default)
    {
        var task = await _db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return false;

        task.CurrentLane = lane;
        task.Status = TaskStatus.InQueue;
        task.QuestionSourceLane = null;
        task.HasAgentWorking = false;

        await _db.SaveChangesAsync(ct);

        await taskNotifier.SendStatusChangedAsync(task.ProjectId, new StatusChangedEvent(
            taskId, task.Status.ToString(), DateTimeOffset.UtcNow));
        return true;
    }

    public Task<int> CountAutoAnsweredMessagesAsync(Ulid taskId, CancellationToken ct = default)
        => _db.TaskMessages
            .AsNoTracking()
            // Metadata is JSON text. A string match, not json_extract: json_extract raises on
            // a malformed row and would fail the whole count, and every writer of this flag
            // writes the compact {"autoAnswered":true} (the spaced form is tolerated too).
            .CountAsync(m => m.TaskId == taskId
                && m.Metadata != null
                && (m.Metadata.Contains("\"autoAnswered\":true")
                    || m.Metadata.Contains("\"autoAnswered\": true")), ct);

    public async Task<TaskMessageSnapshot?> ApplyPmReviewAsync(
        Ulid taskId, string? improvedTitle, string? improvedDescription,
        string summaryMessage, string? metadataJson, string? recommendedModel = null,
        CancellationToken ct = default)
    {
        var task = await _db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return null;

        // Advisory only — a human applies or dismisses it from the task page. A re-review
        // that produced no recommendation leaves an earlier one standing (the task didn't
        // get bigger by being clarified).
        if (!string.IsNullOrWhiteSpace(recommendedModel))
            task.RecommendedModel = recommendedModel;

        if (!string.IsNullOrWhiteSpace(improvedDescription))
        {
            // Agent output bypasses the endpoint validators, so clamp to the column
            // limits (Title 500, Description 10000) so a model's overlong
            // output never lands in the row unchecked.
            if (!string.IsNullOrWhiteSpace(improvedTitle))
                task.Title = Truncate(improvedTitle, 500);
            task.Description = Truncate(improvedDescription, 10000);
        }

        var message = new TaskMessage
        {
            TaskId = task.Id,
            OrgId = task.OrgId,
            Role = "agent",
            Content = summaryMessage,
            MessageType = MessageType.Text,
            Metadata = metadataJson
        };
        _db.TaskMessages.Add(message);

        // Title/description replacement + PM summary message flush in one save, matching the
        // orchestrator's original tracked-context behavior.
        await _db.SaveChangesAsync(ct);

        return new TaskMessageSnapshot(
            message.Id, message.TaskId, task.ProjectId, message.Role, message.Content,
            message.MessageType, message.SenderId, message.CreatedAt);
    }

    public async Task MoveTaskToPlanReadyAsync(Ulid taskId, CancellationToken ct = default)
    {
        var task = await _db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return;

        task.CurrentLane = Lane.PlanReady;
        task.Status = TaskStatus.WaitingPlanApproval;
        task.HasAgentWorking = false;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<bool> AutoApprovePlanAsync(Ulid taskId, CancellationToken ct = default)
    {
        var task = await _db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return false;

        // Highest version: the planning phase may have re-run, and only the newest plan is the
        // one the dev agent will work from.
        var plan = await _db.TaskPlans
            .Where(p => p.TaskId == taskId && p.Status == PlanStatus.PendingReview)
            .OrderByDescending(p => p.Version)
            .FirstOrDefaultAsync(ct);

        if (plan is null) return false;

        plan.Status = PlanStatus.Approved;
        plan.ApprovedAt = DateTimeOffset.UtcNow;

        // Same transition the human approval performs: InQueue, so the queue processor claims
        // it against the project's concurrency budget rather than autopilot running it inline.
        task.CurrentLane = Lane.Developing;
        task.Status = TaskStatus.InQueue;
        task.HasAgentWorking = false;

        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task SetTaskBaseBranchAsync(Ulid taskId, string baseBranch, CancellationToken ct = default)
    {
        var task = await _db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null || task.BaseBranch == baseBranch) return;

        task.BaseBranch = baseBranch;
        await _db.SaveChangesAsync(ct);
    }

    public async Task SetTaskBranchAsync(Ulid taskId, string branchName, CancellationToken ct = default)
    {
        var task = await _db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return;

        task.BranchName = branchName;
        await _db.SaveChangesAsync(ct);

        // The interactive session banner is server-rendered and shows "preparing the
        // workspace" until BranchName exists — this ping is what ends that state live.
        await taskNotifier.SendTaskUpdatedAsync(task.ProjectId, new TaskUpdatedEvent(
            task.Id, Title: null, Priority: null, Status: null, CurrentLane: null,
            IsArchived: null, DateTimeOffset.UtcNow));
    }

    /// <summary>How long a milestone may sit in Shipping before the sweep assumes the worker
    /// that claimed it is gone and re-offers it.</summary>
    private static readonly TimeSpan StaleShipReclaimAfter = TimeSpan.FromMinutes(20);

    public async Task SetTaskSetupFailureAsync(
        Ulid taskId, string? reason, CancellationToken ct = default)
    {
        // Set-based: this runs on every phase that reuses a healthy worktree, where the
        // value is already null and loading the entity to write nothing would be waste.
        await _db.Tasks
            .Where(t => t.Id == taskId && t.SetupFailure != reason)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.SetupFailure, reason), ct);
    }

    public async Task SetTaskPublishedPrUrlAsync(Ulid taskId, string url, CancellationToken ct = default)
    {
        var task = await _db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null || task.PublishedPrUrl == url) return;

        task.PublishedPrUrl = url;
        await _db.SaveChangesAsync(ct);

        await taskNotifier.SendTaskUpdatedAsync(task.ProjectId, new TaskUpdatedEvent(
            task.Id, Title: null, Priority: null, Status: null, CurrentLane: null,
            IsArchived: null, DateTimeOffset.UtcNow));
    }

    public async Task SetTaskPrUrlAsync(Ulid taskId, string prUrl, CancellationToken ct = default)
    {
        var task = await _db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return;

        task.PrUrl = prUrl;
        // A fresh PR is a fresh decision — resume watching it, and clear any recorded
        // creation failure from earlier sweep attempts. The kind goes with the reason:
        // a stale PushFailed would otherwise keep steering the merge page's screen.
        task.PrClosedAt = null;
        var hadFailure = task.MergeFailureReason is not null;
        task.MergeFailureReason = null;
        task.MergeFailureKind = null;
        await _db.SaveChangesAsync(ct);

        if (hadFailure)
            await taskNotifier.SendTaskMergeFailedAsync(task.ProjectId,
                new TaskMergeFailedEvent(task.Id, Reason: null, DateTimeOffset.UtcNow));

        // The "Review PR" card and the review page both gate on PrUrl; without this ping a
        // freshly opened PR is invisible until a reload.
        await taskNotifier.SendTaskUpdatedAsync(task.ProjectId, new TaskUpdatedEvent(
            task.Id, Title: null, Priority: null, Status: null, CurrentLane: null,
            IsArchived: null, DateTimeOffset.UtcNow));
    }

    public async Task<DevelopingTransition?> CompleteDevelopingPhaseAsync(
        Ulid taskId, bool skipCodeReview, bool routeToTesting = false,
        bool parkForUserVerification = false, CancellationToken ct = default)
    {
        var task = await _db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return null;

        var fromLane = task.CurrentLane;
        if (routeToTesting)
        {
            // Queue for the QA agent (the park, when requested, happens on the QA pass)
            task.CurrentLane = Lane.Testing;
            task.Status = TaskStatus.InQueue;
            task.HasAgentWorking = false;
        }
        else if (parkForUserVerification)
        {
            // Park for the requester to test manually — applies even with SkipCodeReview;
            // approval then completes the task directly.
            task.CurrentLane = Lane.Testing;
            task.Status = TaskStatus.WaitingUserVerification;
            task.HasAgentWorking = false;
        }
        else if (skipCodeReview)
        {
            task.CurrentLane = Lane.Done;
            task.Status = TaskStatus.Completed;
            task.CompletedAt = DateTimeOffset.UtcNow;
            task.HasAgentWorking = false;
        }
        else
        {
            // Queue for the reviewer agent
            task.CurrentLane = Lane.CodeReview;
            task.Status = TaskStatus.InQueue;
            task.HasAgentWorking = false;
        }

        await _db.SaveChangesAsync(ct);
        return new DevelopingTransition(fromLane, task.CurrentLane, task.Status);
    }

    /// <summary>
    /// Has this task already been settled by someone else while an agent was running?
    ///
    /// An agent phase is a fire-and-forget run that can take minutes, and the poll cycle it was
    /// launched from keeps going — in the SAME process, never mind other replicas. So a human
    /// can approve and the merge poller can land the PR while the reviewer is still thinking.
    /// Once the task has been carried to Done, a late verdict is an opinion about a question
    /// that is already closed: writing it back resurrects a finished task into a waiting state
    /// nobody can clear.
    ///
    /// "Settled" means IN DONE — not "was ever merged". IsMerged is a one-way flag that nothing
    /// clears, and an earlier version of this guard keyed on it: a task a human had moved back
    /// out of Done then had every subsequent review verdict discarded as "already settled",
    /// was re-picked as resumable, and was reviewed again — an infinite loop that burned a full
    /// code review a minute and could never end. A task the user has deliberately pulled out of
    /// Done is, by definition, not settled.
    /// </summary>
    private static bool IsSettled(TaskEntity task) => task.CurrentLane == Lane.Done;

    /// <summary>
    /// Loads the task with its CURRENT database values, even when this scope already tracks a
    /// stale copy.
    ///
    /// The orchestrator holds one DI scope open for a whole agent phase, and phases run for
    /// minutes while merges and approvals land from other processes. FirstOrDefaultAsync on a
    /// context that already tracks the entity returns the TRACKED instance — EF's identity map
    /// discards the fresh values it just read — so a guard checking it judges the world as it
    /// was at phase START. That is how a reviewer's late rejection reopened a task merged 36
    /// seconds earlier: the settled check passed on a 6-minute-old IsMerged=false. Every
    /// post-agent-run write goes through this instead.
    /// </summary>
    private async Task<TaskEntity?> GetCurrentTaskAsync(Ulid taskId, CancellationToken ct)
    {
        var task = await _db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is not null)
            await _db.Entry(task).ReloadAsync(ct);
        return task;
    }

    public async Task<bool> SetTaskWaitingCodeReviewAsync(
        Ulid taskId, CancellationToken ct = default)
    {
        var task = await GetCurrentTaskAsync(taskId, ct);
        if (task is null) return false;

        // The observed failure: a human approved at 15:28:00, the poller merged, and one second
        // later an unreadable verdict stamped WaitingCodeReview over it. The board then showed
        // the task in Done wearing an "Awaiting Code Review" badge, with a worktree nobody
        // would clean up and an error message about a PR that had already landed.
        if (IsSettled(task)) return false;

        task.Status = TaskStatus.WaitingCodeReview;
        task.HasAgentWorking = false;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetTaskWaitingQaReviewAsync(Ulid taskId, CancellationToken ct = default)
    {
        var task = await GetCurrentTaskAsync(taskId, ct);
        if (task is null) return false;

        // Same race, one lane earlier: a QA run is just as long, and manual verification or an
        // approve-and-merge can land while it is still driving a browser.
        if (IsSettled(task)) return false;

        task.Status = TaskStatus.WaitingQaReview;
        task.HasAgentWorking = false;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task SetTaskWaitingUserVerificationAsync(Ulid taskId, CancellationToken ct = default)
    {
        var task = await _db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return;

        // Sets the lane too — callable defensively from any lane (e.g. a manual drag
        // into Testing on a project without automated QA).
        task.CurrentLane = Lane.Testing;
        task.Status = TaskStatus.WaitingUserVerification;
        task.HasAgentWorking = false;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<QaTransition?> ApplyQaVerdictAsync(
        Ulid taskId, bool passed, int maxQaCycles,
        bool parkForUserVerification = false, CancellationToken ct = default)
    {
        var task = await GetCurrentTaskAsync(taskId, ct);
        if (task is null) return null;

        // Same reasoning as the code-review verdict: a QA run lasts minutes, and a human can
        // approve-and-merge underneath it. A late QA verdict on a settled task is an opinion
        // about a closed question — callers already treat null as "nothing to apply".
        if (IsSettled(task)) return null;

        var fromLane = task.CurrentLane;
        var escalated = false;

        if (passed && parkForUserVerification)
        {
            // QA passed — park for the requester's manual verification before review
            task.CurrentLane = Lane.Testing;
            task.Status = TaskStatus.WaitingUserVerification;
            task.HasAgentWorking = false;
        }
        else if (passed)
        {
            // Queue for the reviewer agent
            task.CurrentLane = Lane.CodeReview;
            task.Status = TaskStatus.InQueue;
            task.HasAgentWorking = false;
        }
        else
        {
            task.QaCycleCount++;

            if (task.QaCycleCount < maxQaCycles)
            {
                // Send back to the dev agent with the QA report in the conversation
                task.CurrentLane = Lane.Developing;
                task.Status = TaskStatus.InQueue;
                task.HasAgentWorking = false;
            }
            else
            {
                task.Status = TaskStatus.WaitingQaReview;
                task.HasAgentWorking = false;
                escalated = true;
            }
        }

        await _db.SaveChangesAsync(ct);
        return new QaTransition(
            fromLane, task.CurrentLane, task.Status, task.QaCycleCount, escalated);
    }

    public async Task<CheckTransition?> ApplyCheckFailureAsync(
        Ulid taskId, int maxCheckCycles, CancellationToken ct = default)
    {
        var task = await _db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return null;

        var fromLane = task.CurrentLane;
        var escalated = false;

        task.CheckCycleCount++;

        if (task.CheckCycleCount < maxCheckCycles)
        {
            // Send back to the dev agent with the check output in the conversation
            task.CurrentLane = Lane.Developing;
            task.Status = TaskStatus.InQueue;
            task.HasAgentWorking = false;
        }
        else
        {
            // Same escalation shape as ApplyNoCommitOutcomeAsync: park where the problem is,
            // in the status a human can actually act on. WaitingUserVerification has a resume
            // path (a chat reply re-queues it for Developing with fresh counters);
            // WaitingCodeReview/WaitingQaReview would be dead ends — no PR or QA report to
            // point their affordances at.
            task.CurrentLane = Lane.Developing;
            task.Status = TaskStatus.WaitingUserVerification;
            task.HasAgentWorking = false;
            escalated = true;
        }

        await _db.SaveChangesAsync(ct);
        return new CheckTransition(
            fromLane, task.CurrentLane, task.Status, task.CheckCycleCount, escalated);
    }

    public async Task<CodeReviewTransition?> ApplyCodeReviewVerdictAsync(
        Ulid taskId, bool approved, int maxReviewCycles, CancellationToken ct = default)
    {
        var task = await GetCurrentTaskAsync(taskId, ct);
        if (task is null) return null;

        // Settled while the reviewer was running — a human approved, the poller merged. Null
        // reads to the orchestrator as "nothing to apply", which is exactly right: rejecting an
        // already-merged task would send merged code back to Developing, and approving it again
        // would restamp CompletedAt on work that finished minutes ago.
        if (IsSettled(task)) return null;

        var fromLane = task.CurrentLane;
        var escalated = false;

        if (approved)
        {
            task.CurrentLane = Lane.Done;
            task.Status = TaskStatus.Completed;
            task.CompletedAt = DateTimeOffset.UtcNow;
            task.HasAgentWorking = false;
        }
        else
        {
            task.ReviewCycleCount++;

            if (task.ReviewCycleCount < maxReviewCycles)
            {
                // Send back to the dev agent with the feedback in the conversation
                task.CurrentLane = Lane.Developing;
                task.Status = TaskStatus.InQueue;
                task.HasAgentWorking = false;
            }
            else
            {
                task.Status = TaskStatus.WaitingCodeReview;
                task.HasAgentWorking = false;
                escalated = true;
            }
        }

        await _db.SaveChangesAsync(ct);
        return new CodeReviewTransition(
            fromLane, task.CurrentLane, task.Status, task.ReviewCycleCount, escalated);
    }

    public async Task<BaseSyncRequeue?> RequeueForBaseSyncAsync(
        Ulid taskId, int maxAttempts, string reason, CancellationToken ct = default)
    {
        var task = await _db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return null;

        var fromLane = task.CurrentLane;
        task.BaseSyncCycleCount++;

        var escalated = task.BaseSyncCycleCount >= maxAttempts;
        if (escalated)
        {
            // Out of automatic passes. Park it where a failed merge already parks — the merge
            // page, with a reason — rather than looping another dev run that has now failed to
            // reconcile the same collision this many times.
            task.Status = TaskStatus.WaitingCodeReview;
            task.MergeFailureReason = reason;
            task.MergeFailureKind = MergeFailureKind.ResolutionExhausted;
        }
        else
        {
            task.CurrentLane = Lane.Developing;
            task.Status = TaskStatus.InQueue;
        }
        task.HasAgentWorking = false;

        await _db.SaveChangesAsync(ct);
        return new BaseSyncRequeue(
            fromLane, task.CurrentLane, task.Status, task.BaseSyncCycleCount, escalated);
    }

    public async Task<CodeReviewTransition?> ApplyNoCommitOutcomeAsync(
        Ulid taskId, int maxAttempts, CancellationToken ct = default)
    {
        var task = await _db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return null;

        var fromLane = task.CurrentLane;
        var escalated = false;

        task.ReviewCycleCount++;

        if (task.ReviewCycleCount < maxAttempts)
        {
            // Send back to the dev agent with the no-commit notice in the conversation
            task.CurrentLane = Lane.Developing;
            task.Status = TaskStatus.InQueue;
            task.HasAgentWorking = false;
        }
        else
        {
            // Park where the problem is, in the status a human can actually act on: the
            // message endpoint resumes WaitingUserVerification back to Developing on reply.
            // WaitingCodeReview would be a dead end here — it has no resume path and there
            // is no PR for its "review required" affordances to point at.
            task.CurrentLane = Lane.Developing;
            task.Status = TaskStatus.WaitingUserVerification;
            task.HasAgentWorking = false;
            escalated = true;
        }

        await _db.SaveChangesAsync(ct);
        return new CodeReviewTransition(
            fromLane, task.CurrentLane, task.Status, task.ReviewCycleCount, escalated);
    }

    public async Task StartSkipPlanningDevelopingAsync(Ulid taskId, CancellationToken ct = default)
    {
        var task = await _db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return;

        task.CurrentLane = Lane.Developing;
        task.Status = TaskStatus.Active;
        task.StartedAt ??= DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task EnsureSyntheticApprovedPlanAsync(
        Ulid taskId, string summary, string approach, CancellationToken ct = default)
    {
        var task = await _db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return;

        var hasPlan = await _db.TaskPlans.AnyAsync(
            p => p.TaskId == taskId && p.Status == PlanStatus.Approved, ct);
        if (hasPlan) return;

        _db.TaskPlans.Add(new TaskPlan
        {
            TaskId = taskId,
            OrgId = task.OrgId,
            Version = 1,
            Content = new PlanContent { Summary = summary, Approach = approach },
            Status = PlanStatus.Approved,
            ApprovedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync(ct);
    }

    public async Task<BlockedTaskResult?> BlockTaskWithErrorAsync(
        Ulid taskId, string error, CancellationToken ct = default)
    {
        var task = await _db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return null;

        task.Status = TaskStatus.Blocked;
        task.HasAgentWorking = false;

        // Close any run that was in flight when the error hit — otherwise the
        // execution row stays Running forever with no recorded reason.
        var runningExecutions = await _db.TaskExecutions
            .Where(e => e.TaskId == taskId && e.Status == ExecutionStatus.Running)
            .ToListAsync(ct);
        foreach (var execution in runningExecutions)
        {
            execution.Status = ExecutionStatus.Failed;
            execution.ErrorMessage = error;
            execution.CompletedAt = DateTimeOffset.UtcNow;
        }

        // Persist the reason in the thread so the UI can show why the task is blocked.
        var message = new TaskMessage
        {
            TaskId = task.Id,
            OrgId = task.OrgId,
            Role = "system",
            Content = $"Task blocked: {error}",
            MessageType = MessageType.Error
        };
        _db.TaskMessages.Add(message);

        await _db.SaveChangesAsync(ct);

        return new BlockedTaskResult(task.ProjectId, new TaskMessageSnapshot(
            message.Id, message.TaskId, task.ProjectId, message.Role, message.Content,
            message.MessageType, message.SenderId, message.CreatedAt));
    }

    public async Task<int> FailOrphanedRunningExecutionsAsync(
        string error, CancellationToken ct = default)
        => await _db.TaskExecutions
            .Where(e => e.Status == ExecutionStatus.Running)
            .Where(e => !_db.Tasks.Any(t => t.Id == e.TaskId && t.HasAgentWorking))
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, ExecutionStatus.Failed)
                .SetProperty(e => e.ErrorMessage, error)
                .SetProperty(e => e.CompletedAt, DateTimeOffset.UtcNow)
                .SetProperty(e => e.UpdatedAt, DateTimeOffset.UtcNow), ct);

    public async Task AddTaskTokensUsedAsync(Ulid taskId, int tokens, CancellationToken ct = default)
    {
        var task = await _db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return;

        task.TotalTokensUsed += tokens;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<bool> ClearTaskMergeFailureAsync(Ulid taskId, CancellationToken ct = default)
    {
        var task = await _db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return false;

        var hadFailure = task.MergeFailureReason != null || task.MergeFailureKind != null;
        task.MergeFailureReason = null;
        task.MergeFailureKind = null;
        var restoredFrom = MergeFailureLane.RestoreToDone(task);
        if (!hadFailure && restoredFrom is null)
            return false;

        await _db.SaveChangesAsync(ct);

        if (restoredFrom is { } fromLane)
            await taskNotifier.SendLaneChangedAsync(task.ProjectId, new LaneChangedEvent(
                task.Id, task.Title, fromLane.ToString(), task.CurrentLane.ToString(),
                task.Status.ToString(), DateTimeOffset.UtcNow));

        return true;
    }

    public async Task<MergeFailureUpdate?> SetTaskMergeFailureAsync(
        Ulid taskId, string reason, MergeFailureKind kind = MergeFailureKind.Unknown,
        CancellationToken ct = default)
    {
        var task = await _db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return null;

        // A merged task has no merge to fail. The late loser of a merge race — the resolver's
        // own push, refused after the sweep had landed the same PR (prod, 2026-09-16,
        // StoreFront #19) — would otherwise demote shipped work out of Done and stamp
        // "Merge failed" on a task whose commit is on main.
        if (task.IsMerged)
            return new MergeFailureUpdate(task.ProjectId, Changed: false, null);

        var truncated = reason.Length > 1000 ? reason[..1000] : reason;

        // Ahead of the change-detection below on purpose: a task already carrying this exact
        // reason (recorded before this rule existed, or by the merge click) is still sitting
        // in the wrong lane, and the poller's next cycle is what corrects it.
        var demotedFrom = MergeFailureLane.DemoteFromDone(task);

        // The merge poller retries every cycle — only record (and broadcast) a change. The kind
        // moving counts on its own: the same reason text can go from "retrying" to "automation
        // is out of moves", and that transition is the whole point of tracking it. It does NOT
        // add a second thread message, though — the text the human reads has not changed.
        var reasonChanged = task.MergeFailureReason != truncated;
        // A kind never recorded (rows from before kinds existed, or a reason set by the merge
        // click) IS Unknown — the enum's documented default — so the poller's identical retry
        // with the default kind is not a change. Only a real classification moving counts.
        var kindChanged = (task.MergeFailureKind ?? MergeFailureKind.Unknown) != kind;
        if (!reasonChanged && !kindChanged && demotedFrom is null)
            return new MergeFailureUpdate(task.ProjectId, Changed: false, null);

        task.MergeFailureReason = truncated;
        task.MergeFailureKind = kind;

        // Persist the reason in the thread so the UI can show why the merge is stuck.
        // Unlike BlockTaskWithErrorAsync this leaves the executions alone — the platform
        // keeps retrying, and the lane move above is the only status change.
        TaskMessage? message = null;
        if (reasonChanged)
        {
            message = new TaskMessage
            {
                TaskId = task.Id,
                OrgId = task.OrgId,
                Role = "system",
                Content = $"Merge failed: {truncated}",
                MessageType = MessageType.Error
            };
            _db.TaskMessages.Add(message);
        }

        await _db.SaveChangesAsync(ct);

        if (demotedFrom is { } fromLane)
            await taskNotifier.SendLaneChangedAsync(task.ProjectId, new LaneChangedEvent(
                task.Id, task.Title, fromLane.ToString(), task.CurrentLane.ToString(),
                task.Status.ToString(), DateTimeOffset.UtcNow));

        return new MergeFailureUpdate(task.ProjectId, reasonChanged || kindChanged,
            message is null
                ? null
                : new TaskMessageSnapshot(
                    message.Id, message.TaskId, task.ProjectId, message.Role, message.Content,
                    message.MessageType, message.SenderId, message.CreatedAt));
    }

    public async Task MarkTaskMergedAsync(Ulid taskId, string? mergeCommitSha = null, CancellationToken ct = default)
    {
        var task = await _db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return;

        var fromLane = task.CurrentLane;
        var hadMergeFailure = task.MergeFailureReason is not null;
        task.IsMerged = true;
        task.MergeFailureReason = null;
        // The kind goes with the reason: a task that landed on its second attempt was left
        // carrying EnvironmentBlocked from the first (prod, 2026-09-16, StoreFront #18).
        task.MergeFailureKind = null;
        task.MergeConflictsJson = null;
        task.MergeResolutionStrategy = null;
        task.MergeResolutionGuidance = null;
        task.MergeResolutionStartedAt = null;
        // The merge landed, so a follow-up meant to steer the resolution has nothing left to
        // steer. Dropping it here is what stops it running against an already-merged task.
        task.QueuedMergeStrategy = null;
        task.QueuedMergeGuidance = null;
        if (task.CurrentLane != Lane.Done)
        {
            task.CurrentLane = Lane.Done;
            task.Status = TaskStatus.Completed;
            task.CompletedAt ??= DateTimeOffset.UtcNow;
        }
        await _db.SaveChangesAsync(ct);

        // Did this move the repo's DEFAULT branch, or only the milestone's integration branch?
        // Derived from whether the milestone is Branch-mode AND has a branch, because that is
        // exactly the condition under which the PR was based on it — see TaskOrchestrator's
        // base-branch resolver. Backlog tasks (no milestone) and tasks under a Trunk milestone
        // always land on the default branch.
        //
        // Local-path repos always target the default branch even under a milestone, and are
        // reported as such here only incidentally: they have no deploys, so the flag's one
        // consumer never looks at them.
        // A Story member lands on the Story's shared branch, never on the default branch.
        var storyMember = task.GroupId is { } memberGroupId
            && await _db.TaskGroups.AsNoTracking()
                .AnyAsync(g => g.Id == memberGroupId && g.Kind == TaskGroupKind.Story, ct);
        var landedOnDefault = !storyMember && (task.MilestoneId is not { } milestoneId
            || !await _db.Milestones
                .AsNoTracking()
                .AnyAsync(m => m.Id == milestoneId
                    && m.IntegrationMode == MilestoneIntegrationMode.Branch
                    && m.BranchName != null, ct));

        await _eventBus.PublishAsync(new Orkka.SharedKernel.Events.Catalog.TaskMerged(
            task.Id, task.OrgId, task.ProjectId, task.RepoId, mergeCommitSha,
            landedOnDefault), ct);

        // The merge sweep runs in the Worker with no endpoint in the loop — this broadcast is
        // the only way a board learns the task reached Done without a reload.
        if (fromLane != Lane.Done)
            await taskNotifier.SendLaneChangedAsync(task.ProjectId, new LaneChangedEvent(
                task.Id, task.Title, fromLane.ToString(), task.CurrentLane.ToString(),
                task.Status.ToString(), DateTimeOffset.UtcNow));
        if (hadMergeFailure)
            await taskNotifier.SendTaskMergeFailedAsync(task.ProjectId,
                new TaskMergeFailedEvent(task.Id, Reason: null, DateTimeOffset.UtcNow));
    }

    public async Task MarkPrClosedAsync(Ulid taskId, CancellationToken ct = default)
    {
        var task = await _db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null || task.IsMerged || task.PrClosedAt is not null) return;

        task.PrClosedAt = DateTimeOffset.UtcNow;
        // Reuses the merge-failure channel rather than inventing a parallel one: it already
        // renders on the board card and the task page, and the save hook turns it into a
        // notification. Rejected work never merged, so it leaves Done with every other merge
        // failure — a human decides what happens next from code review.
        task.MergeFailureReason =
            "The pull request was closed without merging. Reopen it, or reset the task to start over.";
        // A closed PR drops out of the merge sweep, so a resolution pending on it would
        // never be picked up OR cleared — leaving the merge page spinning on "resolving"
        // with no way for the user to choose again.
        task.MergeResolutionStrategy = null;
        task.MergeResolutionGuidance = null;
        task.MergeResolutionStartedAt = null;
        task.QueuedMergeStrategy = null;
        task.QueuedMergeGuidance = null;
        var demotedFrom = MergeFailureLane.DemoteFromDone(task);

        await _db.SaveChangesAsync(ct);

        await taskNotifier.SendTaskMergeFailedAsync(task.ProjectId,
            new TaskMergeFailedEvent(task.Id, task.MergeFailureReason, DateTimeOffset.UtcNow));
        if (demotedFrom is { } fromLane)
            await taskNotifier.SendLaneChangedAsync(task.ProjectId, new LaneChangedEvent(
                task.Id, task.Title, fromLane.ToString(), task.CurrentLane.ToString(),
                task.Status.ToString(), DateTimeOffset.UtcNow));
    }

    public async Task CompleteTaskWithoutPrAsync(Ulid taskId, CancellationToken ct = default)
    {
        var task = await _db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return;

        var fromLane = task.CurrentLane;
        task.CurrentLane = Lane.Done;
        task.Status = TaskStatus.Completed;
        task.CompletedAt ??= DateTimeOffset.UtcNow;
        task.IsMerged = true;
        task.BranchName = null;
        await _db.SaveChangesAsync(ct);

        // Sweep-only caller (no endpoint notification exists for this path) — broadcast or
        // the card sits in its old lane until a reload.
        if (fromLane != Lane.Done)
            await taskNotifier.SendLaneChangedAsync(task.ProjectId, new LaneChangedEvent(
                task.Id, task.Title, fromLane.ToString(), task.CurrentLane.ToString(),
                task.Status.ToString(), DateTimeOffset.UtcNow));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Reset sweep
    // ─────────────────────────────────────────────────────────────────────────

    // ResetRequestedAt (not Status = Resetting) is the authoritative reset flag: a run
    // racing the reset request can still overwrite Status (phase completions, error
    // handlers), but nothing except the reset endpoint sets — and nothing except
    // finalize clears — ResetRequestedAt.
    public async Task<IReadOnlyList<ResetPendingTask>> ListResetPendingTasksAsync(
        CancellationToken ct = default)
        => await _db.Tasks
            .AsNoTracking()
            .Where(t => t.ResetRequestedAt != null)
            .Select(t => new ResetPendingTask(
                t.Id, t.OrgId, t.ProjectId, t.RepoId, t.Title, t.CurrentLane, t.BranchName,
                t.LeaseOwner, t.LeaseExpiresAt, t.RunnerUserId ?? t.CreatedByUserId,
                t.ResetRequestedAt))
            .ToListAsync(ct);

    public async Task<bool> FinalizeTaskResetAsync(Ulid taskId, CancellationToken ct = default)
    {
        // ExecuteDelete/ExecuteUpdate bypass the change tracker, so the wipe and the
        // field reset are held together by an explicit transaction instead of one save.
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        var now = DateTimeOffset.UtcNow;

        // The ResetRequestedAt guard makes finalize single-winner across replicas:
        // whoever clears the flag claims the whole reset, everyone else sees 0 rows.
        var reset = await _db.Tasks
            .Where(t => t.Id == taskId && t.ResetRequestedAt != null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.CurrentLane, Lane.Drafting)
                .SetProperty(t => t.Status, TaskStatus.Draft)
                .SetProperty(t => t.HasAgentWorking, false)
                // A Story member keeps the Story's shared branch: it re-plans on top of it as it
                // stands now (its earlier commits stay — they were reviewed and pushed), and its
                // review base is re-recorded at the next worktree creation, after those commits.
                .SetProperty(t => t.BranchName, t => t.StoryOrder != null ? t.BranchName : null)
                .SetProperty(t => t.ReviewBaseSha, (string?)null)
                .SetProperty(t => t.PrUrl, (string?)null)
                .SetProperty(t => t.PrClosedAt, (DateTimeOffset?)null)
                .SetProperty(t => t.IsMerged, false)
                .SetProperty(t => t.MergeFailureReason, (string?)null)
                .SetProperty(t => t.MergeConflictsJson, (string?)null)
                .SetProperty(t => t.MergeResolutionStrategy, (string?)null)
                .SetProperty(t => t.MergeResolutionGuidance, (string?)null)
                .SetProperty(t => t.MergeResolutionStartedAt, (DateTimeOffset?)null)
                .SetProperty(t => t.QueuedMergeStrategy, (string?)null)
                .SetProperty(t => t.QueuedMergeGuidance, (string?)null)
                .SetProperty(t => t.CurrentAgentId, (Ulid?)null)
                .SetProperty(t => t.LeaseOwner, (string?)null)
                .SetProperty(t => t.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(t => t.ReviewCycleCount, 0)
                .SetProperty(t => t.QaCycleCount, 0)
                .SetProperty(t => t.CheckCycleCount, 0)
                .SetProperty(t => t.QuestionSourceLane, (Lane?)null)
                .SetProperty(t => t.StartedAt, (DateTimeOffset?)null)
                .SetProperty(t => t.CompletedAt, (DateTimeOffset?)null)
                .SetProperty(t => t.QueuePosition, (int?)null)
                // A reset task is back in Drafting and no longer the session's work: keeping
                // the stamp inflated the session's "waiting on you" count forever AND made
                // the task re-eligible for submission under the same session — double-counted
                // on every counter it touched.
                .SetProperty(t => t.AutopilotSessionId, (Ulid?)null)
                // The task is starting over, so its documentation pass must too — otherwise a
                // reset task that completes again is never documented.
                .SetProperty(t => t.DocSyncStatus, DocSyncStatus.None)
                .SetProperty(t => t.DocSyncAttempts, 0)
                .SetProperty(t => t.DocSyncCompletedAt, (DateTimeOffset?)null)
                .SetProperty(t => t.ResetRequestedAt, (DateTimeOffset?)null)
                .SetProperty(t => t.ResetCount, t => t.ResetCount + 1)
                .SetProperty(t => t.UpdatedAt, now), ct);

        if (reset == 0)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        await _db.TaskMessages.Where(m => m.TaskId == taskId).ExecuteDeleteAsync(ct);
        await _db.TaskPlans.Where(p => p.TaskId == taskId).ExecuteDeleteAsync(ct);
        // Reset discards the branch — a stored code-change snapshot would show code that no
        // longer exists anywhere.
        await _db.CodeChangeSnapshots.Where(s => s.TaskId == taskId).ExecuteDeleteAsync(ct);

        // Execution history and token totals are kept for audit — only in-flight rows close.
        await _db.TaskExecutions
            .Where(e => e.TaskId == taskId
                && (e.Status == ExecutionStatus.Running || e.Status == ExecutionStatus.Queued))
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, ExecutionStatus.Cancelled)
                .SetProperty(e => e.ErrorMessage, "Cancelled — task was reset by the user.")
                .SetProperty(e => e.CompletedAt, now)
                .SetProperty(e => e.UpdatedAt, now), ct);

        await transaction.CommitAsync(ct);
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Mapping
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether the session a task runs under answers every question. Read with the snapshot
    /// rather than copied onto the task, so the session row stays the one place the choice
    /// lives; the primary-key lookup only happens for autopilot tasks. Cross-tenant like the
    /// Worker's other autopilot reads — the session id comes from the task itself.
    /// </summary>
    private async Task<bool> AutopilotAnswersEveryQuestionAsync(TaskEntity t, CancellationToken ct)
        => t.AutopilotSessionId is { } sessionId
            && await _db.AutopilotSessions
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(s => s.Id == sessionId)
                .Select(s => s.AnswerEveryQuestion)
                .FirstOrDefaultAsync(ct);

    private static TaskSnapshot ToSnapshot(TaskEntity t, bool autopilotAnswersEveryQuestion) => new(
        t.Id, t.OrgId, t.ProjectId, t.RepoId, t.Number, t.Title, t.Description,
        t.Priority, t.Type, t.CurrentLane, t.Status, t.IsArchived,
        t.RequesterLocale, t.TotalTokensUsed, t.HasAgentWorking, t.BranchName, t.PrUrl,
        t.Tags, t.ContextFiles, t.ContextDocumentIds, t.SkipPlanning, t.SkipTesting, t.SkipCodeReview, t.IsMerged,
        t.ReviewCycleCount, t.QaCycleCount, t.QuestionSourceLane, t.AssignedAgentId, t.CurrentAgentId,
        t.SourceMessageId, t.StartedAt, t.CompletedAt,
        // A task always runs on a runner: its stamped owner, else whoever created it.
        t.RunnerUserId ?? t.CreatedByUserId,
        t.MergeResolutionStrategy, t.MergeResolutionGuidance, t.MergeConflictsJson,
        t.ResetCount, t.Model, t.SkipPmReview, t.CreatedByUserId, t.SetupFailure,
        t.AutopilotSessionId, t.IsInteractive, t.InteractiveSessionStartedAt,
        t.InteractiveAnsweredThroughAt, t.InteractiveFinishRequestedAt, t.ApiSurface,
        t.CheckCycleCount, t.MilestoneId, t.BaseBranch, t.BaseSyncCycleCount,
        t.SpendApprovedUsd, autopilotAnswersEveryQuestion, t.GroupId,
        t.ExternalKey, t.ExternalUrl, t.ExternalEpic, t.WorkingSubpath,
        t.StoryOrder, t.ReviewBaseSha);

    private static ExecutionSnapshot ToSnapshot(TaskExecution e) => new(
        e.Id, e.TaskId, e.OrgId, e.AgentId, e.Lane, e.Status, e.AgentLog,
        e.TokensUsed, e.PromptTokens, e.CompletionTokens, e.ErrorMessage,
        e.StartedAt, e.CompletedAt);

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
