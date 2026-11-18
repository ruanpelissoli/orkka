namespace Orkka.SharedKernel.Caching;

/// <summary>
/// The single place cache keys are constructed. Centralising them is a tenant-safety
/// measure, not a tidiness one: <c>ModuleDbContext</c> filters reads by <c>OrgId</c>, so a
/// cached value carries an org identity that the cache itself cannot see. If a key omits the
/// org, a hit serves one tenant's data to another with no query filter left to stop it.
/// </summary>
/// <remarks>
/// <para>Three key families, distinguished by prefix:</para>
/// <list type="bullet">
/// <item><b>Tenant-scoped</b> — take <c>orgId</c> as their first parameter, always.</item>
/// <item><b>Identity-scoped</b> (<c>auth:</c>) — keyed by user id. These <i>resolve</i>
/// the org, so they cannot be keyed by it.</item>
/// <item><b>Platform-scoped</b> (<c>platform:</c>) — deliberately cross-org, e.g. the AI
/// model catalog. Allow-listed by <c>CacheKeyConventionTests</c>.</item>
/// </list>
/// <para>Keys here are <i>logical</i>. The physical key gets a <c>v{KeyVersion}:</c> prefix
/// from <see cref="OrkkaCache"/> so a payload-shape change cannot collide across a rolling
/// deploy — see <see cref="CacheOptions.KeyVersion"/>.</para>
/// </remarks>
public static class CacheKeys
{
    // ---- Identity-scoped -------------------------------------------------------------

    /// <summary>Org membership, role and permissions for a user — in the workspace a
    /// request named, or (null) their active one.</summary>
    /// <remarks>Identity-scoped, not tenant-scoped: the user comes first and the workspace only
    /// narrows which of their memberships — hence not named <c>orgId</c>.</remarks>
    public static string Membership(string userId, Ulid? workspace = null)
        => workspace is { } org ? $"auth:member:{userId}:{org}" : $"auth:member:{userId}";

    // ---- Platform-scoped (cross-org by design) ---------------------------------------

    /// <summary>The AI model catalog — global, identical for every org.</summary>
    public static string ModelCatalog => "platform:modelcatalog";

    // ---- Tenant-scoped ---------------------------------------------------------------

    /// <summary>Project read model (name, key, settings, markup).</summary>
    public static string ProjectSummary(Ulid orgId, Ulid projectId)
        => $"projects:summary:{orgId}:{projectId}";

    /// <summary>A project's repos, keyed by repo id.</summary>
    public static string ProjectRepos(Ulid orgId, Ulid projectId)
        => $"projects:repos:{orgId}:{projectId}";

    /// <summary>Slug → project id. Slugs are unique per org, never globally.</summary>
    public static string ProjectSlug(Ulid orgId, string slug)
        => $"projects:slug:{orgId}:{slug}";
}
