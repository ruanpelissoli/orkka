namespace Orkka.Local.Infrastructure;

using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Orkka.Modules.Tasks.Features.Hubs;
using Orkka.Runners.Server.AppRun;

/// <summary>
/// App-run ("Run project") events, broadcast straight to the browsers' TaskHub group — this
/// process holds both the runner socket and the hub. Same group and message names as
/// TaskHub's own app-run relays (<c>apprun:{projectId}:{userId}</c>, <c>AppRunOutput</c>,
/// <c>AppRunState</c>), so the UI is unchanged. Scrollback is stripped from state updates:
/// browsers get it from the attach call and keep their copy across updates.
/// </summary>
public sealed class LocalAppRunEvents(
    IHubContext<TaskHub> hub,
    ILogger<LocalAppRunEvents> logger) : IAppRunEvents
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task OutputAsync(
        Ulid orgId, string userId, Ulid projectId, string runId, string base64, CancellationToken ct)
    {
        try
        {
            await hub.Clients.Group(Group(projectId, userId)).SendAsync("AppRunOutput", runId, base64, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Failed to broadcast app-run output for run {RunId}", runId);
        }
    }

    public async Task StateAsync(
        Ulid orgId, string userId, Ulid projectId, IReadOnlyList<AppRunSnapshot> snapshots, CancellationToken ct)
    {
        try
        {
            var payload = JsonSerializer.SerializeToElement(
                snapshots.Select(s => s with { Scrollback = null }), Json);
            await hub.Clients.Group(Group(projectId, userId)).SendAsync("AppRunState", payload, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Failed to broadcast app-run state for project {ProjectId}", projectId);
        }
    }

    internal static string Group(Ulid projectId, string userId) => $"apprun:{projectId}:{userId}";
}
