namespace Orkka.Modules.Tasks.Infrastructure.EventHandlers;

using Microsoft.Extensions.Logging;
using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Events;
using Orkka.SharedKernel.Events.Catalog;

// A dependency is only satisfied once the blocker is done AND merged, so the merge — not
// completion — is the moment dependents actually unblock. Refresh their "waiting on" badges
// on live boards; the worker's next poll picks the unblocked dependents up on its own.
// Best-effort realtime: a lost event is healed by a board reload.
public sealed class DependentsRefreshOnTaskMerged(
    TasksDbContext db,
    ITaskNotifier taskNotifier,
    Orkka.Modules.Projects.Contracts.IProjectsApi projectsApi,
    ILogger<DependentsRefreshOnTaskMerged> logger) : IIntegrationEventHandler<TaskMerged>
{
    public async Task HandleAsync(TaskMerged evt, CancellationToken ct)
    {
        var refreshed = await DependentsRefresh.RefreshAsync(db, taskNotifier, projectsApi, evt.TaskId, evt.ProjectId, ct);

        if (refreshed > 0)
            logger.LogInformation(
                "Refreshed dependency badges for {Count} task(s) after {TaskId} merged",
                refreshed, evt.TaskId);
    }
}
