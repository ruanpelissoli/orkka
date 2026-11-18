namespace Orkka.SharedKernel.Caching;

using Microsoft.Extensions.Caching.Hybrid;

/// <summary>How long an entry lives. The cache is in-process only.</summary>
public sealed record CachePolicy(
    TimeSpan LocalExpiration,
    TimeSpan Expiration)
{
    public HybridCacheEntryOptions ToEntryOptions() => new()
    {
        LocalCacheExpiration = LocalExpiration,
        Expiration = Expiration,
        Flags = HybridCacheEntryFlags.DisableDistributedCacheRead
            | HybridCacheEntryFlags.DisableDistributedCacheWrite
    };
}

/// <summary>
/// Named presets, so a TTL is a reviewed decision in one file rather than a magic number
/// scattered across endpoints.
/// </summary>
public static class CachePolicies
{
    /// <summary>
    /// Identity and permissions. Short because it gates authorization: if the backplane drops
    /// an invalidation, a revoked permission must still lapse quickly. Explicit invalidation
    /// on every membership write makes the common case immediate.
    /// </summary>
    public static readonly CachePolicy Auth = new(
        LocalExpiration: TimeSpan.FromSeconds(30),
        Expiration: TimeSpan.FromSeconds(30));

    /// <summary>
    /// Project/repo metadata: read by nearly every endpoint, written rarely.
    /// </summary>
    /// <remarks>
    /// 30s deliberately, not longer: it is the staleness contract SCALING_ROLLOUT.md already
    /// documents for this data ("board repo names can lag 30s"). Sharing that budget across
    /// every endpoint instead of the board alone is the win here — not a longer window.
    /// </remarks>
    public static readonly CachePolicy Metadata = new(
        LocalExpiration: TimeSpan.FromSeconds(30),
        Expiration: TimeSpan.FromSeconds(30));

    /// <summary>Global reference data (the model catalog).</summary>
    public static readonly CachePolicy PlatformReference = new(
        LocalExpiration: TimeSpan.FromSeconds(60),
        Expiration: TimeSpan.FromSeconds(60));
}
