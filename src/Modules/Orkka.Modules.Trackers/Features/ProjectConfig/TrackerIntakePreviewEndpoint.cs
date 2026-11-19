namespace Orkka.Modules.Trackers.Features.ProjectConfig;

using System.Net.Http.Json;
using System.Text.Json;
using FastEndpoints;
using Microsoft.Extensions.Logging;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Runners.Contracts;
using Orkka.Modules.Trackers.Contracts;
using Orkka.SharedKernel.Security;

/// <summary>Supported=false: the owner's runner is offline (or too old to answer with tickets).
/// Ok=false: the tracker refused the read (Error says why, in its words). Preview is null in
/// both cases.</summary>
public sealed record TrackerIntakePreviewResponse(
    bool Supported, bool Ok, string? Error = null, TrackerIntakePreview? Preview = null);

/// <summary>
/// "These N tickets would be pulled": the project's saved ticket source, read by its owner's
/// runner exactly as the next poll would read it (the same assignment, the same reads, the same
/// normalization — <c>dry-run</c>), then put through the real poll's intake decision
/// (<c>TrackersApi.PlanIntake</c>: Stories and their member sub-tasks, plain tasks) and the
/// routing rules. Nothing is created, leased, cached or remembered; the tracker is only read.
/// Works before Start — that is what it is for.
/// </summary>
public sealed class TrackerIntakePreviewEndpoint(
    IProjectResolver projectResolver,
    ITrackersApi trackersApi,
    ICurrentUser currentUser,
    IRunnerRelayLocator relayLocator,
    IHttpClientFactory httpClientFactory,
    ILogger<TrackerIntakePreviewEndpoint> logger)
    : EndpointWithoutRequest<TrackerIntakePreviewResponse>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The poll itself is bounded at two minutes on the runner; the hop allows for it.</summary>
    internal static readonly TimeSpan RelayTimeout = TimeSpan.FromMinutes(3);

    public override void Configure() => Post("/api/projects/{projectId}/tracker/preview");

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (await projectResolver.ResolveAsync(Route<string>("projectId")!, ct) is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var built = await trackersApi.BuildPreviewAssignmentAsync(projectId, ct);
        if (built is null)
        {
            ThrowError("This project has no ticket source yet — save one first.", 404);
            return;
        }
        // Only the owner's machine holds the token the poll reads with.
        if (built.OwnerUserId != currentUser.UserId)
        {
            await Send.OkAsync(new TrackerIntakePreviewResponse(true, false,
                "Only the ticket source's owner can preview it — their runner holds the tracker token."), ct);
            return;
        }

        var relay = await relayLocator.LocateAsync(currentUser.OrgId, currentUser.UserId, ct);
        if (relay is null)
        {
            await Send.OkAsync(new TrackerIntakePreviewResponse(false, false), ct);
            return;
        }

        RelayTrackerQueryResponse? relayed;
        try
        {
            var client = httpClientFactory.CreateClient(RunnerRelay.HttpClientName);
            client.Timeout = RelayTimeout;
            using var response = await client.PostAsJsonAsync(
                $"{relay.BaseUrl}/internal/runner/tracker-query",
                new RelayTrackerQueryRequest(currentUser.OrgId.ToString(), relay.UserId,
                    new TrackerQueryRequest(built.Assignment.ConnectionId, TrackerQueryKinds.DryRun,
                        Assignment: built.Assignment)),
                Json, ct);
            response.EnsureSuccessStatusCode();
            relayed = await response.Content.ReadFromJsonAsync<RelayTrackerQueryResponse>(Json, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Ticket source dry run relay failed for project {ProjectId}", projectId);
            await Send.OkAsync(new TrackerIntakePreviewResponse(false, false), ct);
            return;
        }

        if (relayed is not { Supported: true, Result: { } result })
        {
            await Send.OkAsync(new TrackerIntakePreviewResponse(false, false), ct);
            return;
        }
        if (!result.Ok)
        {
            await Send.OkAsync(new TrackerIntakePreviewResponse(true, false, result.Error), ct);
            return;
        }

        var preview = await trackersApi.PreviewIntakeAsync(projectId, result.Tickets ?? [], ct);
        await Send.OkAsync(new TrackerIntakePreviewResponse(true, preview is not null, null, preview), ct);
    }
}

// Wire shapes mirroring the host's RunnerRelayEndpoints, file-scoped as the other relay callers do.
file sealed record RelayTrackerQueryRequest(string OrgId, string UserId, TrackerQueryRequest Query);

file sealed record RelayTrackerQueryResponse(bool Supported, TrackerQueryResult? Result);
