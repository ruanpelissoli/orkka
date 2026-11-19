namespace Orkka.LocalRunner.Trackers;

using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR.Client;
using Orkka.LocalRunner.Contracts;

/// <summary>The two host methods the poller calls. An interface so the loop is testable
/// without a hub.</summary>
public interface ITrackerHub
{
    bool IsConnected { get; }

    /// <summary>The Hello's InstanceId — the poll lease holder.</summary>
    string InstanceId { get; }

    Task<TrackerSyncResponse?> SyncAsync(TrackerSyncRequest request, CancellationToken ct);
    Task<TrackerReportAck?> ReportAsync(TrackerTicketReport report, CancellationToken ct);
    Task<TrackerReportAck?> AckTransitionsAsync(TrackerTransitionReport report, CancellationToken ct);
}

/// <summary>The runner's live hub connection, invoked like <c>RequestApproval</c> /
/// <c>CallProjectTool</c>.</summary>
public sealed class RunnerTrackerHub(RunnerHostLink hostLink, string instanceId) : ITrackerHub
{
    public bool IsConnected => hostLink.Connection is { State: HubConnectionState.Connected };

    public string InstanceId => instanceId;

    public async Task<TrackerSyncResponse?> SyncAsync(TrackerSyncRequest request, CancellationToken ct)
        => await Connection().InvokeAsync<TrackerSyncResponse?>(RunnerProtocol.SyncTrackers, request, ct);

    public async Task<TrackerReportAck?> ReportAsync(TrackerTicketReport report, CancellationToken ct)
        => await Connection().InvokeAsync<TrackerReportAck?>(RunnerProtocol.ReportTickets, report, ct);

    public async Task<TrackerReportAck?> AckTransitionsAsync(TrackerTransitionReport report, CancellationToken ct)
        => await Connection().InvokeAsync<TrackerReportAck?>(RunnerProtocol.AckTransitions, report, ct);

    private HubConnection Connection()
        => hostLink.Connection is { State: HubConnectionState.Connected } connection
            ? connection
            : throw new InvalidOperationException("The host link is down.");
}

/// <summary>The result of polling one assignment. <see cref="Commit"/> records what this
/// poll delivered (the tickets' updated times, the cache's seen markers); the caller runs it
/// only once the host accepted the report, so a lost report never loses comments.</summary>
public sealed record TrackerPollOutcome(TrackerTicketReport Report, string Summary, Action Commit);

/// <summary>
/// Polls one <see cref="TrackerPollAssignment"/>: fetches the complete ticket set, caches
/// attachments, fetches the comments of tickets that are new or whose updated time moved,
/// and normalizes everything into a <see cref="TrackerTicketReport"/>. Any failure yields an
/// <c>Ok=false</c> report with a scrubbed error and NO tickets — a partial set marked Ok
/// would tell the host the missing tickets left.
/// </summary>
public sealed class TrackerTicketPoller(
    TrackerStore store,
    IWorkspaceSecretStore secrets,
    Func<string, ITrackerTicketSource> sources,
    TrackerAttachmentCache cache,
    ILogger<TrackerTicketPoller> logger,
    TimeProvider? timeProvider = null,
    TrackerCredentialResolver? credentials = null)
{
    private readonly TrackerCredentialResolver _credentials = credentials ?? new TrackerCredentialResolver(secrets);

    public const int MaxComments = 30;
    public static readonly TimeSpan AssignmentTimeout = TimeSpan.FromMinutes(2);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    // "{configId}|{key}" → the tracker's updated time this runner last DELIVERED. In memory:
    // after a restart every ticket's comments are fetched once more, filtered by the host's
    // LastCommentAt, which is cheap and never loses one.
    private readonly ConcurrentDictionary<string, DateTimeOffset?> _deliveredUpdatedAt = new(StringComparer.Ordinal);

    internal TimeSpan Timeout { get; init; } = AssignmentTimeout;

    public async Task<TrackerPollOutcome> PollAsync(TrackerPollAssignment assignment, string instanceId, CancellationToken ct)
    {
        var polledAt = _time.GetUtcNow();
        TrackerCredential? credential = null;
        try
        {
            var entry = store.Get(assignment.ConnectionId)
                ?? throw new TrackerException($"This machine has no tracker connection '{assignment.ConnectionId}'.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            var token = timeout.Token;
            credential = await _credentials.ResolveAsync(entry, token);
            var source = sources(entry.Kind);

            var raw = await source.FetchTicketsAsync(credential, assignment, token);
            var known = assignment.Known
                .GroupBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var tickets = new List<TrackerTicket>(raw.Count);
            var delivered = new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal);
            int added = 0, changed = 0;
            foreach (var r in raw.DistinctBy(t => t.Key, StringComparer.OrdinalIgnoreCase))
            {
                known.TryGetValue(r.Key, out var k);
                var names = TrackerAttachmentCache.PlanNames(r.Attachments);
                var attachments = await cache.EnsureAsync(credential, assignment.ConnectionId, r.Key, r.Attachments, names, token);

                var stateKey = $"{assignment.ConfigId}|{r.Key}";
                var moved = k is null
                    || !_deliveredUpdatedAt.TryGetValue(stateKey, out var lastDelivered)
                    || lastDelivered != r.UpdatedAt;
                var comments = moved
                    ? await source.FetchCommentsAsync(credential, r, k?.LastCommentAt, MaxComments, token)
                    : [];

                var ticket = TicketNormalizer.Normalize(r, names, attachments, k?.ContentHash, comments);
                tickets.Add(ticket);
                delivered[stateKey] = r.UpdatedAt;
                if (k is null)
                    added++;
                else if (ticket.Body is not null)
                    changed++;
            }

            var report = new TrackerTicketReport(assignment.ConfigId, instanceId, true, null, polledAt, [.. tickets]);
            var summary = $"{assignment.Scope}: {tickets.Count} ticket{(tickets.Count == 1 ? "" : "s")}, {added} new, {changed} changed";
            return new TrackerPollOutcome(report, summary, () =>
            {
                foreach (var (key, updatedAt) in delivered)
                    _deliveredUpdatedAt[key] = updatedAt;
                foreach (var ticket in tickets)
                    cache.MarkSeen(assignment.ConnectionId, ticket.Key);
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var message = ex switch
            {
                TrackerException => ex.Message,
                OperationCanceledException => $"The poll did not finish within {Timeout.TotalMinutes:0.#} minutes.",
                _ => $"The poll failed: {ex.Message}",
            };
            message = credential?.Scrub(message) ?? message;
            logger.LogDebug("Tracker poll of {ConfigId} failed: {Error}", assignment.ConfigId, message);
            return new TrackerPollOutcome(
                new TrackerTicketReport(assignment.ConfigId, instanceId, false, message, polledAt, []),
                $"{assignment.Scope}: poll failed — {message}",
                () => { });
        }
    }
}

/// <summary>
/// Every 30 s, while the hub is connected and this machine holds a tracker connection: asks
/// the host which ticket sources to poll (<c>SyncTrackers</c>, which also takes the poll
/// lease), first applies the status moves and pull-request checks it hands back
/// (<c>AckTransitions</c> — bounded, so they never wait behind a slow poll), then polls the
/// assignments one after another and reports each (<c>ReportTickets</c>). One assignment's
/// failure never stops the others. A host without these methods is left alone for ten minutes.
/// </summary>
public sealed class TrackerPollerService(
    ITrackerHub hub,
    TrackerStore store,
    TrackerTicketPoller poller,
    TrackerWriteBack writeBack,
    TrackerAttachmentCache cache,
    ILogger<TrackerPollerService> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MissingMethodBackoff = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(6);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private DateTimeOffset _backoffUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogDebug("Tracker poll cycle failed: {Error}", ex.Message);
            }
            try
            {
                await Task.Delay(Interval, _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One cycle. Returns how many assignments were polled.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (now < _backoffUntil || !hub.IsConnected)
            return 0;

        string[] connectionIds;
        try
        {
            connectionIds = [.. store.List().Select(e => e.Id)];
        }
        catch (Exception ex)
        {
            logger.LogDebug("Could not read the tracker connections: {Error}", ex.Message);
            return 0;
        }
        if (connectionIds.Length == 0)
            return 0;

        PruneIfDue(now);

        TrackerSyncResponse? sync;
        try
        {
            sync = await hub.SyncAsync(new TrackerSyncRequest(connectionIds, hub.InstanceId,
                [TrackerSyncFeatures.Boards, TrackerSyncFeatures.GitHub]), ct);
        }
        catch (Exception ex) when (IsMissingMethod(ex))
        {
            BackOff(now);
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogDebug("Could not ask the host which tickets to poll: {Error}", ex.Message);
            return 0;
        }

        await WriteBackAsync(sync, ct);

        var polled = 0;
        foreach (var assignment in sync?.Assignments ?? [])
        {
            var outcome = await poller.PollAsync(assignment, hub.InstanceId, ct);
            polled++;
            if (outcome.Report.Ok)
                logger.LogInformation("{Summary}", outcome.Summary);
            else
                logger.LogWarning("{Summary}", outcome.Summary);

            try
            {
                var ack = await hub.ReportAsync(outcome.Report, ct);
                if (ack is { Accepted: true })
                    outcome.Commit();
                else
                    logger.LogWarning("The host did not accept the tickets for {Scope}: {Error}",
                        assignment.Scope, ack?.Error ?? "no answer");
            }
            catch (Exception ex) when (IsMissingMethod(ex))
            {
                BackOff(now);
                return polled;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning("Could not report the tickets for {Scope}: {Error}", assignment.Scope, ex.Message);
            }
        }
        return polled;
    }

    private async Task WriteBackAsync(TrackerSyncResponse? sync, CancellationToken ct)
    {
        TrackerTransitionReport? report;
        try
        {
            report = await writeBack.ApplyAsync(sync, hub.InstanceId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("Could not apply the tracker status changes: {Error}", ex.Message);
            return;
        }
        if (report is null)
            return;
        try
        {
            var ack = await hub.AckTransitionsAsync(report, ct);
            if (ack is not { Accepted: true })
                logger.LogWarning("The host did not accept the status changes: {Error}", ack?.Error ?? "no answer");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Unacknowledged moves are handed out again next sync; applying one twice is a no-op.
            logger.LogWarning("Could not acknowledge the status changes: {Error}", ex.Message);
        }
    }

    /// <summary>What a host older than ticket polling answers: SignalR's "Unknown hub method"
    /// (or the older "Method does not exist").</summary>
    internal static bool IsMissingMethod(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e.Message.Contains("Unknown hub method", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("Method does not exist", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void BackOff(DateTimeOffset now)
    {
        _backoffUntil = now + MissingMethodBackoff;
        logger.LogInformation("The host does not poll trackers yet — trying again in {Minutes} minutes.",
            (int)MissingMethodBackoff.TotalMinutes);
    }

    private void PruneIfDue(DateTimeOffset now)
    {
        if (now - _lastPrune < PruneInterval)
            return;
        _lastPrune = now;
        try
        {
            var removed = cache.Prune();
            if (removed > 0)
                logger.LogInformation("Removed the cached attachments of {Count} ticket(s) no longer assigned.", removed);
        }
        catch (Exception ex)
        {
            logger.LogDebug("Could not prune the ticket cache: {Error}", ex.Message);
        }
    }
}
