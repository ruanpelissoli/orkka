namespace Orkka.SharedKernel.Caching;

/// <summary>
/// Read-through cache. A thin layer over <c>HybridCache</c> — it does not reimplement any of
/// the hard parts (two-tier storage, stampede protection, serialization), it only adds the
/// three things every call site would otherwise have to remember: key versioning, per-family
/// metrics, and the global kill switch.
/// </summary>
public interface IOrkkaCache
{
    /// <summary>
    /// Returns the cached value, invoking <paramref name="factory"/> only on a miss.
    /// Concurrent callers for the same key share one factory invocation.
    /// </summary>
    /// <param name="key">Built by <see cref="CacheKeys"/> — never inline.</param>
    ValueTask<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, ValueTask<T>> factory,
        CachePolicy policy,
        CancellationToken ct = default);
}

/// <summary>
/// Write-side counterpart to <see cref="IOrkkaCache"/>. Separate because the set of components
/// that <i>invalidate</i> (write endpoints, worker processors) barely overlaps with the set
/// that reads, and because the distributed implementation additionally publishes to peers.
/// </summary>
public interface ICacheInvalidator
{
    ValueTask RemoveAsync(string key, CancellationToken ct = default);

    ValueTask RemoveAsync(IReadOnlyCollection<string> keys, CancellationToken ct = default);
}
