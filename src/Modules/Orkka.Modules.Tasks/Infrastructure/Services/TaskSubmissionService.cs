namespace Orkka.Modules.Tasks.Infrastructure.Services;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// The one place a task moves Drafting → InQueue.
///
/// Submission is not a single field write: it also opens the dependency-analysis gate, assigns a
/// queue position, starts the task's milestone, broadcasts the lane change, and kicks off the
/// background edge analysis. Autopilot performs the same transition from the Worker with no human
/// present, and any of those steps going missing on that path is a real defect — a task submitted
/// without <see cref="TaskEntity.DependencyAnalysisStartedAt"/> can be claimed in the seconds
/// before its edges exist, which is precisely how a whole milestone starts at once.
///
/// So both callers share this service rather than each maintaining their own copy.
/// </summary>
public sealed class TaskSubmissionService(
    TasksDbContext db,
    ITaskNotifier taskNotifier,
    MilestoneProgressUpdater milestoneProgress,
    IServiceScopeFactory scopeFactory,
    ILogger<TaskSubmissionService> logger)
{
    /// <summary>
    /// Submits a task the caller has already loaded, authorized, and validated. The entity must
    /// be tracked: the change-tracker save is what publishes the module's attention events, which
    /// a set-based update would bypass.
    /// </summary>
    public async Task SubmitAsync(TaskEntity entity, string? requesterLocale, CancellationToken ct)
    {
        var fromLane = entity.CurrentLane;

        entity.CurrentLane = Lane.Ready;
        entity.Status = TaskStatus.InQueue;
        entity.RequesterLocale = requesterLocale ?? entity.RequesterLocale;
        entity.DependencyAnalysisStartedAt = DateTimeOffset.UtcNow;
        entity.QueuePosition = await NextQueuePositionAsync(entity.OrgId, entity.ProjectId, ct);

        await db.SaveChangesAsync(ct);

        await AfterSubmissionAsync(
            entity.Id, entity.OrgId, entity.ProjectId, entity.MilestoneId,
            entity.Title, fromLane, entity.Status, ct);
    }

    /// <summary>
    /// Submits a task on autopilot's behalf, stamping the session that owns it.
    ///
    /// Guarded and set-based: the update only matches a task still sitting in Drafting, so two
    /// Worker replicas sweeping the same project cannot both submit it and double-count the
    /// session's task counter. Returns false when it did not match — already submitted, being
    /// reset, or no longer eligible — which the sweep treats as "someone else got it", never as
    /// an error.
    ///
    /// <paramref name="sessionId"/> lands in the SAME update as the lane move. A task that were
    /// InQueue without its stamp would run through the human-gated pipeline instead of the
    /// hands-free one, so the two must never be separately observable.
    /// </summary>
    public async Task<bool> TryAutoSubmitAsync(Ulid taskId, Ulid sessionId, CancellationToken ct)
    {
        // Read the bits the post-submission side effects need before the update, since afterwards
        // the row no longer matches the guard and a re-read cannot tell us we were the winner.
        var task = await db.Tasks
            .AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => new { t.OrgId, t.ProjectId, t.MilestoneId, t.Title })
            .FirstOrDefaultAsync(ct);

        if (task is null)
            return false;

        var now = DateTimeOffset.UtcNow;

        // Computed outside the guarded update — a correlated MAX is not expressible in
        // ExecuteUpdate, and a tie here costs two cards sharing a queue position on the board.
        // Pickup order is priority-first anyway; this is a display detail, not an invariant.
        var queuePosition = await NextQueuePositionAsync(task.OrgId, task.ProjectId, ct);

        var rows = await db.Tasks
            .Where(t => t.Id == taskId
                && t.CurrentLane == Lane.Drafting
                && t.Status == TaskStatus.Draft
                && !t.IsArchived
                && t.RepoId != null
                && t.ResetRequestedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.CurrentLane, Lane.Ready)
                .SetProperty(t => t.Status, TaskStatus.InQueue)
                .SetProperty(t => t.AutopilotSessionId, sessionId)
                .SetProperty(t => t.DependencyAnalysisStartedAt, now)
                .SetProperty(t => t.QueuePosition, queuePosition)
                .SetProperty(t => t.UpdatedAt, now),
                ct);

        if (rows == 0)
            return false;

        await AfterSubmissionAsync(
            taskId, task.OrgId, task.ProjectId, task.MilestoneId,
            task.Title, Lane.Drafting, TaskStatus.InQueue, ct);

        logger.LogInformation(
            "Autopilot submitted task {TaskId} under session {SessionId}", taskId, sessionId);

        return true;
    }

    /// <summary>
    /// Everything that must happen once a task is in the queue, whoever put it there: tell the
    /// board, start the milestone, and analyse the task's dependencies.
    /// </summary>
    private async Task AfterSubmissionAsync(
        Ulid taskId, Ulid orgId, Ulid projectId, Ulid? milestoneId,
        string title, Lane fromLane, TaskStatus status, CancellationToken ct)
    {
        await taskNotifier.SendLaneChangedAsync(projectId, new LaneChangedEvent(
            taskId, title, fromLane.ToString(), Lane.Ready.ToString(),
            status.ToString(), DateTimeOffset.UtcNow));

        // Leaving Drafting is what starts a milestone. No integration event covers it —
        // Draft → InQueue is not an attention status — so the transition is driven here.
        await milestoneProgress.RefreshAsync(orgId, projectId, milestoneId, ct);

        // A task the human marked as spanning repos is split instead of analysed. The split
        // writes its own edges from the plan it just made, so running the analyzer as well
        // would spend a second model call to guess at an order we already know — and the
        // analyzer rewrites its own Ai rows on every run, which is why the split's edges are
        // recorded as Manual.
        var groupId = await db.Tasks
            .AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => t.GroupId)
            .FirstOrDefaultAsync(ct);

        // A Story member is neither split nor analysed: it is one sub-task in one repository,
        // and the story's order (its Manual edge on the previous member) is the dependency.
        var isStoryMember = groupId is { } gid && await db.TaskGroups.AsNoTracking()
            .AnyAsync(g => g.Id == gid && g.Kind == Contracts.TaskGroupKind.Story, ct);

        if (isStoryMember)
        {
            // Nothing will run that would clear the gate submission opened, so clear it now:
            // left set, every member sat out the whole analysis window (5 minutes) before the
            // queue would claim it — the first sub-task of a Story included.
            var member = db.Tasks.Local.FirstOrDefault(t => t.Id == taskId)
                ?? await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
            if (member is { DependencyAnalysisStartedAt: not null })
            {
                member.DependencyAnalysisStartedAt = null;
                await db.SaveChangesAsync(ct);
            }
            return;
        }
        if (groupId is not null)
            FireCrossRepoSplit(taskId);
        else
            FireDependencyAnalysis(taskId);
    }

    /// <summary>
    /// Splits a spanning task across its repositories, off the request path.
    ///
    /// Same shape as <see cref="FireDependencyAnalysis"/> and for the same reasons: it is a model
    /// call that submission must not block on, the dependency gate opened at submission keeps the
    /// queue off the task meanwhile, and the gate is cleared here however the split went.
    ///
    /// The siblings need no gate of their own. They are inserted in the same transaction as the
    /// edges between them, so there is no instant at which a sibling is visible to the queue
    /// without the edges that order it.
    /// </summary>
    private void FireCrossRepoSplit(Ulid taskId)
    {
        _ = Task.Run(async () =>
        {
            using var scope = scopeFactory.CreateScope();
            try
            {
                var splitter = scope.ServiceProvider.GetRequiredService<ICrossRepoSplitter>();
                await splitter.SplitAsync(taskId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Cross-repo split failed for task {TaskId}", taskId);
            }
            finally
            {
                try
                {
                    var scopedDb = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
                    await scopedDb.Tasks
                        .Where(t => t.Id == taskId)
                        .ExecuteUpdateAsync(s => s.SetProperty(
                            t => t.DependencyAnalysisStartedAt, (DateTimeOffset?)null),
                            CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Could not clear the split gate for task {TaskId} — it unblocks on the "
                        + "time window instead", taskId);
                }
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Analyses the task's dependencies against the rest of the board, off the request path.
    ///
    /// Deliberately not awaited: it is a model call, and submission must not block on it. The
    /// gate opened above is what keeps the queue from claiming the task in the meantime, and the
    /// gate is cleared here however the analysis went — a failed analysis means fewer known
    /// edges, not a task nobody may ever run.
    /// </summary>
    private void FireDependencyAnalysis(Ulid taskId)
    {
        _ = Task.Run(async () =>
        {
            using var scope = scopeFactory.CreateScope();
            try
            {
                var analyzer = scope.ServiceProvider.GetRequiredService<ITaskDependencyAnalyzer>();
                await analyzer.AnalyzeDependenciesAsync(taskId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Dependency analysis failed for task {TaskId}", taskId);
            }
            finally
            {
                try
                {
                    var scopedDb = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
                    await scopedDb.Tasks
                        .Where(t => t.Id == taskId)
                        .ExecuteUpdateAsync(s => s.SetProperty(
                            t => t.DependencyAnalysisStartedAt, (DateTimeOffset?)null),
                            CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Could not clear the dependency-analysis gate for task {TaskId} — it "
                        + "unblocks on the time window instead", taskId);
                }
            }
        }, CancellationToken.None);
    }

    private async Task<int> NextQueuePositionAsync(Ulid orgId, Ulid projectId, CancellationToken ct)
    {
        // MAX over every task that ever held a position, not only those currently InQueue.
        // Scoped to InQueue, the numbering restarted at 1 whenever the queue drained — so a
        // task re-entering the queue later (answered question, runner re-park, review
        // bounce) kept its old low number and silently jumped the whole line.
        var max = await db.Tasks
            .Where(t => t.OrgId == orgId && t.ProjectId == projectId
                && t.QueuePosition != null)
            .MaxAsync(t => (int?)t.QueuePosition, ct) ?? 0;

        return max + 1;
    }
}
