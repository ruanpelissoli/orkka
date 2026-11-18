namespace Orkka.Runners.Server;

using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

/// <summary>
/// Hosted (multi-tenant) <see cref="IRunnerLinkProvider"/>: one <see cref="RunnerLink"/>
/// per (org, user), created on demand and kept forever as the stable handle for that
/// identity. Semantics per identity match the Local edition's singleton exactly — a
/// second connection from the same user replaces the first; a disconnect fails only that
/// link's in-flight runs. Links here only ever see connections owned by their own
/// identity (the hub binds identity from the bearer token before any registration), so
/// org isolation is structural, not filtered.
/// </summary>
public sealed class MultiTenantRunnerLinkProvider(
    Func<IHubClients> clients,
    ILoggerFactory loggerFactory,
    RunnerLinkMessages messages) : IRunnerLinkProvider
{
    private readonly ConcurrentDictionary<(Ulid OrgId, string UserId), RunnerLink> _links = new();
    private readonly ConcurrentDictionary<string, (Ulid OrgId, string UserId)> _byConnection = new();

    public IRunnerLink GetLink(Ulid orgId, string userId) => GetOrCreate(orgId, userId);

    /// <summary>Connected on THIS Worker replica — the claim-affinity check. Presence
    /// rows answer "connected anywhere"; this answers "can I stream to it from here".</summary>
    public bool IsConnectedLocally(Ulid orgId, string userId) =>
        _links.TryGetValue((orgId, userId), out var link) && link.IsConnected;

    /// <summary>Whether the locally connected runner's Hello advertised a capability —
    /// false when not connected here at all.</summary>
    public bool SupportsCapability(Ulid orgId, string userId, string capability) =>
        _links.TryGetValue((orgId, userId), out var link)
        && link.IsConnected
        && link.Status.Hello?.Capabilities?.Contains(capability) == true;

    /// <summary>Whether this connection is its identity's active runner and said
    /// <paramref name="instanceId"/> in its Hello (see <see cref="RunnerLink.IsActiveInstance"/>).</summary>
    public bool IsActiveInstance(Ulid orgId, string userId, string connectionId, string? instanceId) =>
        _links.TryGetValue((orgId, userId), out var link) && link.IsActiveInstance(connectionId, instanceId);

    /// <summary>Binds a connection to its identity's link on Hello.</summary>
    public void SetRunner(Ulid orgId, string userId, string connectionId, Orkka.LocalRunner.Contracts.RunnerHello hello)
    {
        _byConnection[connectionId] = (orgId, userId);
        GetOrCreate(orgId, userId).SetRunner(connectionId, hello);
    }

    /// <summary>Routes a disconnect to the owning link. Returns the identity so the hub
    /// can clear presence — null when the connection never completed a Hello.</summary>
    public (Ulid OrgId, string UserId)? OnDisconnected(string connectionId)
    {
        if (!_byConnection.TryRemove(connectionId, out var key))
            return null;
        if (_links.TryGetValue(key, out var link))
            link.OnDisconnected(connectionId);
        return key;
    }

    /// <summary>The link owned by the calling connection — chunk uploads resolve through
    /// this, so a runner can only ever feed channels of runs targeted at itself.</summary>
    public bool TryGetLinkByConnection(string connectionId, out IRunnerLink link)
    {
        if (_byConnection.TryGetValue(connectionId, out var key)
            && _links.TryGetValue(key, out var owned))
        {
            link = owned;
            return true;
        }
        link = null!;
        return false;
    }

    private RunnerLink GetOrCreate(Ulid orgId, string userId) =>
        _links.GetOrAdd((orgId, userId), _ =>
            new RunnerLink(clients(), loggerFactory.CreateLogger<RunnerLink>(), messages));
}
