namespace Orkka.Modules.Tasks.Infrastructure.Services;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Features.Milestones;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Events;
using Orkka.SharedKernel.Events.Catalog;

/// <summary>
/// Recomputes a milestone's status from the tasks under it and broadcasts the new counts.
///
/// Status is *derived*, never accumulated: there is no stored counter to drift, and calling this
/// twice for the same change is harmless. That matters because the triggers are scattered — a
/// merge, a status change, a task being moved between milestones — and a missed call costs a
/// stale badge rather than a wrong one, since progress is recomputed on every read anyway.
/// </summary>
public sealed class MilestoneProgressUpdater(
    TasksDbContext db,
    ITaskNotifier taskNotifier,
    IEventBus eventBus,
    ILogger<MilestoneProgressUpdater> logger)
{
    public async Task RefreshAsync(Ulid orgId, Ulid projectId, Ulid? milestoneId, CancellationToken ct)
    {
        if (milestoneId is not { } id)
            return;

        // Untracked: the transition below is a conditional UPDATE, never a tracked save, so
        // nothing here should leave a stale Milestone in a context a caller may save later.
        var milestone = await db.Milestones
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == id && m.OrgId == orgId && m.ProjectId == projectId, ct);

        if (milestone is null)
            return;

        var counts = (await MilestoneQueries.GetCountsByMilestoneAsync(db, orgId, projectId, ct))
            .For(id);

        var status = milestone.Status;

        // Cancelled is the one status a human sets deliberately. Never overwrite it — a
        // cancelled milestone whose stragglers merge must not resurrect itself as Completed.
        if (status != MilestoneStatus.Cancelled)
        {
            var next = Derive(milestone, counts, await HasStartedAsync(db, orgId, id, ct));

            if (next != status)
            {
                if (await TransitionAsync(milestone, next, ct))
                {
                    logger.LogInformation(
                        "Milestone {MilestoneId} {From} → {To} ({Done}/{Total} done)",
                        id, status, next, counts.Done, counts.Total);

                    status = next;

                    // Published after the write so a failed statement can't produce a phantom
                    // "what's next?" prompt for a milestone that never closed — and only by
                    // the refresh whose write landed, so it fires once per completion.
                    if (next == MilestoneStatus.Completed)
                    {
                        await eventBus.PublishAsync(new MilestoneCompleted(
                            id, orgId, projectId, milestone.Title,
                            milestone.CreatedByUserId, counts.Total), ct);
                    }
                }
                else
                {
                    // A concurrent refresh for the same change got there first. Report what it
                    // wrote rather than the status this one loaded, which is now stale.
                    status = await db.Milestones
                        .AsNoTracking()
                        .Where(m => m.Id == id)
                        .Select(m => m.Status)
                        .FirstOrDefaultAsync(ct);
                }
            }
        }

        await taskNotifier.SendMilestoneChangedAsync(projectId,
            MilestoneNotifications.Changed(id, status.ToString(), counts));
    }

    /// <summary>
    /// Applies one status transition as a single UPDATE keyed on the status it was derived from.
    /// True when this call made the transition.
    ///
    /// Refreshes race. The <c>TaskStatusChanged</c> handler and the caller that saved the task
    /// both refresh the same milestone, and <c>TaskMerged</c> and <c>TaskStatusChanged</c> both
    /// fire for the last task. Each loads the old status and derives the same transition; keying
    /// on the previous status makes the transition happen once.
    ///
    /// A milestone ships in place: its tasks land one by one (merged into a folder, or published
    /// to the forge as the developer's own pull requests), so completing it is shipping it —
    /// Shipped is written in the same statement. There is no integration branch to merge and no
    /// sweep that could, which is also why dependent milestones can rely on Shipped arriving.
    /// </summary>
    private async Task<bool> TransitionAsync(Milestone milestone, MilestoneStatus next, CancellationToken ct)
    {
        var previous = milestone.Status;
        var now = DateTimeOffset.UtcNow;
        var completes = next == MilestoneStatus.Completed;

        // Set once, when work begins; never cleared by a later transition.
        DateTimeOffset? startedAt = next is MilestoneStatus.Active or MilestoneStatus.Completed
            ? now
            : null;

        var rows = await db.Milestones
            .Where(m => m.Id == milestone.Id && m.Status == previous)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, next)
                .SetProperty(m => m.StartedAt, m => m.StartedAt ?? startedAt)
                .SetProperty(m => m.CompletedAt,
                    m => completes ? (DateTimeOffset?)(m.CompletedAt ?? now) : null)
                .SetProperty(m => m.ShipState,
                    m => completes ? MilestoneShipState.Shipped : m.ShipState)
                .SetProperty(m => m.ShippedAt,
                    m => completes ? (DateTimeOffset?)now : m.ShippedAt)
                .SetProperty(m => m.ShipFailureReason,
                    m => completes ? null : m.ShipFailureReason),
                ct);

        return rows == 1;
    }

    /// <summary>
    /// An empty milestone stays Planned however it was reached — "0 of 0 done" is not an
    /// achievement, and auto-completing one would fire the grooming trigger on a milestone
    /// nobody has planned yet.
    /// </summary>
    private static MilestoneStatus Derive(
        Milestone milestone, MilestoneCountsResponse counts, bool hasStarted) =>
        counts.Total switch
        {
            0 => MilestoneStatus.Planned,
            _ when counts.Done == counts.Total => MilestoneStatus.Completed,
            _ when hasStarted => MilestoneStatus.Active,
            // Everything still in Drafting: planned, not started.
            _ => MilestoneStatus.Planned,
        };

    /// <summary>Work has begun once any task has left Drafting. Archived tasks count — they
    /// are counted by <see cref="MilestoneQueries.GetCountsByMilestoneAsync"/>, and a milestone
    /// deriving "started" from a smaller set than it derives "done" from can contradict itself.
    /// </summary>
    private static Task<bool> HasStartedAsync(
        TasksDbContext db, Ulid orgId, Ulid milestoneId, CancellationToken ct) =>
        db.Tasks
            .AsNoTracking()
            .AnyAsync(t => t.OrgId == orgId && t.MilestoneId == milestoneId
                && t.CurrentLane != Lane.Drafting, ct);
}
