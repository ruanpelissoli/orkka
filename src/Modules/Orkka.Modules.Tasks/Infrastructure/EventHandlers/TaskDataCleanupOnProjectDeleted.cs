namespace Orkka.Modules.Tasks.Infrastructure.EventHandlers;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orkka.SharedKernel.Events;
using Orkka.SharedKernel.Events.Catalog;

// Deleting a project used to cascade-delete its tasks via FKs to the projects table. Projects
// live in their own module/schema (no cross-module FKs), so this handler removes the task rows
// instead. Dependent rows (task plans/messages/executions/dependencies) still cascade from
// their module-internal FKs.
public sealed class TaskDataCleanupOnProjectDeleted(
    TasksDbContext db,
    ILogger<TaskDataCleanupOnProjectDeleted> logger) : IIntegrationEventHandler<ProjectDeleted>
{
    public async Task HandleAsync(ProjectDeleted evt, CancellationToken ct)
    {
        var tasks = await db.Tasks.Where(t => t.ProjectId == evt.ProjectId).ExecuteDeleteAsync(ct);

        // After the tasks, not before: the milestone FK is SetNull, so deleting milestones first
        // would rewrite every task row on its way out for nothing.
        var milestones = await db.Milestones
            .Where(m => m.ProjectId == evt.ProjectId).ExecuteDeleteAsync(ct);

        logger.LogInformation(
            "Cleaned up task data for deleted project {ProjectId}: {Tasks} tasks, {Milestones} milestones",
            evt.ProjectId, tasks, milestones);
    }
}
