namespace Orkka.Runners.Server.AppRun;

using Microsoft.Extensions.Logging;
using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Events;
using Orkka.SharedKernel.Events.Catalog;

/// <summary>
/// Keeps the running app in step with the repo: when a task's work lands on the base branch,
/// every running target of that project that does not reload itself is restarted, and the
/// task thread gets the address where the merged work is now live.
///
/// Registered in the processes that hold the runner socket — Orkka Local's host and the
/// hosted Worker — because that is where the orchestrator's run state lives. On hosted, a
/// merge performed by the API host publishes its event there, not here; the pipeline's own
/// merges (autopilot, auto-approve) do land in the Worker, and the Attach reconcile catches
/// the rest the next time the panel opens.
///
/// Reaction-only, and best-effort: a restart that fails is logged, never surfaced on the task.
/// </summary>
public sealed class AppRunOnTaskMerged(
    AppRunOrchestrator orchestrator,
    ITasksApi tasksApi,
    ILogger<AppRunOnTaskMerged> logger) : IIntegrationEventHandler<TaskMerged>
{
    public async Task HandleAsync(TaskMerged evt, CancellationToken ct)
    {
        var audiences = orchestrator.AudiencesOf(evt.ProjectId);
        if (audiences.Count == 0)
            return;

        var liveUrls = new List<string>();
        foreach (var (orgId, userId) in audiences)
        {
            try
            {
                var restarted = await orchestrator.RestartAsync(
                    orgId, userId, evt.ProjectId, plan => plan.RestartOnMerge, ct);
                if (restarted > 0)
                    logger.LogInformation(
                        "Task {TaskId} merged — restarted {Count} target(s) of project {ProjectId}",
                        evt.TaskId, restarted, evt.ProjectId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Task {TaskId} merged but restarting project {ProjectId}'s targets failed",
                    evt.TaskId, evt.ProjectId);
            }

            // Only the org that owns the task gets the message; on hosted every audience is
            // one member of it, so the URLs are the same localhost ones for each.
            if (orgId == evt.OrgId)
            {
                liveUrls.AddRange(orchestrator.Snapshot(orgId, userId, evt.ProjectId)
                    .Where(s => s.Status == AppRunStatuses.Running && s.PreviewUrl is not null)
                    .Select(s => s.PreviewUrl!));
            }
        }

        var urls = liveUrls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (urls.Count == 0)
            return;

        try
        {
            var where = urls.Count == 1
                ? urls[0]
                : string.Join(" · ", urls);
            await tasksApi.AddTaskMessageAsync(
                evt.TaskId, "system",
                $"Merged — the app is running with this change at {where}",
                MessageType.SystemEvent, ct: ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not post the live-URL message on task {TaskId}", evt.TaskId);
        }
    }
}
