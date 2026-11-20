namespace Orkka.Modules.Trackers.Features.Imports;

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Runners.Contracts;
using Orkka.Modules.Trackers.Contracts;

/// <summary><see cref="ITrackerImports"/>: the preview's runner reads (keys only, through the
/// loopback relay to the source owner's runner) and the queue, shared by the endpoints and MCP.</summary>
internal sealed class TrackerImportService(
    ITrackersApi trackersApi,
    IRunnerRelayLocator relayLocator,
    IHttpClientFactory httpClientFactory,
    ILogger<TrackerImportService> logger) : ITrackerImports
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan RelayTimeout = TimeSpan.FromMinutes(3);
    private const string NoSource = "This project has no ticket source yet — set one up first.";

    public Task<TrackerImportResult> RequestAsync(Ulid projectId, string userId, string key, IReadOnlyList<string> subTaskKeys,
        CancellationToken ct = default)
        => trackersApi.RequestImportAsync(projectId, userId, key, subTaskKeys, ct);

    public Task<TrackerImportStatus?> GetAsync(Ulid projectId, Ulid importId, CancellationToken ct = default)
        => trackersApi.GetImportAsync(projectId, importId, ct);

    public async Task<TrackerImportPreviewOutcome> PreviewAsync(Ulid orgId, string userId, Ulid projectId, string input,
        CancellationToken ct = default)
    {
        if (await trackersApi.SourceKindAsync(projectId, ct) is not { } kind)
            return new TrackerImportPreviewOutcome(true, false, NoSource);
        if (trackersApi.NormalizeTicketKey(kind, input) is not { } key)
            return new TrackerImportPreviewOutcome(true, false, kind switch
            {
                TrackerKinds.Jira => $"'{input}' is not a Jira issue key (like SHOP-10) or issue link.",
                TrackerKinds.AzureDevOps => $"'{input}' is not an Azure DevOps work item id (like 4567) or work item link.",
                TrackerKinds.GitHub => $"'{input}' is not a GitHub issue (owner/repo#12) or issue link.",
                _ => $"'{input}' is not a ticket key.",
            });

        // First the ticket alone (its sub-tasks are only known once it is read), then the ticket
        // with its sub-tasks.
        var first = await ReadAsync(orgId, userId, projectId, [key], ct);
        if (first.Response is { } early)
            return early;
        var ticket = first.Tickets!.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));
        if (ticket is null)
            return new TrackerImportPreviewOutcome(true, false,
                $"{key} was not found with your account (it does not exist, was moved, or is not visible to you).");
        if (ticket.Level == TicketLevel.Above)
            return new TrackerImportPreviewOutcome(true, false,
                $"{key} is above a story (an epic or a feature). Orkka works stories and their sub-tasks — import one of its stories.");

        var tickets = first.Tickets!;
        if (ticket.ChildKeys is { Length: > 0 } children)
        {
            var second = await ReadAsync(orgId, userId, projectId, [key, .. children.Take(100)], ct);
            if (second.Response is { } late)
                return late;
            tickets = second.Tickets!;
        }

        var preview = await trackersApi.PreviewImportAsync(projectId, key, tickets, ct);
        return preview is null
            ? new TrackerImportPreviewOutcome(true, false, $"{key} was not found with your account.")
            : new TrackerImportPreviewOutcome(true, true, null, preview);
    }

    private async Task<(TrackerImportPreviewOutcome? Response, IReadOnlyList<TrackerTicket>? Tickets)> ReadAsync(
        Ulid orgId, string userId, Ulid projectId, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var built = await trackersApi.BuildImportAssignmentAsync(projectId, keys, ct);
        if (built is null)
            return (new TrackerImportPreviewOutcome(true, false, NoSource), null);
        if (built.OwnerUserId != userId)
            return (new TrackerImportPreviewOutcome(true, false,
                "Only the ticket source's owner can import tickets — their runner holds the tracker token."), null);

        var relay = await relayLocator.LocateAsync(orgId, userId, ct);
        if (relay is null)
            return (new TrackerImportPreviewOutcome(false, false), null);
        try
        {
            var client = httpClientFactory.CreateClient(RunnerRelay.HttpClientName);
            client.Timeout = RelayTimeout;
            using var response = await client.PostAsJsonAsync(
                $"{relay.BaseUrl}/internal/runner/tracker-query",
                new RelayTrackerQueryRequest(orgId.ToString(), relay.UserId,
                    new TrackerQueryRequest(built.Assignment.ConnectionId, TrackerQueryKinds.DryRun, Assignment: built.Assignment)),
                Json, ct);
            response.EnsureSuccessStatusCode();
            var relayed = await response.Content.ReadFromJsonAsync<RelayTrackerQueryResponse>(Json, ct);
            if (relayed is not { Supported: true, Result: { } result })
                return (new TrackerImportPreviewOutcome(false, false), null);
            return result.Ok
                ? (null, result.Tickets ?? [])
                : (new TrackerImportPreviewOutcome(true, false, result.Error), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Ticket import preview relay failed for project {ProjectId}", projectId);
            return (new TrackerImportPreviewOutcome(false, false), null);
        }
    }
}

// Wire shapes mirroring the host's RunnerRelayEndpoints, file-scoped as the other relay callers do.
file sealed record RelayTrackerQueryRequest(string OrgId, string UserId, TrackerQueryRequest Query);

file sealed record RelayTrackerQueryResponse(bool Supported, TrackerQueryResult? Result);
