namespace Orkka.Modules.Runners.Domain;

using Orkka.SharedKernel.Domain;

/// <summary>
/// The last provider readiness a user's runner ever reported, kept after the runner goes
/// away. <see cref="RunnerPresence"/> is deleted on disconnect because its
/// WorkerInstance/ConnectionId drive claim affinity and the relay locator, so it cannot
/// double as memory. This row exists so the execution-target decision ("does their runner
/// have Codex?") has an answer while the runner is offline — otherwise every task created
/// during a reconnect would be routed as if the user had no subscriptions at all.
/// Upserted on every Hello; removed only when the user's last runner token is revoked.
/// </summary>
public sealed class RunnerProviderSnapshot : TenantEntity
{
    public required string UserId { get; set; }

    public required string MachineName { get; set; }

    public string? DaemonVersion { get; set; }

    /// <summary>JSON snapshot of RunnerProviderStatus[] from the Hello at <see cref="CapturedAt"/>.</summary>
    public string ProvidersJson { get; set; } = "[]";

    public DateTimeOffset CapturedAt { get; set; }
}
