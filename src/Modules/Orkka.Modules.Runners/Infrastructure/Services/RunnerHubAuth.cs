namespace Orkka.Modules.Runners.Infrastructure.Services;

using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Runners.Domain;
using Orkka.Modules.Runners.Infrastructure;

/// <summary>Identity bound to a runner connection from its bearer token — the daemon
/// never self-declares org or user.</summary>
public sealed record RunnerConnectionIdentity(Ulid OrgId, string UserId, Ulid TokenId);

/// <summary>A rotation's outcome: a fresh raw token (shown to the runner once) or null when the
/// presented one is not due, plus when the token the runner should now hold expires.</summary>
public sealed record RunnerTokenRotationResult(string? NewToken, DateTimeOffset ExpiresAt);

/// <summary>
/// Token screening for the hosted runner hub. Scoped (owns a DbContext); the hub runs on
/// the Worker with no user context, so every query ignores the tenant filter and scopes
/// by explicit ids.
/// </summary>
public sealed class RunnerHubAuth(RunnersDbContext db, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Validates an <c>Authorization: Bearer orkr_…</c> header value; null when
    /// missing, malformed, unknown, revoked or expired.</summary>
    public async Task<RunnerConnectionIdentity?> ValidateBearerAsync(
        string? authorizationHeader, CancellationToken ct = default)
    {
        const string bearerPrefix = "Bearer ";
        if (authorizationHeader?.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase) != true)
            return null;

        var raw = authorizationHeader[bearerPrefix.Length..].Trim();
        if (!raw.StartsWith(RunnerTokenValues.Prefix, StringComparison.Ordinal))
            return null;

        var hash = RunnerTokenValues.Hash(raw);
        var now = _clock.GetUtcNow();
        var token = await db.RunnerTokens.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.RevokedAt == null && t.ExpiresAt > now, ct);

        return token is null
            ? null
            : new RunnerConnectionIdentity(token.OrgId, token.UserId, token.Id);
    }

    /// <summary>Heartbeat re-validation: each Hello re-checks the token, so revoking it — or
    /// its expiry — takes a live connection down within one heartbeat (~60s) even when the
    /// revoke API call landed on a different Worker replica.</summary>
    public async Task<bool> IsUnusableAsync(Ulid tokenId, CancellationToken ct = default)
    {
        var token = await db.RunnerTokens.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tokenId, ct);
        return token is null || token.RevokedAt is not null || token.ExpiresAt <= _clock.GetUtcNow();
    }

    /// <summary>
    /// Gives a connected runner a new token when its own is within
    /// <see cref="RunnerTokenValues.RotateWithin"/> of expiry. The old token keeps working for
    /// <see cref="RunnerTokenValues.RotationGrace"/> so the runner can store the new one and
    /// redial; the new one inherits name, org and user. Null token: not due (or not usable).
    /// </summary>
    public async Task<RunnerTokenRotationResult?> RotateIfDueAsync(Ulid tokenId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var current = await db.RunnerTokens.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tokenId, ct);
        if (current is null || current.RevokedAt is not null || current.ExpiresAt <= now)
            return null;
        if (current.ExpiresAt - now > RunnerTokenValues.RotateWithin)
            return new RunnerTokenRotationResult(null, current.ExpiresAt);

        var raw = RunnerTokenValues.Generate();
        var next = new RunnerToken
        {
            OrgId = current.OrgId,
            UserId = current.UserId,
            Name = current.Name,
            TokenHash = RunnerTokenValues.Hash(raw),
            ExpiresAt = now + RunnerTokenValues.Lifetime,
            LastMachineName = current.LastMachineName,
        };
        db.RunnerTokens.Add(next);

        var grace = now + RunnerTokenValues.RotationGrace;
        if (current.ExpiresAt > grace)
            current.ExpiresAt = grace;

        await db.SaveChangesAsync(ct);
        return new RunnerTokenRotationResult(raw, next.ExpiresAt);
    }

    /// <summary>Stamps LastUsedAt/LastMachineName so the tokens UI shows which machine
    /// holds each token.</summary>
    public async Task TouchAsync(Ulid tokenId, string machineName, CancellationToken ct = default)
    {
        await db.RunnerTokens.IgnoreQueryFilters()
            .Where(t => t.Id == tokenId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.LastUsedAt, _clock.GetUtcNow())
                .SetProperty(t => t.LastMachineName, machineName), ct);
    }
}
