namespace Orkka.Modules.Tasks.Infrastructure.EventHandlers;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Features.Tasks;
using Orkka.SharedKernel.Events;
using Orkka.SharedKernel.Events.Catalog;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

// When a task completes, every task that was waiting on it needs its "waiting on" badge
// refreshed on live boards. For an ordinary edge a completed-but-unmerged blocker still
// blocks and its badge merely flips to "awaiting merge"; for an edge between two members of
// the same cross-repo group, completion CLEARS it (see TaskDependencyQueries.IsUnmet), and
// the next queue poll starts the dependent. TasksDbContext publishes TaskStatusChanged on every tracked
// transition into Completed, so hooking the bus here covers all completion sites (worker,
// code review, merge, webhook) in whichever process performed the write — with zero changes
// to those sites. Best-effort realtime: a lost event is healed by a board reload.
public sealed class DependentsRefreshOnTaskCompleted(
    TasksDbContext db,
    ITaskNotifier taskNotifier,
    Orkka.Modules.Projects.Contracts.IProjectsApi projectsApi,
    ILogger<DependentsRefreshOnTaskCompleted> logger) : IIntegrationEventHandler<TaskStatusChanged>
{
    public async Task HandleAsync(TaskStatusChanged evt, CancellationToken ct)
    {
        if (evt.NewStatus != nameof(TaskStatus.Completed))
            return;

        var refreshed = await DependentsRefresh.RefreshAsync(db, taskNotifier, projectsApi, evt.TaskId, evt.ProjectId, ct);

        if (refreshed > 0)
            logger.LogInformation(
                "Refreshed dependency badges for {Count} task(s) after {TaskId} completed",
                refreshed, evt.TaskId);
    }
}

/// <summary>
/// Shared recompute-and-notify body for the completed/merged handlers: re-derives each
/// dependent's remaining unmet blockers and pushes a TaskDependenciesChanged event per
/// dependent. Returns how many dependents were refreshed (0 when the blocker has none).
/// </summary>
internal static class DependentsRefresh
{
    public static async Task<int> RefreshAsync(
        TasksDbContext db,
        ITaskNotifier taskNotifier,
        Orkka.Modules.Projects.Contracts.IProjectsApi projectsApi,
        Ulid blockerTaskId,
        Ulid projectId,
        CancellationToken ct)
    {
        var dependents = await db.TaskDependencies
            .AsNoTracking()
            .Where(d => d.DependsOnTaskId == blockerTaskId)
            .Select(d => d.TaskId)
            .Distinct()
            .ToListAsync(ct);

        if (dependents.Count == 0)
            return 0;

        var project = await projectsApi.GetProjectSummaryAsync(projectId, ct);
        if (project is null)
            return 0;

        // Remaining unmet blockers per dependent, one grouped query for all of them.
        var remainingByTaskId = (await db.TaskDependencies
                .AsNoTracking()
                .Where(d => dependents.Contains(d.TaskId))
                .Where(TaskDependencyQueries.IsUnmet)
                .Select(d => new { d.TaskId, d.DependsOnTaskId, d.DependsOnTask.Number, d.DependsOnTask.Status, d.DependsOnTask.IsMerged })
                .ToListAsync(ct))
            .GroupBy(x => x.TaskId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => new BlockingTaskRef(x.DependsOnTaskId, $"{project.Key}-{x.Number}", x.Status.ToString(), x.IsMerged)).ToList());

        foreach (var dependentId in dependents)
        {
            await taskNotifier.SendTaskDependenciesChangedAsync(projectId, new TaskDependenciesChangedEvent(
                dependentId,
                remainingByTaskId.GetValueOrDefault(dependentId) ?? [],
                DateTimeOffset.UtcNow));
        }

        return dependents.Count;
    }
}
