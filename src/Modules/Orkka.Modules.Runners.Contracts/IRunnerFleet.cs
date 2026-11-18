namespace Orkka.Modules.Runners.Contracts;

public static class RunnerPresenceRules
{
    /// <summary>True when a presence row's <c>CapabilitiesJson</c> lists the capability.
    /// A malformed or absent list answers false: an absent capability degrades a feature,
    /// a wrongly assumed one breaks the run that relies on it.</summary>
    public static bool HasCapability(string? capabilitiesJson, string capability)
    {
        if (string.IsNullOrWhiteSpace(capabilitiesJson))
            return false;
        try
        {
            return System.Text.Json.JsonSerializer
                .Deserialize<string[]>(capabilitiesJson)?
                .Contains(capability, StringComparer.Ordinal) == true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>A presence row older than this is treated as offline — the daemon
    /// re-Hellos every 60s, so three missed heartbeats mean the runner (or its Worker
    /// pod) is gone even if the disconnect cleanup never ran.</summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(3);
}

/// <summary>DB-backed presence snapshot for one user's runner. Cross-process by design:
/// the hub (Worker) writes rows, the API host reads them — Online is derived from
/// <see cref="LastSeenAt"/> against <see cref="RunnerPresenceRules.OnlineWindow"/>.</summary>
public sealed record RunnerPresenceSnapshot(
    Ulid OrgId,
    string UserId,
    bool Online,
    string MachineName,
    string? DaemonVersion,
    string WorkerInstance,
    DateTimeOffset ConnectedAt,
    DateTimeOffset LastSeenAt,
    string ProvidersJson,
    /// <summary>The runner's advertised capabilities as a JSON string array, or null for a
    /// runner that reported none. Read with <see cref="RunnerPresenceRules.HasCapability"/>
    /// — chat decides between platform tools and the fenced-block prompt on it, before the
    /// turn is built, and the API pod has no runner link of its own to ask.</summary>
    string? CapabilitiesJson);

/// <summary>Presence reads for other modules (the M3 claim filter checks whether a
/// UserRunner task's owner has a live runner before claiming it).</summary>
public interface IRunnerFleet
{
    Task<RunnerPresenceSnapshot?> GetPresenceAsync(
        Ulid orgId, string userId, CancellationToken ct = default);

    Task<IReadOnlyList<RunnerPresenceSnapshot>> ListPresenceAsync(
        Ulid orgId, CancellationToken ct = default);

    /// <summary>
    /// Which vendor CLIs are signed in on the user's runner — the live Hello when the runner
    /// is online, else the last Hello it ever sent (kept when the presence row is deleted on
    /// disconnect). Null when the runner has never connected, which callers must treat as
    /// "unknown", not "nothing".
    /// </summary>
    Task<RunnerProviderCoverage?> GetProviderCoverageAsync(
        Ulid orgId, string userId, CancellationToken ct = default);
}

/// <summary>The runner's provider readiness as of <see cref="CapturedAt"/>.</summary>
public sealed record RunnerProviderCoverage(
    string MachineName,
    DateTimeOffset CapturedAt,
    bool Online,
    IReadOnlyList<Orkka.LocalRunner.Contracts.RunnerProviderStatus> Providers)
{
    public Orkka.LocalRunner.Contracts.RunnerProviderStatus? Find(string cliKey)
        => Providers.FirstOrDefault(p => string.Equals(p.Provider, cliKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>Usable, not merely authenticated: the CLI must also be on PATH, or the spawn
    /// fails after the readiness said "ready" (the copilot .cmd-shim incident).</summary>
    public bool IsReady(string cliKey) => Find(cliKey) is { CliFound: true, Authenticated: true };
}

/// <summary>
/// Claim affinity: can THIS process stream to the given user's runner right now? The
/// hosted Worker answers from its in-memory connection registry (a UserRunner task is
/// only claimable by the replica holding the WebSocket); other hosts default to true —
/// Orkka Local has exactly one runner and its proxies already degrade gracefully.
/// </summary>
public interface IRunnerClaimAffinity
{
    bool CanExecuteHere(Ulid orgId, string userId);

    /// <summary>Whether the LOCALLY connected runner advertised a capability. False when the
    /// runner is not connected to this process. Hosts without a capability registry (Orkka
    /// Local's single-runner setup) answer true and rely on proxy-level degradation.</summary>
    bool SupportsCapability(Ulid orgId, string userId, string capability);
}
