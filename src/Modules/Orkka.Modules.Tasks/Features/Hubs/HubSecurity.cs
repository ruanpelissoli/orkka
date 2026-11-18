namespace Orkka.Modules.Tasks.Features.Hubs;

using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.Modules.Agents.Contracts;
using Orkka.SharedKernel.Storage;
using Orkka.SharedKernel.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using System.Security.Claims;
using Orkka.Modules.Organizations.Contracts;

/// <summary>
/// Authorization helpers for SignalR hubs.
///
/// Hub method invocations arrive over the websocket, not through the HTTP middleware pipeline, so
/// <c>ICurrentUser</c> (which reads <c>HttpContext.Items</c>) is not reliably populated here. We
/// therefore resolve the caller's org directly from their user identity claim via the
/// Organizations module, and gate worker-only broadcast methods on the service-account claim.
/// </summary>
public static class HubSecurity
{
    /// <summary>True when the caller authenticated with the Worker API key (service account).</summary>
    public static bool IsServiceAccount(ClaimsPrincipal? user) =>
        user?.HasClaim("service_account", "true") is true;

    /// <summary>
    /// The caller's user id from validated claims (user id, or the fixed local-edition id) —
    /// null/empty for service accounts and anonymous callers.
    /// </summary>
    public static string? GetUserId(ClaimsPrincipal? user) =>
        user?.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? user?.FindFirstValue("sub");

    /// <summary>
    /// Resolves the caller's OrgId from their user id claim, or null if the caller is not a
    /// resolvable end user (e.g. a service account, or a user with no org membership).
    /// </summary>
    public static Task<Ulid?> ResolveOrgIdAsync(
        ClaimsPrincipal? user, IOrgMembershipService membership, CancellationToken ct = default)
    {
        var userId = user?.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user?.FindFirstValue("sub");

        if (string.IsNullOrEmpty(userId))
            return Task.FromResult<Ulid?>(null);

        return membership.GetOrgIdForUserAsync(userId, ct);
    }

    /// <summary>
    /// Full membership (member id + role, not just the org) for callers that need to check
    /// project-scoped access — the org alone cannot answer "may they reach this project".
    /// <paramref name="workspace"/> is the hub connection's <c>?workspace=</c> (the org id of
    /// the workspace the client is showing — one per job); without it, the user's active one.
    /// A named workspace the user does not belong to resolves to null: access denied.
    /// </summary>
    public static Task<MembershipInfo?> ResolveMembershipAsync(
        ClaimsPrincipal? user, IOrgMembershipService membership, CancellationToken ct = default,
        string? workspace = null)
    {
        var userId = user?.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user?.FindFirstValue("sub");

        if (string.IsNullOrEmpty(userId))
            return Task.FromResult<MembershipInfo?>(null);
        if (string.IsNullOrWhiteSpace(workspace))
            return membership.ResolveAsync(userId, ct);
        return Ulid.TryParse(workspace, out var orgId)
            ? membership.ResolveInOrgAsync(userId, orgId, ct)
            : Task.FromResult<MembershipInfo?>(null);
    }
}
