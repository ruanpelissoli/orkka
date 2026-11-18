namespace Orkka.Modules.Projects.PublicApi;

using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Caching;
using Orkka.SharedKernel.Security;

/// <summary>
/// Caches the two <see cref="IProjectsApi"/> reads that nearly every endpoint performs:
/// the project summary and its repo list. Everything else delegates untouched.
/// </summary>
/// <remarks>
/// <para><b>API host only.</b> Registered from <c>AddProjectsModuleApi</c>, never in the
/// Worker: the Worker has no <c>ICurrentUser</c>, so <c>ModuleDbContext</c>'s tenant filter is
/// bypassed and its reads span every org. Caching those under an org-keyed key would be a
/// cross-tenant leak.</para>
/// <para>The same reasoning applies within this host to unauthenticated requests — GitHub
/// webhooks and the Platform SDK reach these APIs with no member context — which is why an
/// unauthenticated caller bypasses the cache entirely rather than keying on a default OrgId.</para>
/// </remarks>
internal sealed class CachedProjectsApi(
    ProjectsApi inner,
    IOrkkaCache cache,
    ICacheInvalidator invalidator,
    ICurrentUser currentUser) : IProjectsApi
{
    /// <summary>
    /// False whenever the tenant filter is not in force, in which case a result has no org
    /// identity and must not be given one by being written under an org-scoped key.
    /// </summary>
    private bool CanCache => currentUser.IsAuthenticated;

    public Task<ProjectSummary?> GetProjectSummaryAsync(Ulid projectId, CancellationToken ct = default)
    {
        if (!CanCache)
            return inner.GetProjectSummaryAsync(projectId, ct);

        return cache.GetOrCreateAsync(
            CacheKeys.ProjectSummary(currentUser.OrgId, projectId),
            async token => await inner.GetProjectSummaryAsync(projectId, token),
            CachePolicies.Metadata,
            ct).AsTask();
    }

    public async Task<IReadOnlyList<RepoSummary>> ListReposAsync(Ulid projectId, CancellationToken ct = default)
    {
        if (!CanCache)
            return await inner.ListReposAsync(projectId, ct);

        // Cached as a list rather than a dictionary: the shape has to survive JSON, and the
        // handful of callers that want it keyed build their own lookup anyway.
        return await cache.GetOrCreateAsync(
            CacheKeys.ProjectRepos(currentUser.OrgId, projectId),
            async token => (IReadOnlyList<RepoSummary>)await inner.ListReposAsync(projectId, token),
            CachePolicies.Metadata,
            ct);
    }

    // ---- Uncached: batch reads, cross-org reads, and every write ---------------------
    // Batch forms are left alone on purpose — they are called once per request by design,
    // so caching them would add key churn without removing a round trip.

    public Task<IReadOnlyDictionary<Ulid, string>> GetProjectNamesAsync(
        IReadOnlyCollection<Ulid> projectIds, CancellationToken ct = default)
        => inner.GetProjectNamesAsync(projectIds, ct);

    // Does git work and writes WorkspaceStatus — never cacheable. A cached ListReposAsync can
    // therefore carry a stale Pending for the rest of its short TTL; that is how the in-module
    // read paths have always behaved, and no caller may gate repo access on the flag anyway.
    public Task<string?> EnsureRepoWorkspaceAsync(Ulid repoId, string purpose, CancellationToken ct = default)
        => inner.EnsureRepoWorkspaceAsync(repoId, purpose, ct);

    // Routing reads happen on the worker (no tenant context) every poll; the value changes
    // once per repo. Not cached here: the worker host pins the cache off anyway.
    public Task<IReadOnlyDictionary<Ulid, string?>> GetRepoStacksAsync(
        IReadOnlyCollection<Ulid> repoIds, CancellationToken ct = default)
        => inner.GetRepoStacksAsync(repoIds, ct);

    public Task SetRepoStackAsync(Ulid repoId, string stack, CancellationToken ct = default)
        => inner.SetRepoStackAsync(repoId, stack, ct);

    public Task<IReadOnlyDictionary<Ulid, ProjectSummary>> GetProjectSummariesAsync(
        IReadOnlyCollection<Ulid> projectIds, CancellationToken ct = default)
        => inner.GetProjectSummariesAsync(projectIds, ct);

    public Task<IReadOnlyList<Ulid>> ListProjectIdsAsync(Ulid orgId, CancellationToken ct = default)
        => inner.ListProjectIdsAsync(orgId, ct);

    // Writes through, then drops the repo list it just invalidated: a linked folder that
    // does not appear until the cache expires reads as "the link silently failed", which is
    // the one impression this flow cannot afford.
    public async Task<RepoSummary?> AddLocalRepoAsync(
        Ulid orgId, Ulid projectId, string path, string name, string defaultBranch,
        CancellationToken ct = default)
    {
        var repo = await inner.AddLocalRepoAsync(orgId, projectId, path, name, defaultBranch, ct);
        if (repo is not null)
            await invalidator.RemoveAsync(CacheKeys.ProjectRepos(orgId, projectId), ct);
        return repo;
    }

    public Task<ProjectSummary> CreateProjectAsync(NewProject project, CancellationToken ct = default)
        => inner.CreateProjectAsync(project, ct);

    public async Task<RepoSummary?> AddDiscoveredRepoAsync(
        Ulid orgId, Ulid projectId, DiscoveredRepoRegistration repo, CancellationToken ct = default)
    {
        var added = await inner.AddDiscoveredRepoAsync(orgId, projectId, repo, ct);
        if (added is not null)
            await invalidator.RemoveAsync(CacheKeys.ProjectRepos(orgId, projectId), ct);
        return added;
    }

    public async Task SetRepoMissingAsync(Ulid repoId, DateTimeOffset? missingSince, CancellationToken ct = default)
    {
        await inner.SetRepoMissingAsync(repoId, missingSince, ct);
        await DropRepoListAsync(repoId, ct);
    }

    public async Task SetRepoGitUrlAsync(Ulid repoId, string gitUrl, CancellationToken ct = default)
    {
        await inner.SetRepoGitUrlAsync(repoId, gitUrl, ct);
        await DropRepoListAsync(repoId, ct);
    }

    private async Task DropRepoListAsync(Ulid repoId, CancellationToken ct)
    {
        if (await inner.GetRepoAsync(repoId, ct) is { } repo)
            await invalidator.RemoveAsync(CacheKeys.ProjectRepos(repo.OrgId, repo.ProjectId), ct);
    }

    // WorkspaceStatus rides along in the cached repo list, so the same drop applies. The
    // repo id is all the caller has; the project comes from the row itself.
    public async Task SetRepoWorkspaceStatusAsync(
        Ulid repoId, WorkspaceStatus status, CancellationToken ct = default)
    {
        await inner.SetRepoWorkspaceStatusAsync(repoId, status, ct);

        if (await inner.GetRepoAsync(repoId, ct) is { } repo)
            await invalidator.RemoveAsync(CacheKeys.ProjectRepos(repo.OrgId, repo.ProjectId), ct);
    }

    public Task<RepoSummary?> GetRepoAsync(Ulid repoId, CancellationToken ct = default)
        => inner.GetRepoAsync(repoId, ct);

    public Task<IReadOnlyDictionary<Ulid, RepoSummary>> ListReposForProjectsAsync(
        IReadOnlyCollection<Ulid> projectIds, CancellationToken ct = default)
        => inner.ListReposForProjectsAsync(projectIds, ct);

    public Task<IReadOnlyList<Ulid>> ListLiveRepoIdsAsync(CancellationToken ct = default)
        => inner.ListLiveRepoIdsAsync(ct);

    // A row lock is the serialization mechanism here — caching it would break task numbering.
    public Task<int> ReserveTaskNumbersAsync(Ulid projectId, int count, CancellationToken ct = default)
        => inner.ReserveTaskNumbersAsync(projectId, count, ct);
}
