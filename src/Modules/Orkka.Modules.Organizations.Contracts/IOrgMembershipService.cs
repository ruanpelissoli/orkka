namespace Orkka.Modules.Organizations.Contracts;

using Orkka.SharedKernel.Security;

/// <param name="Locale">The member's saved interface language ("pt", "es", ...), or null when
/// they never chose one. Carried on the membership so the auth middleware can prefer it over
/// the request's Accept-Language: the header is whatever client happened to send the request,
/// this is what the user actually picked — and it decides the language the agents answer in.</param>
public sealed record MembershipInfo(
    Ulid MemberId, Ulid OrgId, OrgRole Role, IReadOnlyList<Permission> Permissions,
    string? Locale = null);

/// <summary>One of the user's workspaces — an org, typically one per job.</summary>
/// <param name="IsActive">The one a request that names no workspace resolves to.</param>
public sealed record WorkspaceInfo(
    Ulid OrgId, string Name, string Slug, OrgRole Role, bool IsActive);

/// <summary>
/// Membership resolution for auth middleware and hub security. Lookups are cross-org by
/// design (a user id is resolved before any tenant context exists).
/// </summary>
public interface IOrgMembershipService
{
    /// <summary>Resolves the member for a user id in their active workspace (the one they
    /// last switched to), or null when they have no membership.</summary>
    Task<MembershipInfo?> ResolveAsync(string userId, CancellationToken ct = default);

    /// <summary>The user's membership in one named workspace, or null when they are not a member
    /// of it — the answer for a request that carries <c>X-Orkka-Workspace</c>.</summary>
    Task<MembershipInfo?> ResolveInOrgAsync(string userId, Ulid orgId, CancellationToken ct = default);

    /// <summary>Every workspace the user belongs to, active one flagged.</summary>
    Task<IReadOnlyList<WorkspaceInfo>> ListWorkspacesAsync(string userId, CancellationToken ct = default);

    /// <summary>A new workspace with the user as its owner, made active.</summary>
    Task<WorkspaceInfo> CreateWorkspaceAsync(string userId, string name, CancellationToken ct = default);

    /// <summary>Makes <paramref name="orgId"/> the user's active workspace. False when they are
    /// not a member of it.</summary>
    Task<bool> SetActiveWorkspaceAsync(string userId, Ulid orgId, CancellationToken ct = default);

    /// <summary>
    /// Resolves the member for a user id; first-time users get an organization and
    /// an owner (original root) membership provisioned automatically.
    /// </summary>
    Task<MembershipInfo> ResolveOrProvisionAsync(string userId, CancellationToken ct = default);

    /// <summary>OrgId for a user id, or null when the user has no membership.</summary>
    Task<Ulid?> GetOrgIdForUserAsync(string userId, CancellationToken ct = default);
}
