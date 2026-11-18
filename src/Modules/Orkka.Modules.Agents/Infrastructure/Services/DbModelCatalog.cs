namespace Orkka.Modules.Agents.Infrastructure.Services;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Agents.Contracts;
using Orkka.SharedKernel.Caching;

/// <summary>
/// DB-backed model catalog, editable at runtime from the admin UI. Cached so model resolution
/// never adds a per-call query; the admin write endpoints invalidate it explicitly, which the
/// invalidation backplane propagates to the other hosts.
///
/// Degrades to the shipped catalog rather than to nothing: an empty table (fresh deploy where
/// the migration ran but the seeder hasn't) or an unreachable database would otherwise empty
/// every model picker and fail every task create. The log line is the alarm.
/// </summary>
public sealed class DbModelCatalog(
    DbContextOptions<AgentsDbContext> dbOptions,
    IOrkkaCache cache,
    ILogger<DbModelCatalog> logger) : IModelCatalog
{
    public static string CacheKey => CacheKeys.ModelCatalog;

    public async ValueTask<ModelCatalogSnapshot> GetAsync(CancellationToken ct = default)
    {
        try
        {
            // The cached value is the payload, never the snapshot — see ModelCatalogPayload for
            // why the snapshot itself cannot survive a cache round-trip.
            var payload = await cache.GetOrCreateAsync(
                CacheKeys.ModelCatalog,
                async token => await LoadAsync(token),
                CachePolicies.PlatformReference,
                ct);

            return payload is { Models.Count: > 0 }
                ? payload.ToSnapshot()
                : ModelCatalogSnapshot.Static;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Model catalog read failed; serving the shipped catalog until the database recovers");
            return ModelCatalogSnapshot.Static;
        }
    }

    private async Task<ModelCatalogPayload> LoadAsync(CancellationToken ct)
    {
        // A short-lived context, like DbPlatformRateProvider: the cached payload outlives the
        // request scope that built it.
        await using var db = new AgentsDbContext(dbOptions);

        var rows = await db.AiModels
            .AsNoTracking()
            .OrderBy(m => m.SortOrder)
            .ThenBy(m => m.ModelId)
            .Select(m => new AiModelEntry(
                m.ModelId, m.DisplayName, m.Provider, m.Kind, m.Description, m.SupportsMcp,
                m.IsEnabled, m.RequiresRunner, m.ImageProviderKey,
                m.InputRatePer1M, m.OutputRatePer1M, m.SortOrder,
                m.CacheReadRatePer1M, m.CacheWriteRatePer1M))
            .ToListAsync(ct);

        // An empty list is cached as-is and read back as the shipped catalog by the caller;
        // skipping the second query here just avoids work that could not be used.
        if (rows.Count == 0)
            return new ModelCatalogPayload(rows, []);

        var tiers = await db.AiTierDefaults
            .AsNoTracking()
            .Select(t => new AiTierDefaultEntry(t.Provider, t.Tier, t.ModelId))
            .ToListAsync(ct);

        return new ModelCatalogPayload(rows, tiers);
    }
}
