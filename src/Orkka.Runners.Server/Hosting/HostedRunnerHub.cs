namespace Orkka.Runners.Server.Hosting;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Runners.Infrastructure.Services;
using Orkka.Runners.Server;

/// <summary>
/// The hub employee runner daemons dial into over the public `runner.` ingress (hybrid
/// mode). Unlike Orkka Local's single-token hub, every connection must present a per-user
/// <c>Authorization: Bearer orkr_…</c> token; the connection is bound server-side to that
/// token's (org, user) — the daemon never self-declares identity, and all subsequent ops
/// route through the identity's own <see cref="RunnerLink"/>, so org isolation is
/// structural. Revocation takedown rides the 60s re-Hello (each Hello re-checks the
/// token), covering revoke calls that landed on a different Worker replica.
/// </summary>
[AllowAnonymous]
public sealed class HostedRunnerHub(
    MultiTenantRunnerLinkProvider links,
    RunnerHubAuth auth,
    RunnerPresenceWriter presence,
    IServiceScopeFactory scopeFactory,
    ProjectToolRegistry projectTools,
    Orkka.Modules.Agents.Contracts.IAgentAuditLog auditLog,
    RunnerRepoWarmer warmer,
    ILogger<HostedRunnerHub> logger) : Hub
{
    private const string IdentityKey = "runner-identity";
    private const string AnnouncedKey = "runner-announced";
    private const string StaleRunsSweptKey = "runner-stale-runs-swept";
    private const string WarmedKey = "runner-repos-warmed";
    // The hub runs several methods of one connection at once (RunnerHubTimeouts
    // .MaxParallelInvocationsPerClient); Hellos stay one at a time, since their once-per-
    // connection steps check-and-set Context.Items.
    private const string HelloGateKey = "runner-hello-gate";

    public override async Task OnConnectedAsync()
    {
        var header = Context.GetHttpContext()?.Request.Headers.Authorization.FirstOrDefault();
        var identity = await auth.ValidateBearerAsync(header, Context.ConnectionAborted);
        if (identity is null)
        {
            logger.LogWarning("[RunnerHub] Connection {Id} rejected: missing, unknown, or revoked token",
                Context.ConnectionId);
            Context.Abort();
            return;
        }

        Context.Items[IdentityKey] = identity;
        Context.Items[HelloGateKey] = new SemaphoreSlim(1, 1);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (links.OnDisconnected(Context.ConnectionId) is { } key)
        {
            await presence.RemoveAsync(key.OrgId, key.UserId, Context.ConnectionId);
            await auditLog.RecordAsync(new Orkka.Modules.Agents.Contracts.AgentAuditRecord(
                key.OrgId, null,
                Orkka.Modules.Agents.Contracts.AgentAuditEventType.RunnerDisconnected,
                $"Runner of {key.UserId} disconnected",
                Actor: "platform",
                ActorKind: Orkka.Modules.Agents.Contracts.AuditActorKind.System,
                ActorUserId: key.UserId));
        }
        await base.OnDisconnectedAsync(exception);
    }

    public async Task Hello(RunnerHello hello)
    {
        if (!Context.Items.TryGetValue(HelloGateKey, out var g) || g is not SemaphoreSlim gate)
        {
            await HelloCoreAsync(hello);
            return;
        }
        await gate.WaitAsync(Context.ConnectionAborted);
        try
        {
            await HelloCoreAsync(hello);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task HelloCoreAsync(RunnerHello hello)
    {
        if (Context.Items[IdentityKey] is not RunnerConnectionIdentity identity)
        {
            Context.Abort();
            return;
        }

        if (hello.ProtocolVersion != RunnerProtocol.ProtocolVersion)
        {
            logger.LogWarning(
                "[RunnerHub] {User}: protocol mismatch (host v{Host}, runner v{Runner}) — runner must update",
                identity.UserId, RunnerProtocol.ProtocolVersion, hello.ProtocolVersion);
            Context.Abort();
            return;
        }

        // Pre-hybrid daemons speak v3 ops but were never meant for a multi-tenant hub.
        if (hello.Capabilities?.Contains(RunnerCapabilities.Hybrid) is not true)
        {
            logger.LogWarning(
                "[RunnerHub] {User}: runner on {Machine} lacks the hybrid capability — update it",
                identity.UserId, hello.MachineName);
            Context.Abort();
            return;
        }

        // Heartbeat re-validation: the revoke endpoint may run on another replica, so the
        // periodic re-Hello is what actually takes a revoked or expired connection down (~60s max).
        if (await auth.IsUnusableAsync(identity.TokenId))
        {
            logger.LogInformation("[RunnerHub] {User}: token revoked or expired — closing connection",
                identity.UserId);
            Context.Abort();
            return;
        }

        links.SetRunner(identity.OrgId, identity.UserId, Context.ConnectionId, hello);
        await presence.UpsertAsync(
            identity.OrgId, identity.UserId, identity.TokenId,
            Environment.MachineName, Context.ConnectionId, hello);
        await auth.TouchAsync(identity.TokenId, hello.MachineName);

        // The tracker connections this machine holds — metadata only; the tokens stay there.
        // Every Hello: a connection added or removed on the machine shows up within a minute.
        if (hello.Trackers is { } trackers)
        {
            using var trackerScope = scopeFactory.CreateScope();
            await trackerScope.ServiceProvider
                .GetRequiredService<Orkka.Modules.Trackers.Contracts.ITrackersApi>()
                .UpsertRunnerConnectionsAsync(identity.OrgId, identity.UserId, hello.MachineName, trackers);
        }

        // Audit once per connection, not per 60s heartbeat re-Hello.
        if (Context.Items.TryAdd(AnnouncedKey, true))
        {
            await auditLog.RecordAsync(new Orkka.Modules.Agents.Contracts.AgentAuditRecord(
                identity.OrgId, null,
                Orkka.Modules.Agents.Contracts.AgentAuditEventType.RunnerConnected,
                $"Runner of {identity.UserId} connected from {hello.MachineName} (daemon {hello.DaemonVersion ?? "unknown"})",
                Actor: "platform",
                ActorKind: Orkka.Modules.Agents.Contracts.AuditActorKind.System,
                ActorUserId: identity.UserId,
                Metadata: new { hello.MachineName, hello.DaemonVersion }));
        }

        // v1 policy: no resume — a run the runner still carries from a PREVIOUS connection is
        // stale (its chunk channel died with that connection). Only on a connection's first
        // Hello: the runner re-Hellos every 60s on the same connection, and those list the
        // runs THIS connection is streaming right now.
        if (Context.Items.TryAdd(StaleRunsSweptKey, true))
        {
            foreach (var staleRunId in hello.ActiveRunIds)
                await Clients.Caller.SendAsync(RunnerProtocol.CancelRun, staleRunId);
        }

        // Unpark: tasks parked while this user's runner was offline — or had no AI CLI
        // signed in — become claimable again. Only once the runner reports a signed-in CLI:
        // this runs on every 60s heartbeat, and re-queueing into a runner that still has
        // nothing to run on would only park the same tasks again. Fire-and-forget on its own
        // scope — Hello must stay fast, and the queue processor picks the re-queued tasks up
        // on its next poll (this replica now holds the connection, so claim affinity is
        // satisfied here).
        if (hello.Providers.Any(p => p.CliFound && p.Authenticated))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var tasksApi = scope.ServiceProvider
                        .GetRequiredService<Orkka.Modules.Tasks.Contracts.ITasksApi>();
                    var requeued = await tasksApi.RequeueWaitingForRunnerTasksAsync(
                        identity.OrgId, identity.UserId, CancellationToken.None);
                    if (requeued > 0)
                        logger.LogInformation(
                            "[RunnerHub] {User}: runner reconnected — re-queued {Count} parked task(s)",
                            identity.UserId, requeued);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "[RunnerHub] Unpark sweep failed for {User} — tasks stay parked until retried",
                        identity.UserId);
                }
            });
        }

        // Warm the runner's repo cache once per connection (see RunnerRepoWarmer): the first
        // chat turn or task then finds its clones ready instead of paying for them.
        if (Context.Items.TryAdd(WarmedKey, true))
            _ = warmer.WarmAsync(identity.OrgId, identity.UserId, CancellationToken.None);
    }

    /// <summary>
    /// A project-context tool call from the runner's agent (see
    /// <see cref="RunnerProtocol.CallProjectTool"/>). Multi-tenant hub, so the run is
    /// resolved through the connection's BOUND org — the task id in the payload is a lookup
    /// key, never an authorization, and a runner can only ever reach runs of its own org.
    /// </summary>
    public async Task<RunnerToolCallResult> CallProjectTool(RunnerToolCallRequest request)
    {
        if (Context.Items[IdentityKey] is not RunnerConnectionIdentity identity)
        {
            return new RunnerToolCallResult(
                "This tool is not available for the current run.", IsError: true);
        }

        return await ProjectToolCalls.HandleAsync(
            projectTools, request, identity.OrgId, logger, Context.ConnectionAborted);
    }

    /// <summary>
    /// The runner renews its token before it expires (<see cref="RunnerProtocol.RotateToken"/>).
    /// Only ever for the token this connection authenticated with — a runner cannot rotate, or
    /// learn about, any other.
    /// </summary>
    public async Task<RunnerTokenRotation?> RotateToken()
    {
        if (Context.Items[IdentityKey] is not RunnerConnectionIdentity identity)
            return null;

        var rotated = await auth.RotateIfDueAsync(identity.TokenId, Context.ConnectionAborted);
        if (rotated?.NewToken is not null)
        {
            logger.LogInformation("[RunnerHub] {User}: runner token renewed (expires {ExpiresAt:u})",
                identity.UserId, rotated.ExpiresAt);
        }

        return rotated is null ? null : new RunnerTokenRotation(rotated.NewToken, rotated.ExpiresAt);
    }

    /// <summary>
    /// The runner asks which ticket sources to poll now (<see cref="RunnerProtocol.SyncTrackers"/>).
    /// Answered for the connection's BOUND org and user only: the sources returned are that
    /// developer's own, polled with the token only their machine holds.
    /// </summary>
    public async Task<TrackerSyncResponse> SyncTrackers(TrackerSyncRequest request)
    {
        if (Context.Items[IdentityKey] is not RunnerConnectionIdentity identity)
            return new TrackerSyncResponse([]);

        using var scope = scopeFactory.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<Orkka.Modules.Trackers.Contracts.ITrackersApi>()
            .SyncAsync(identity.OrgId, identity.UserId, request, Context.ConnectionAborted,
                supersedesOtherInstances: links.IsActiveInstance(
                    identity.OrgId, identity.UserId, Context.ConnectionId, request.InstanceId));
    }

    /// <summary>One poll's tickets (<see cref="RunnerProtocol.ReportTickets"/>). The config id in
    /// the report is checked against the bound user's own sources — never trusted to name one.</summary>
    public async Task<TrackerReportAck> ReportTickets(TrackerTicketReport report)
    {
        if (Context.Items[IdentityKey] is not RunnerConnectionIdentity identity)
            return new TrackerReportAck(false, "Not identified.");

        try
        {
            using var scope = scopeFactory.CreateScope();
            return await scope.ServiceProvider
                .GetRequiredService<Orkka.Modules.Trackers.Contracts.ITrackersApi>()
                .ReportAsync(identity.OrgId, identity.UserId, report, Context.ConnectionAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "[RunnerHub] {User}: ticket report for {ConfigId} failed",
                identity.UserId, report.ConfigId);
            return new TrackerReportAck(false, "The report could not be stored — it is retried on the next poll.");
        }
    }

    /// <summary>What the runner did with the status moves and pull-request checks a sync handed
    /// it (<see cref="RunnerProtocol.AckTransitions"/>) — matched against the bound user's own
    /// tickets only.</summary>
    public async Task<TrackerReportAck> AckTransitions(TrackerTransitionReport report)
    {
        if (Context.Items[IdentityKey] is not RunnerConnectionIdentity identity)
            return new TrackerReportAck(false, "Not identified.");

        try
        {
            using var scope = scopeFactory.CreateScope();
            return await scope.ServiceProvider
                .GetRequiredService<Orkka.Modules.Trackers.Contracts.ITrackersApi>()
                .AckTransitionsAsync(identity.OrgId, identity.UserId, report, Context.ConnectionAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "[RunnerHub] {User}: transition ack failed", identity.UserId);
            return new TrackerReportAck(false, "The acknowledgement could not be stored.");
        }
    }

    /// <summary>Runner uploads a run's chunks. The channel is resolved through the
    /// caller's own link — a runner can only ever feed runs targeted at itself.</summary>
    public async Task StreamRunChunks(string runId, IAsyncEnumerable<RunnerChunk> chunks)
    {
        if (!links.TryGetLinkByConnection(Context.ConnectionId, out var link)
            || !link.TryGetRunChannel(runId, out var channel))
        {
            logger.LogWarning("[RunnerHub] Chunks for unknown run {RunId} from {Conn} — ignoring",
                runId, Context.ConnectionId);
            return;
        }

        await RunnerChunkPump.DrainAsync(runId, chunks, channel, Context.ConnectionAborted, logger);
    }
}
