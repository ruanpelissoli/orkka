namespace Orkka.Tests.Caching;

using FluentAssertions;
using Orkka.SharedKernel.Caching;

/// <summary>
/// Covers invalidation — the part of the caching layer this codebase actually wrote.
/// </summary>
/// <remarks>
/// Deliberately no TTL-expiry tests: expiry is framework behaviour we do not own, and testing
/// it would mean either sleeping (slow and flaky) or substituting the clock inside HybridCache
/// (which would then no longer be the thing shipping). What can regress here is whether a
/// write path's <c>RemoveAsync</c> actually reaches the entry a read path created — so that is
/// what is asserted, with no <c>Task.Delay</c> anywhere.
/// </remarks>
public sealed class CacheInvalidationTests : IDisposable
{
    private readonly TestCache _cache = new();

    public void Dispose() => _cache.Dispose();

    private ValueTask<int> GetCounterAsync(string key, StrongBox counter) =>
        _cache.Cache.GetOrCreateAsync(
            key,
            _ => ValueTask.FromResult(++counter.Value),
            CachePolicies.Metadata);

    [Fact]
    public async Task SecondRead_DoesNotRunTheFactory()
    {
        var counter = new StrongBox();
        var key = CacheKeys.ProjectSummary(Ulid.NewUlid(), Ulid.NewUlid());

        (await GetCounterAsync(key, counter)).Should().Be(1);
        (await GetCounterAsync(key, counter)).Should().Be(1);

        counter.Value.Should().Be(1, "the value was served from cache, not rebuilt");
    }

    [Fact]
    public async Task ReadAfterInvalidation_RunsTheFactoryAgain()
    {
        var counter = new StrongBox();
        var key = CacheKeys.ProjectSummary(Ulid.NewUlid(), Ulid.NewUlid());

        await GetCounterAsync(key, counter);
        await _cache.Invalidator.RemoveAsync(key);

        (await GetCounterAsync(key, counter)).Should().Be(2);
    }

    [Fact]
    public async Task Invalidation_MatchesTheKeyBuilderUsedToRead()
    {
        // The read path and the write path build the key independently. If versioning were
        // applied on only one of them the eviction would silently miss, and the only symptom
        // would be stale data in production.
        var orgId = Ulid.NewUlid();
        var projectId = Ulid.NewUlid();
        var counter = new StrongBox();

        await GetCounterAsync(CacheKeys.ProjectSummary(orgId, projectId), counter);
        await _cache.Invalidator.RemoveAsync(CacheKeys.ProjectSummary(orgId, projectId));

        (await GetCounterAsync(CacheKeys.ProjectSummary(orgId, projectId), counter))
            .Should().Be(2);
    }

    [Fact]
    public async Task InvalidatingOneOrg_LeavesAnotherOrgsEntryIntact()
    {
        var orgA = Ulid.NewUlid();
        var orgB = Ulid.NewUlid();
        var projectId = Ulid.NewUlid();
        var counter = new StrongBox();

        await GetCounterAsync(CacheKeys.ProjectSummary(orgA, projectId), counter);
        await GetCounterAsync(CacheKeys.ProjectSummary(orgB, projectId), counter);
        counter.Value.Should().Be(2, "the same project id under two orgs is two distinct keys");

        await _cache.Invalidator.RemoveAsync(CacheKeys.ProjectSummary(orgA, projectId));

        (await GetCounterAsync(CacheKeys.ProjectSummary(orgB, projectId), counter))
            .Should().Be(2, "org B's entry was untouched");
    }

    [Fact]
    public async Task BatchInvalidation_EvictsEveryKey()
    {
        var orgId = Ulid.NewUlid();
        var first = CacheKeys.ProjectSummary(orgId, Ulid.NewUlid());
        var second = CacheKeys.ProjectRepos(orgId, Ulid.NewUlid());
        var counter = new StrongBox();

        await GetCounterAsync(first, counter);
        await GetCounterAsync(second, counter);

        await _cache.Invalidator.RemoveAsync([first, second]);

        await GetCounterAsync(first, counter);
        await GetCounterAsync(second, counter);
        counter.Value.Should().Be(4);
    }

    [Fact]
    public async Task RemovingAnUncachedKey_IsANoOp()
    {
        var act = async () =>
            await _cache.Invalidator.RemoveAsync(CacheKeys.ProjectSummary(Ulid.NewUlid(), Ulid.NewUlid()));

        await act.Should().NotThrowAsync();
    }

    private sealed class StrongBox
    {
        public int Value;
    }
}
