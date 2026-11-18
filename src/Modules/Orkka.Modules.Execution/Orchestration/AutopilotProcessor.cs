namespace Orkka.Modules.Execution.Orchestration;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Usage.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;

/// <summary>
/// Keeps the queue fed from Drafting while a session is open, and closes the session when there
/// is nothing left to do.
///
/// It does NOT execute anything. Everything downstream of submission â€” dependency deferral,
/// concurrency slots, same-repo serialisation, claiming,
/// PR creation, merge â€” is already <see cref="TaskQueueProcessor"/>'s job and is already
/// autonomous. Autopilot is the missing auto-submitter, nothing more, which is why this class
/// is a selection policy rather than a second pipeline.
/// </summary>
public sealed class AutopilotProcessor(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<AutopilotProcessor> logger) : BackgroundService
{
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(
        configuration.GetValue<int>("Worker:AutopilotPollSeconds", 15));

    /// <summary>
    /// How long a session must have nothing to do before it is declared drained.
    ///
    /// Without it, a user adding tasks one at a time would race the sweep: type a task, sweep
    /// finds the board momentarily empty, session closes, toggle flips off under their cursor.
    /// Measured against the session's UpdatedAt, which only moves when a counter actually
    /// changed â€” so "quiet" means quiet, not merely "unswept".
    /// </summary>
    /// <summary>Sessions already told once that they are holding for a milestone — the
    /// sweep runs every few seconds and the line is worth reading once.</summary>
    private readonly HashSet<Ulid> _holdLogged = [];

    private readonly TimeSpan _drainGrace = TimeSpan.FromSeconds(
        configuration.GetValue<int>("Worker:AutopilotDrainGraceSeconds", 90));

    /// <summary>Terminal failures in a row before the session stops. A broken setup command
    /// fails every task identically; without this it would burn the whole cap discovering
    /// that. Config-tunable: transient environment trouble (a runner briefly offline, a
    /// permission misconfiguration being fixed) used to kill whole sessions at the
    /// hard-coded 3.</summary>
    private readonly int _maxConsecutiveFailures =
        configuration.GetValue<int>("Worker:AutopilotMaxConsecutiveFailures", 3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("AutopilotProcessor started with {Interval}s poll interval",
            _pollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Autopilot sweep failed");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }

        logger.LogInformation("AutopilotProcessor stopped");
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var tasksApi = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        var sessions = await tasksApi.ListOpenAutopilotSessionsAsync(ct);

        // Stopping deliberately lets in-flight tasks finish — but their spend used to stop
        // being recorded the moment the session closed, so "Spent" read permanently low by
        // exactly the tail of the session. Keep refreshing counters until the drain settles.
        await RefreshDrainingSessionsAsync(scope, tasksApi, ct);

        if (sessions.Count == 0)
            return;

        var projectsApi = scope.ServiceProvider.GetRequiredService<IProjectsApi>();
        var usageQuery = scope.ServiceProvider.GetRequiredService<IUsageQuery>();
        var notifier = scope.ServiceProvider.GetRequiredService<ITaskNotifier>();

        foreach (var session in sessions)
        {
            if (ct.IsCancellationRequested)
                return;

            try
            {
                await SweepSessionAsync(
                    session, tasksApi, projectsApi, usageQuery, notifier, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // One bad project must not stop every other org's autopilot.
                logger.LogError(ex, "Autopilot sweep failed for session {SessionId}", session.Id);
            }
        }
    }

    /// <summary>Counter/spend refresh only — no submission, no stop logic; those belong to
    /// open sessions. Best-effort per session: accounting lag must not fail the sweep.</summary>
    private async Task RefreshDrainingSessionsAsync(
        IServiceScope scope, ITasksApi tasksApi, CancellationToken ct)
    {
        IReadOnlyList<OpenAutopilotSession> draining;
        try
        {
            draining = await tasksApi.ListDrainingAutopilotSessionsAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not list draining autopilot sessions");
            return;
        }

        if (draining.Count == 0)
            return;

        var usageQuery = scope.ServiceProvider.GetRequiredService<IUsageQuery>();

        foreach (var session in draining)
        {
            if (ct.IsCancellationRequested)
                return;

            try
            {
                var taskIds = await tasksApi.ListAutopilotSessionTaskIdsAsync(session.Id, ct);
                var progress = await tasksApi.GetAutopilotProgressAsync(session.Id, ct);
                var spent = taskIds.Count == 0
                    ? 0m
                    : (await usageQuery.GetTaskCostsAsync(
                        session.OrgId, session.ProjectId, taskIds, ct)).Values.Sum();

                // ConsecutiveFailures is meaningless after a stop — nothing reads it — so
                // recomputing it here would be noise; 0 keeps the row quiet.
                await tasksApi.UpdateAutopilotSessionAsync(
                    session.Id, spent, taskIds.Count,
                    progress.Completed, progress.Failed, consecutiveFailures: 0, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Draining refresh failed for autopilot session {SessionId}", session.Id);
            }
        }
    }

    private async Task SweepSessionAsync(
        OpenAutopilotSession session,
        ITasksApi tasksApi,
        IProjectsApi projectsApi,
        IUsageQuery usageQuery,
        ITaskNotifier notifier,
        CancellationToken ct)
    {
        var project = await projectsApi.GetProjectSummaryAsync(session.ProjectId, ct);
        if (project is null)
        {
            await StopAsync(tasksApi, notifier, session, AutopilotStopReason.ProjectUnavailable, ct);
            return;
        }

        var sessionTaskIds = await tasksApi.ListAutopilotSessionTaskIdsAsync(session.Id, ct);
        var progress = await tasksApi.GetAutopilotProgressAsync(session.Id, ct);

        // Spend is priced by the Usage module over the session's OWN tasks, never by summing project
        // usage since a timestamp: the latter would also charge the session for chat, design
        // and brief runs a human triggered while it happened to be open. It is the ESTIMATED
        // list-price spend (vendor-reported cost, else list price of the tokens) — Orkka bills
        // nothing and records runner work at $0, so the recorded cost would never reach a cap.
        var spent = sessionTaskIds.Count == 0
            ? 0m
            : (await usageQuery.GetTaskEstimatedSpendAsync(
                session.OrgId, session.ProjectId, sessionTaskIds, ct)).Values.Sum();

        var consecutiveFailures = CountLeadingFailures(progress.RecentlyTerminal);

        await tasksApi.UpdateAutopilotSessionAsync(
            session.Id, spent, sessionTaskIds.Count,
            progress.Completed, progress.Failed, consecutiveFailures, ct);

        if (session.CapUsd is { } cap && spent >= cap)
        {
            logger.LogInformation(
                "Autopilot session {SessionId} reached its cap ({Spent:C} of {Cap:C})",
                session.Id, spent, cap);
            await StopAsync(tasksApi, notifier, session, AutopilotStopReason.CapReached, ct);
            return;
        }

        if (consecutiveFailures >= _maxConsecutiveFailures)
        {
            logger.LogWarning(
                "Autopilot session {SessionId} stopped after {Count} consecutive failures",
                session.Id, consecutiveFailures);
            await StopAsync(tasksApi, notifier, session, AutopilotStopReason.ConsecutiveFailures, ct);
            return;
        }

        // Headroom: keep the queue stocked a little ahead of what the project can run at once,
        // rather than dumping the backlog in. A wall of InQueue cards makes QueuePosition
        // meaningless and fires one dependency-analysis model call per submitted task.
        //
        // Dependency-blocked queued tasks are excluded: pickup skips them, so they hold no
        // execution slot — counting them as in-flight let a wave of blocked dependents eat
        // the entire headroom and stop the very blockers they wait on from being submitted.
        var dependencyBlocked = sessionTaskIds.Count == 0
            ? 0
            : (await tasksApi.GetTasksWithUnmetDependenciesAsync(sessionTaskIds, ct)).Count;
        var maxParallel = project.Settings?.MaxParallelTasks ?? 3;
        var depthFactor = Math.Max(1, project.Settings?.AutopilotQueueDepthFactor ?? 2);

        // Tasks being planned ahead are in-flight too, so the headroom has to cover them or
        // plan-ahead would eat the queue it needs: the pipeline wants MaxParallelTasks building
        // AND PlanAheadDepth planning at once, and the depth factor keeps a queue behind both.
        var planAhead = project.Settings?.EffectivePlanAheadDepth
            ?? ProjectSettingsDto.PlanAheadDepthFor(maxParallel);
        var headroom = ((maxParallel + planAhead) * depthFactor)
            - Math.Max(0, progress.Progressing - dependencyBlocked);

        var candidates = headroom > 0
            ? await tasksApi.ListAutopilotCandidatesAsync(
                session.OrgId, session.ProjectId, headroom, ct)
            : [];

        var submitted = 0;
        foreach (var candidate in candidates)
        {
            if (ct.IsCancellationRequested)
                return;

            // Guarded and set-based, so two replicas sweeping the same project cannot both
            // submit this task. A false here means the other one got it â€” an ordinary race.
            if (await tasksApi.TryAutoSubmitAsync(candidate.Id, session.Id, ct))
                submitted++;
        }

        if (submitted > 0)
        {
            logger.LogInformation(
                "Autopilot submitted {Count} task(s) for project {ProjectId} (session {SessionId})",
                submitted, session.ProjectId, session.Id);
            return;
        }

        await StopIfDrainedAsync(tasksApi, notifier, session, progress, candidates.Count, ct);
    }

    /// <summary>
    /// Ends the session when there is nothing left for autopilot to do.
    ///
    /// "Nothing to submit" is NOT the condition, and getting that wrong is the whole trap: a
    /// milestone whose backend tasks are running and whose frontend tasks are blocked on them
    /// has zero submittable work right now, and stopping there would switch autopilot off in the
    /// middle of its own dependency wave. So a session stays open while anything it submitted
    /// can still move on its own.
    ///
    /// Work parked for a human does not count as movement. It will not progress until somebody
    /// looks at it, and holding the session open for that means holding it open until morning â€”
    /// so autopilot stops, and the parked task is what the user finds waiting.
    /// </summary>
    private async Task StopIfDrainedAsync(
        ITasksApi tasksApi, ITaskNotifier notifier, OpenAutopilotSession session,
        AutopilotProgress progress, int candidateCount, CancellationToken ct)
    {
        if (!HasNothingLeftToMove(progress, candidateCount))
            return;

        // UpdatedAt only moves when a counter actually changed, so an old one means the session
        // has genuinely been still â€” not merely that this sweep found nothing.
        var quietFor = DateTimeOffset.UtcNow - session.UpdatedAt;
        if (quietFor < _drainGrace)
            return;

        // Nothing to submit because the next milestone is gated on one that is still landing
        // — its last tasks running or parked for their runner, its PRs merging, its ship
        // pending. That is waiting, not drained: the moment the milestone ships, those
        // Drafting tasks become candidates and this session submits them. Prod, 2026-09-17:
        // a session started for milestone 4 stopped itself in fifteen seconds while
        // milestone 3's last two tasks were landing, and the owner had to start it again.
        try
        {
            var pending = await tasksApi.GetAutopilotStatusAsync(session.OrgId, session.ProjectId, ct);
            if (pending.DraftingRemaining > 0
                && await tasksApi.HasMilestoneWorkInFlightAsync(session.ProjectId, ct))
            {
                if (_holdLogged.Add(session.Id))
                    logger.LogInformation(
                        "Autopilot session {SessionId} is holding — {Drafting} task(s) in Drafting wait on a milestone still landing",
                        session.Id, pending.DraftingRemaining);
                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not tell whether a milestone is still landing for session {SessionId}", session.Id);
        }

        logger.LogInformation(
            "Autopilot session {SessionId} drained â€” {Completed} completed, {Failed} failed, "
            + "{Parked} waiting on a human",
            session.Id, progress.Completed, progress.Failed, progress.ParkedForHuman);

        // "Drained" with a full Drafting lane is not drained, it is blocked: every remaining
        // task is behind a dependency still in Drafting or a milestone that has not shipped
        // (a Failed ship holds the whole roadmap). Name it, because from the board this looks
        // like autopilot switching itself off for no reason - observed on a project where a
        // milestone's ship had failed and four sessions in a row ended with 0 tasks.
        try
        {
            var status = await tasksApi.GetAutopilotStatusAsync(session.OrgId, session.ProjectId, ct);
            if (status.DraftingRemaining > 0)
                logger.LogWarning(
                    "Autopilot session {SessionId} is stopping with {Drafting} task(s) still in Drafting "
                    + "that it cannot submit - check for a milestone whose ship failed, or tasks whose "
                    + "dependencies are still drafts (project {ProjectId})",
                    session.Id, status.DraftingRemaining, session.ProjectId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not count Drafting tasks for the drain log of session {SessionId}", session.Id);
        }

        await StopAsync(tasksApi, notifier, session, AutopilotStopReason.Drained, ct);
    }

    /// <summary>
    /// Nothing left in the session that can move on its own. A task waiting for its owner's
    /// runner is NOT parked on a human: the runner reconnecting re-queues it. Prod,
    /// 2026-09-17: the Worker restarted on a deploy, the runner had not reconnected thirty
    /// seconds later, the one remaining task was parked WaitingForRunner, and the session
    /// drained fifteen seconds before the runner came back — leaving a gated pair with one
    /// half in Done and the other with nobody to approve its plan.
    /// </summary>
    internal static bool HasNothingLeftToMove(AutopilotProgress progress, int candidateCount)
        => candidateCount == 0 && progress.Progressing == 0 && progress.WaitingForRunner == 0;

    private async Task StopAsync(
        ITasksApi tasksApi, ITaskNotifier notifier, OpenAutopilotSession session,
        AutopilotStopReason reason, CancellationToken ct)
    {
        // Losing this race just means another replica stopped it first, for its own reason.
        // In-flight tasks are deliberately left alone: cancelling a mid-run agent throws away
        // everything already paid for, and the run is nearly always cheaper to finish.
        if (!await tasksApi.StopAutopilotSessionAsync(session.Id, reason, ct))
            return;

        logger.LogInformation(
            "Autopilot session {SessionId} stopped ({Reason})", session.Id, reason);

        // The board has to flip for whoever is watching, and this stop came from the Worker at
        // whatever hour it happened — nobody clicked anything. Without the broadcast the toggle
        // stays visually on until the page is reloaded.
        try
        {
            var status = await tasksApi.GetAutopilotStatusAsync(session.OrgId, session.ProjectId, ct);
            await notifier.SendAutopilotChangedAsync(session.ProjectId,
                new AutopilotChangedEvent(session.ProjectId, status, DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            // The session is stopped either way; the board catches up on next load. Never let
            // a hub failure resurface as a sweep error and retry the stop.
            logger.LogWarning(ex,
                "Could not broadcast autopilot stop for session {SessionId}", session.Id);
        }
    }

    /// <summary>Failures at the head of the newest-first terminal sequence. A single success
    /// anywhere in the run breaks the streak, which is what makes this "cannot make progress"
    /// rather than "has had some failures".</summary>
    private static int CountLeadingFailures(IReadOnlyList<bool> recentlyTerminalSucceeded)
    {
        var count = 0;
        foreach (var succeeded in recentlyTerminalSucceeded)
        {
            if (succeeded) break;
            count++;
        }

        return count;
    }
}

