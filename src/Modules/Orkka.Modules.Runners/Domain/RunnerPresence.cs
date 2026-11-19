namespace Orkka.Modules.Runners.Domain;

using Orkka.SharedKernel.Domain;

/// <summary>
/// One row per (org, user) with a connected runner. Written by the hub in whichever
/// Worker replica owns the WebSocket; read by the API host (roster/status endpoints)
/// and the task claim filter (a replica only claims a UserRunner task when this row
/// names it as <see cref="WorkerInstance"/> and is fresh). The 60s re-Hello is the
/// heartbeat — rows older than RunnerPresenceRules.OnlineWindow count as offline.
/// </summary>
public sealed class RunnerPresence : TenantEntity
{
    public required string UserId { get; set; }

    /// <summary>Machine name of the Worker pod holding the connection (stable per
    /// StatefulSet replica) — the claim-affinity key.</summary>
    public required string WorkerInstance { get; set; }

    /// <summary>SignalR connection id — disconnect cleanup deletes the row only when it
    /// still matches, so a reconnect that already moved the row elsewhere survives the
    /// old connection's teardown.</summary>
    public required string ConnectionId { get; set; }

    public required string MachineName { get; set; }

    public string? DaemonVersion { get; set; }

    /// <summary>The RunnerToken this connection authenticated with.</summary>
    public Ulid TokenId { get; set; }

    public DateTimeOffset ConnectedAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>JSON snapshot of RunnerProviderStatus[] from the last Hello.</summary>
    public string ProvidersJson { get; set; } = "[]";

    /// <summary>JSON snapshot of the capability strings from the last Hello.</summary>
    public string? CapabilitiesJson { get; set; }

    /// <summary>JSON snapshot of RunnerGitIdentity from the last Hello — which GitHub account
    /// this machine pushes as. Null from runners older than the field. Logins only, no tokens.</summary>
    public string? GitIdentityJson { get; set; }

    /// <summary>JSON snapshot of the forge CLIs (gh, az, glab) from the last Hello — installed,
    /// signed in, as whom. Null from runners older than the field. Logins only, no tokens.</summary>
    public string? ForgeClisJson { get; set; }

    /// <summary>JSON array of CommandApproval from the last Hello — repository commands the
    /// machine refused and nobody has approved there yet (newest first, at most 20). Approval
    /// itself is only ever `./orkka commands approve` on that machine.</summary>
    public string? PendingCommandApprovalsJson { get; set; }

    /// <summary>JSON snapshot of RunnerCommitIdentity from the last Hello — who this workspace's
    /// commits are authored as (name, email, where it comes from). Null from older runners.</summary>
    public string? CommitIdentityJson { get; set; }
}
