namespace Orkka.Modules.Tasks.Infrastructure.EventHandlers;

using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Tasks.Infrastructure.Services;
using Orkka.SharedKernel.Events;
using Orkka.SharedKernel.Events.Catalog;

/// <summary>
/// A merge is what finishes a task's work, so it is the moment a milestone can become Complete.
/// Best-effort realtime: a lost event costs a stale badge until the next read, since counts are
/// recomputed on every roadmap load.
/// </summary>
public sealed class MilestoneProgressOnTaskMerged(
    TasksDbContext db,
    MilestoneProgressUpdater updater) : IIntegrationEventHandler<TaskMerged>
{
    public async Task HandleAsync(TaskMerged evt, CancellationToken ct)
    {
        var milestoneId = await db.Tasks
            .AsNoTracking()
            .Where(t => t.Id == evt.TaskId)
            .Select(t => t.MilestoneId)
            .FirstOrDefaultAsync(ct);

        await updater.RefreshAsync(evt.OrgId, evt.ProjectId, milestoneId, ct);
    }
}

/// <summary>
/// Covers the transitions a merge doesn't: a task entering an attention status changes the
/// "needs attention" count, and Completed can close a milestone whose tasks never produce a PR
/// (a docs-only or already-merged task).
/// </summary>
public sealed class MilestoneProgressOnTaskStatusChanged(
    TasksDbContext db,
    MilestoneProgressUpdater updater) : IIntegrationEventHandler<TaskStatusChanged>
{
    public async Task HandleAsync(TaskStatusChanged evt, CancellationToken ct)
    {
        var milestoneId = await db.Tasks
            .AsNoTracking()
            .Where(t => t.Id == evt.TaskId)
            .Select(t => t.MilestoneId)
            .FirstOrDefaultAsync(ct);

        await updater.RefreshAsync(evt.OrgId, evt.ProjectId, milestoneId, ct);
    }
}
