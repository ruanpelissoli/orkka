namespace Orkka.Modules.Runners.Domain;

using Orkka.SharedKernel.Domain;

/// <summary>
/// A per-user credential for connecting a runner daemon to the hosted runner hub. Only
/// the SHA-256 hex hash is persisted — the raw <c>orkr_…</c> token is shown once at
/// creation and never again. Revocation is a soft delete so the audit trail survives.
/// </summary>
public sealed class RunnerToken : TenantEntity
{
    /// <summary>user id of the runner's owner — the hub binds every connection
    /// presenting this token to (OrgId, UserId), never trusting the daemon's own claims.</summary>
    public required string UserId { get; set; }

    /// <summary>User-chosen label ("Work laptop") — users may hold several tokens.</summary>
    public required string Name { get; set; }

    /// <summary>SHA-256 hex of the raw token.</summary>
    public required string TokenHash { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>Machine name from the last Hello presented with this token.</summary>
    public string? LastMachineName { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// The token stops working at this instant (<see cref="Infrastructure.Services.RunnerTokenValues.Lifetime"/>
    /// after creation). A runner in use never meets it: within
    /// <see cref="Infrastructure.Services.RunnerTokenValues.RotateWithin"/> of expiry the hub hands
    /// it a fresh token, and this one is cut to a short grace period. So a token copied off a
    /// machine — or left on one nobody uses — dies on its own.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
