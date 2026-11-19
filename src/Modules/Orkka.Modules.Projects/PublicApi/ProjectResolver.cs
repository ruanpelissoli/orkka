namespace Orkka.Modules.Projects.PublicApi;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Projects.Infrastructure;
using Orkka.SharedKernel.Caching;
using Orkka.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

public sealed class ProjectResolver(
    ProjectsDbContext db,
    ICurrentUser currentUser,
    IOrkkaCache cache) : IProjectResolver
{
    // Per-request memo in front of the shared cache. Kept because it also short-circuits the
    // repeated cache lookups a single handler makes.
    private readonly Dictionary<string, Ulid?> requestCache = [];

    /// <summary>A project id or slug → its id in the caller's workspace, or null (callers
    /// answer 404).</summary>
    public async Task<Ulid?> ResolveAsync(string idOrSlug, CancellationToken ct = default)
    {
        // Ulid.TryParse accepts ANY 26-character string, so a 26-character slug used to be read as
        // an id and 404 (NEG-030). Orkka writes ids in canonical uppercase Crockford base32 and
        // slugs are lowercase letters, digits and dashes, so an uppercase id with a letter in it
        // can't be a slug. Everything else is looked up as a slug first, then read as an id
        // (a lowercased id, or a digits-only value) if no project has that slug.
        if (IsCanonicalId(idOrSlug) && Ulid.TryParse(idOrSlug, out var ulid))
            return ulid;

        if (requestCache.TryGetValue(idOrSlug, out var memoized))
            return memoized;

        var resolved = await ResolveSlugAsync(idOrSlug, ct);
        if (resolved is null && LooksLikeId(idOrSlug) && Ulid.TryParse(idOrSlug, out var id))
            resolved = id;

        requestCache[idOrSlug] = resolved;
        return resolved;
    }

    /// <summary>A 26-character id in Orkka's own spelling: uppercase Crockford base32 with at
    /// least one letter (an all-digit value could also be a slug).</summary>
    private static bool IsCanonicalId(string value)
        => value.Length == 26 && value.Any(char.IsAsciiLetterUpper) && value.All(IsCrockford);

    /// <summary>A 26-character value in the Crockford alphabet, in either case.</summary>
    private static bool LooksLikeId(string value)
        => value.Length == 26 && value.All(c => IsCrockford(char.ToUpperInvariant(c)));

    private static bool IsCrockford(char c)
        => c is >= '0' and <= '9' || (c is >= 'A' and <= 'Z' && c is not ('I' or 'L' or 'O' or 'U'));

    private async Task<Ulid?> ResolveSlugAsync(string idOrSlug, CancellationToken ct)
    {

        var orgId = currentUser.OrgId;

        // Without a shared cache this is one query per endpoint: opening a task page fires
        // half a dozen requests that each resolve the same slug, and every one of them ran
        // the identical lookup because the memo above only lives for one request.
        //
        // Unauthenticated callers bypass it — no org means no tenant filter, so the result
        // has no org identity to key on.
        var id = currentUser.IsAuthenticated
            ? await cache.GetOrCreateAsync(
                CacheKeys.ProjectSlug(orgId, idOrSlug),
                token => LookupAsync(orgId, idOrSlug, token),
                CachePolicies.Metadata,
                ct)
            : await LookupAsync(orgId, idOrSlug, ct);

        return id;
    }

    private async ValueTask<Ulid?> LookupAsync(Ulid orgId, string slug, CancellationToken ct)
        => await db.Projects.AsNoTracking()
            .Where(p => p.OrgId == orgId && p.Slug == slug)
            .Select(p => (Ulid?)p.Id)
            .FirstOrDefaultAsync(ct);
}
