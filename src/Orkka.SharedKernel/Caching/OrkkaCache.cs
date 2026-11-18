namespace Orkka.SharedKernel.Caching;

using System.Diagnostics;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;

/// <summary>
/// Applies the key version, records metrics, and honours the kill switch. Everything else is
/// delegated to <see cref="HybridCache"/>.
/// </summary>
internal sealed class OrkkaCache(HybridCache cache, CacheMetrics metrics, IOptions<CacheOptions> options)
    : IOrkkaCache
{
    private readonly CacheOptions _options = options.Value;

    public async ValueTask<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, ValueTask<T>> factory,
        CachePolicy policy,
        CancellationToken ct = default)
    {
        if (!_options.Enabled)
            return await factory(ct);

        // Set from inside the factory rather than inferred afterwards: when two callers race,
        // only one runs the factory and the other joins its result. The joiner did not touch
        // the database, so counting it as a hit is the honest reading.
        var ranFactory = false;
        var started = Stopwatch.GetTimestamp();

        var value = await cache.GetOrCreateAsync(
            CacheKeyVersioning.Apply(key, _options.KeyVersion),
            async token =>
            {
                ranFactory = true;
                return await factory(token);
            },
            policy.ToEntryOptions(),
            cancellationToken: ct);

        var family = CacheFamily.Of(key);
        if (ranFactory)
            metrics.RecordMiss(family, Stopwatch.GetElapsedTime(started));
        else
            metrics.RecordHit(family);

        return value;
    }
}

/// <summary>
/// Evicts from this process — the only cache there is.
/// </summary>
internal sealed class LocalCacheInvalidator(HybridCache cache, CacheMetrics metrics, IOptions<CacheOptions> options)
    : ICacheInvalidator
{
    private readonly CacheOptions _options = options.Value;

    public ValueTask RemoveAsync(string key, CancellationToken ct = default)
        => RemoveAsync([key], ct);

    public async ValueTask RemoveAsync(IReadOnlyCollection<string> keys, CancellationToken ct = default)
    {
        if (keys.Count == 0)
            return;

        await cache.RemoveAsync(
            keys.Select(k => CacheKeyVersioning.Apply(k, _options.KeyVersion)).ToArray(), ct);

        foreach (var key in keys)
            metrics.RecordInvalidation(CacheFamily.Of(key), source: "local");
    }
}

/// <summary>
/// Turns a logical key into a physical one. Kept separate from <see cref="CacheKeys"/> so the
/// key builders stay pure functions with no ambient configuration — which is what lets the
/// architecture test reflect over them.
/// </summary>
internal static class CacheKeyVersioning
{
    public static string Apply(string key, string keyVersion) => $"v{keyVersion}:{key}";
}
