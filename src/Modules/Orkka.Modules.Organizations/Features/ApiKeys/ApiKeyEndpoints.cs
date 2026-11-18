namespace Orkka.Modules.Organizations.Features.ApiKeys;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Organizations.Domain;
using Orkka.Modules.Organizations.Features.Members;
using Orkka.Modules.Organizations.Infrastructure;
using Orkka.SharedKernel.Security;

public sealed record ApiKeyResponse(
    Ulid Id,
    string Name,
    string Prefix,
    IReadOnlyList<string> Permissions,
    Ulid? ProjectId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    bool Revoked);

public sealed record CreateApiKeyRequest(
    string Name,
    /// <summary>Permission names (subset of the caller's own; Owners may grant any).</summary>
    List<string> Permissions,
    /// <summary>Optional: restrict the key to one project (slug or id).</summary>
    string? ProjectId = null);

/// <summary>The raw key appears here exactly once.</summary>
public sealed record CreateApiKeyResponse(ApiKeyResponse Key, string RawKey);

internal static class ApiKeyMappers
{
    public static ApiKeyResponse ToResponse(this OrgApiKey k) =>
        new(k.Id, k.Name, k.Prefix, k.Permissions, k.ProjectId, k.CreatedAt, k.LastUsedAt, k.RevokedAt is not null);
}

/// <summary>The caller's own keys (revoked ones included, so a leaked-key story is auditable).</summary>
public sealed class ListApiKeysEndpoint(OrganizationsDbContext db, ICurrentUser currentUser)
    : EndpointWithoutRequest<List<ApiKeyResponse>>
{
    public override void Configure() => Get("/api/me/api-keys");

    public override async Task HandleAsync(CancellationToken ct)
    {
        var keys = await db.OrgApiKeys.AsNoTracking()
            .Where(k => k.OrgId == currentUser.OrgId && k.MemberId == currentUser.MemberId)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(ct);
        await Send.OkAsync(keys.Select(k => k.ToResponse()).ToList(), ct);
    }
}

public sealed class CreateApiKeyEndpoint(
    OrganizationsDbContext db,
    ICurrentUser currentUser,
    Orkka.Modules.Projects.Contracts.IProjectResolver projectResolver,
    Orkka.Modules.Agents.Contracts.IAgentAuditLog audit)
    : Endpoint<CreateApiKeyRequest, CreateApiKeyResponse>
{
    private const int MaxActiveKeysPerMember = 10;

    public override void Configure() => Post("/api/me/api-keys");

    public override async Task HandleAsync(CreateApiKeyRequest req, CancellationToken ct)
    {
        // A key made through another key would be escalation-by-chaining; keys are minted by people.
        if (HttpContext.User.HasClaim(OrgApiKeyAuthHandler.ClaimType, "true"))
        {
            ThrowError("API keys can't create API keys — sign in to manage them.", 403);
            return;
        }

        var name = req.Name?.Trim() ?? "";
        if (name.Length is 0 or > 100)
        {
            AddError(r => r.Name, "Name is required (max 100 characters).");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var requested = new HashSet<Permission>();
        foreach (var raw in req.Permissions ?? [])
        {
            if (!Enum.TryParse<Permission>(raw, out var p))
            {
                AddError(r => r.Permissions, $"Unknown permission \"{raw}\".");
                await Send.ErrorsAsync(cancellation: ct);
                return;
            }
            requested.Add(p);
        }
        if (requested.Count == 0)
        {
            AddError(r => r.Permissions, "Pick at least one permission.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }
        // Anti-escalation: a key only carries permissions its creator holds.
        if (!currentUser.IsOwner && !requested.IsSubsetOf(currentUser.Permissions))
        {
            ThrowError("You can only grant permissions you hold yourself.", 403);
            return;
        }

        Ulid? projectId = null;
        if (!string.IsNullOrWhiteSpace(req.ProjectId))
        {
            projectId = await projectResolver.ResolveAsync(req.ProjectId.Trim(), ct);
            if (projectId is null)
            {
                AddError(r => r.ProjectId, "Project not found.");
                await Send.ErrorsAsync(cancellation: ct);
                return;
            }
        }

        var active = await db.OrgApiKeys.CountAsync(
            k => k.OrgId == currentUser.OrgId && k.MemberId == currentUser.MemberId && k.RevokedAt == null, ct);
        if (active >= MaxActiveKeysPerMember)
        {
            ThrowError($"You already have {active} active keys (max {MaxActiveKeysPerMember}). Revoke one first.", 409);
            return;
        }

        var rawKey = ApiKeyValues.Generate();
        var key = new OrgApiKey
        {
            OrgId = currentUser.OrgId,
            MemberId = currentUser.MemberId,
            UserId = currentUser.UserId,
            Name = name,
            Prefix = ApiKeyValues.DisplayPrefix(rawKey),
            KeyHash = ApiKeyValues.Hash(rawKey),
            Permissions = requested.Select(p => p.ToString()).OrderBy(p => p).ToArray(),
            ProjectId = projectId,
        };
        db.OrgApiKeys.Add(key);
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(Orkka.Modules.Agents.Contracts.AgentAuditRecord.ByUser(
            currentUser.OrgId, currentUser.UserId, Orkka.Modules.Agents.Contracts.AgentAuditEventType.ApiKeyCreated,
            $"API key \"{key.Name}\" ({key.Prefix}…) created by {currentUser.UserId}",
            projectId: projectId,
            metadata: new { keyId = key.Id.ToString(), key.Name, key.Prefix, key.Permissions }), ct);

        await Send.OkAsync(new CreateApiKeyResponse(key.ToResponse(), rawKey), ct);
    }
}

public sealed class RevokeApiKeyEndpoint(
    OrganizationsDbContext db,
    ICurrentUser currentUser,
    Orkka.Modules.Agents.Contracts.IAgentAuditLog audit)
    : EndpointWithoutRequest
{
    public override void Configure() => Delete("/api/me/api-keys/{keyId:ulid}");

    public override async Task HandleAsync(CancellationToken ct)
    {
        var keyId = Ulid.Parse(Route<string>("keyId"));
        var key = await db.OrgApiKeys
            .FirstOrDefaultAsync(k => k.Id == keyId && k.OrgId == currentUser.OrgId, ct);
        // Own keys always; Owners can revoke anyone's (a leaked key must not wait for its owner).
        if (key is null || (key.MemberId != currentUser.MemberId && !currentUser.IsOwner))
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        if (key.RevokedAt is null)
        {
            key.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            await audit.RecordAsync(Orkka.Modules.Agents.Contracts.AgentAuditRecord.ByUser(
                currentUser.OrgId, currentUser.UserId, Orkka.Modules.Agents.Contracts.AgentAuditEventType.ApiKeyRevoked,
                $"API key \"{key.Name}\" ({key.Prefix}…) revoked by {currentUser.UserId}",
                projectId: key.ProjectId,
                metadata: new { keyId = key.Id.ToString(), key.Name, key.Prefix, ownerUserId = key.UserId }), ct);
        }
        await Send.NoContentAsync(ct);
    }
}
