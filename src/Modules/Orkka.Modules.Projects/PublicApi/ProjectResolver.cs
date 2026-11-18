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
        if (Ulid.TryParse(idOrSlug, out var ulid))
            return ulid;

        if (requestCache.TryGetValue(idOrSlug, out var memoized))
            return memoized;

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

        requestCache[idOrSlug] = id;
        return id;
    }

    private async ValueTask<Ulid?> LookupAsync(Ulid orgId, string slug, CancellationToken ct)
        => await db.Projects.AsNoTracking()
            .Where(p => p.OrgId == orgId && p.Slug == slug)
            .Select(p => (Ulid?)p.Id)
            .FirstOrDefaultAsync(ct);
}
