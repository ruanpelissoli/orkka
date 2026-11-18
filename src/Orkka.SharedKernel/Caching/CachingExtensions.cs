namespace Orkka.SharedKernel.Caching;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public static class CachingExtensions
{
    /// <summary>
    /// Registers the in-process cache (HybridCache, L1 only) and its invalidation seam.
    /// </summary>
    /// <remarks>
    /// No <c>ValidateOnStart</c>: every setting is optional by design, and a cache that
    /// refuses to start is strictly worse than one that runs with its defaults.
    /// </remarks>
    public static IServiceCollection AddOrkkaCache(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<CacheOptions>()
            .Bind(configuration.GetSection(CacheOptions.SectionName));

        services.AddSingleton<CacheMetrics>();

        services.AddHybridCache(o =>
        {
            // Oversized entries are logged and skipped rather than stored, which is a safe
            // degrade — but it does mean no feature may assume an entry exists. 512 KB clears
            // every payload cached today by a wide margin.
            o.MaximumPayloadBytes = 512 * 1024;
            o.MaximumKeyLength = 512;
            o.DefaultEntryOptions = CachePolicies.Metadata.ToEntryOptions();
        })
        .AddSerializerFactory<OrkkaJsonSerializerFactory>();

        services.AddSingleton<IOrkkaCache, OrkkaCache>();
        services.AddSingleton<ICacheInvalidator, LocalCacheInvalidator>();

        return services;
    }
}
