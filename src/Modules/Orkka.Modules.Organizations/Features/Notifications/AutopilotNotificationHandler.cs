namespace Orkka.Modules.Organizations.Features.Notifications;

using Orkka.Modules.Organizations.Domain;
using Orkka.Modules.Organizations.Infrastructure;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Events;
using Orkka.SharedKernel.Events.Catalog;

/// <summary>
/// Tells whoever started an autopilot session how it ended.
///
/// This is the whole point of running unattended: you come back to a result, not to a dashboard
/// you have to interpret. So the body leads with what changed — shipped, failed, waiting on you
/// — and the reason explains why it stopped rather than merely that it did.
///
/// IMPORTANT: the dispatcher scope has no authenticated ICurrentUser, so the tenant query filter
/// is OFF here — OrgId must be set on new rows and filtered in queries explicitly.
/// </summary>
public sealed class AutopilotStoppedNotificationHandler(
    NotificationPublisher publisher,
    IProjectsApi projectsApi)
    : IIntegrationEventHandler<AutopilotSessionStopped>
{
    public async Task HandleAsync(AutopilotSessionStopped evt, CancellationToken ct)
    {
        // A manual stop is the one case the recipient already knows about — they just clicked
        // the button. Everything else happened while they were not looking, which is exactly
        // when a notification earns its place.
        if (evt.Reason == nameof(AutopilotStopReason.Manual))
            return;

        if (string.IsNullOrEmpty(evt.StartedByUserId))
            return;

        var project = await projectsApi.GetProjectSummaryAsync(evt.ProjectId, ct);

        var notification = new Notification
        {
            OrgId = evt.OrgId,
            UserId = evt.StartedByUserId,
            Type = NotificationType.AutopilotStopped,
            Title = Title(evt, project?.Name),
            Body = Body(evt),
            ProjectId = evt.ProjectId,
            ProjectSlug = project?.Slug,
        };

        await publisher.PublishAsync(
            notification,
            persist: true,
            ct);
    }

    private static string Title(AutopilotSessionStopped evt, string? projectName)
    {
        var where = projectName is null ? "Autopilot" : $"Autopilot on {projectName}";

        return evt.Reason switch
        {
            nameof(AutopilotStopReason.Drained) => $"{where} finished",
            nameof(AutopilotStopReason.CapReached) => $"{where} reached its spend cap",
            nameof(AutopilotStopReason.BudgetBlocked) => $"{where} stopped — spend limit reached",
            nameof(AutopilotStopReason.ConsecutiveFailures) => $"{where} stopped after repeated failures",
            _ => $"{where} stopped",
        };
    }

    /// <summary>
    /// Outcome first, explanation second. "Waiting on you" leads when there is any, because it
    /// is the only part the user has to act on.
    /// </summary>
    private static string Body(AutopilotSessionStopped evt)
    {
        var parts = new List<string>
        {
            evt.TasksCompleted == 1 ? "1 task shipped" : $"{evt.TasksCompleted} tasks shipped",
        };

        if (evt.TasksWaitingOnYou > 0)
            parts.Add($"{evt.TasksWaitingOnYou} waiting on you");

        if (evt.TasksFailed > 0)
            parts.Add($"{evt.TasksFailed} failed");

        if (evt.SpentUsd > 0)
            parts.Add($"{evt.SpentUsd:C} spent");

        var summary = string.Join(", ", parts) + ".";

        var why = evt.Reason switch
        {
            nameof(AutopilotStopReason.Drained) =>
                " Nothing left to pick up on its own.",
            nameof(AutopilotStopReason.CapReached) =>
                " Raise the cap and turn it back on to keep going.",
            nameof(AutopilotStopReason.BudgetBlocked) =>
                " Raise the limit and turn it back on to keep going.",
            nameof(AutopilotStopReason.ConsecutiveFailures) =>
                " Several tasks failed in a row — worth checking the repo's setup command before restarting.",
            nameof(AutopilotStopReason.ProjectUnavailable) =>
                " The project is no longer available.",
            _ => "",
        };

        return summary + why;
    }
}
