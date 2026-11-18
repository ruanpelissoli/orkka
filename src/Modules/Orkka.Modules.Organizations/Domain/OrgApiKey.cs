namespace Orkka.Modules.Organizations.Domain;

using Orkka.SharedKernel.Domain;

/// <summary>
/// A member-owned API key for the public REST surface and the MCP endpoint. Only the SHA-256
/// hash is stored (same convention as invites and runner tokens); the raw key is shown once.
/// Permissions are a snapshot chosen at creation, capped by what the member held then and
/// intersected with what they hold at use. Optional project restriction narrows reach.
/// </summary>
public sealed class OrgApiKey : TenantEntity
{
    public Ulid MemberId { get; set; }
    public required string UserId { get; set; }
    public required string Name { get; set; }
    /// <summary>First 16 chars of the raw key — what the list shows and how the validator narrows the lookup.</summary>
    public required string Prefix { get; set; }
    public required string KeyHash { get; set; }
    /// <summary>Permission names (SharedKernel <c>Permission</c>), stored as text[].</summary>
    public string[] Permissions { get; set; } = [];
    public Ulid? ProjectId { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public bool IsActive => RevokedAt is null;
}
