namespace Orkka.Modules.Runners.Features.RunnerTokens;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Runners.Domain;
using Orkka.Modules.Runners.Infrastructure;
using Orkka.Modules.Runners.Infrastructure.Services;
using Orkka.SharedKernel.Security;

/// <param name="ExpiresAt">When the token stops working. A connected runner renews its own
/// token before then; an unused one simply lapses.</param>
public sealed record RunnerTokenResponse(
    string Id,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    string? LastMachineName,
    bool Revoked,
    DateTimeOffset ExpiresAt);

public sealed record CreateRunnerTokenRequest(string Name);

/// <summary>Token is the raw orkr_ value — returned exactly once, never retrievable again.</summary>
public sealed record CreateRunnerTokenResponse(string Id, string Name, string Token, DateTimeOffset ExpiresAt);

/// <summary>Lists the caller's own runner tokens. Self-service: every org member manages
/// the tokens for their own machines; no admin grant involved.</summary>
public sealed class ListRunnerTokensEndpoint(
    RunnersDbContext db,
    ICurrentUser currentUser)
    : EndpointWithoutRequest<List<RunnerTokenResponse>>
{
    public override void Configure()
    {
        Get("/api/me/runner-tokens");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var tokens = await db.RunnerTokens.AsNoTracking()
            .Where(t => t.OrgId == currentUser.OrgId && t.UserId == currentUser.UserId)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new RunnerTokenResponse(
                t.Id.ToString(), t.Name, t.CreatedAt, t.LastUsedAt, t.LastMachineName,
                t.RevokedAt != null, t.ExpiresAt))
            .ToListAsync(ct);

        await Send.OkAsync(tokens, ct);
    }
}

public sealed class CreateRunnerTokenEndpoint(
    RunnersDbContext db,
    ICurrentUser currentUser,
    Orkka.Modules.Agents.Contracts.IAgentAuditLog auditLog)
    : Endpoint<CreateRunnerTokenRequest, CreateRunnerTokenResponse>
{
    public override void Configure()
    {
        Post("/api/me/runner-tokens");
    }

    public override async Task HandleAsync(CreateRunnerTokenRequest req, CancellationToken ct)
    {
        var name = req.Name?.Trim() ?? "";
        if (name.Length is 0 or > 100)
        {
            ThrowError("Give the token a name between 1 and 100 characters (e.g. \"Work laptop\").");
            return;
        }

        var raw = RunnerTokenValues.Generate();
        var token = new RunnerToken
        {
            OrgId = currentUser.OrgId,
            UserId = currentUser.UserId,
            Name = name,
            TokenHash = RunnerTokenValues.Hash(raw),
            ExpiresAt = DateTimeOffset.UtcNow + RunnerTokenValues.Lifetime,
        };
        db.RunnerTokens.Add(token);
        await db.SaveChangesAsync(ct);

        await auditLog.RecordAsync(new Orkka.Modules.Agents.Contracts.AgentAuditRecord(
            currentUser.OrgId, null,
            Orkka.Modules.Agents.Contracts.AgentAuditEventType.RunnerTokenCreated,
            $"Runner token \"{token.Name}\" created by {currentUser.UserId}",
            Actor: currentUser.UserId,
            ActorKind: Orkka.Modules.Agents.Contracts.AuditActorKind.User,
            ActorUserId: currentUser.UserId,
            Metadata: new { tokenId = token.Id.ToString(), token.Name }), ct);

        await Send.OkAsync(new CreateRunnerTokenResponse(token.Id.ToString(), token.Name, raw, token.ExpiresAt), ct);
    }
}

/// <summary>Soft-revokes a token. A live connection using it is taken down by the hub's
/// heartbeat re-validation within ~60s (the hub re-checks revocation on every Hello).</summary>
public sealed class RevokeRunnerTokenEndpoint(
    RunnersDbContext db,
    ICurrentUser currentUser,
    Orkka.Modules.Agents.Contracts.IAgentAuditLog auditLog,
    Infrastructure.Services.RunnerPresenceWriter presenceWriter)
    : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("/api/me/runner-tokens/{tokenId:ulid}");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var tokenId = Ulid.Parse(Route<string>("tokenId"));
        var token = await db.RunnerTokens.FirstOrDefaultAsync(
            t => t.Id == tokenId && t.OrgId == currentUser.OrgId
                 && t.UserId == currentUser.UserId, ct);
        if (token is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        token.RevokedAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // No token left = no runner. Its last-known providers must stop steering where new
        // tasks run, or a machine the user retired keeps routing tasks "to their runner".
        var stillHasToken = await db.RunnerTokens.AnyAsync(
            t => t.OrgId == currentUser.OrgId && t.UserId == currentUser.UserId
                 && t.RevokedAt == null, ct);
        if (!stillHasToken)
            await presenceWriter.ForgetProvidersAsync(currentUser.OrgId, currentUser.UserId, ct);

        await auditLog.RecordAsync(new Orkka.Modules.Agents.Contracts.AgentAuditRecord(
            currentUser.OrgId, null,
            Orkka.Modules.Agents.Contracts.AgentAuditEventType.RunnerTokenRevoked,
            $"Runner token \"{token.Name}\" revoked by {currentUser.UserId}",
            Actor: currentUser.UserId,
            ActorKind: Orkka.Modules.Agents.Contracts.AuditActorKind.User,
            ActorUserId: currentUser.UserId,
            Metadata: new { tokenId = token.Id.ToString(), token.Name }), ct);

        await Send.NoContentAsync(ct);
    }
}
