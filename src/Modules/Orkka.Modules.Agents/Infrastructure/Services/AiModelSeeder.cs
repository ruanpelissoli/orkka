namespace Orkka.Modules.Agents.Infrastructure.Services;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;

/// <summary>
/// Seeds the shipped model catalog (<see cref="ModelCatalogSeed"/>) into an empty or partially
/// populated table. Inserts only rows that are MISSING — never updates an existing row, so an
/// admin's rename, reprice or disable is permanent and a redeploy can't undo it.
///
/// AppHost and Worker may both run this concurrently; the unique indexes make the race harmless.
/// DbContextOptions is scoped, so this singleton hosted service resolves it via a scope.
/// </summary>
internal sealed class AiModelSeeder(
    IServiceScopeFactory scopeFactory,
    ILogger<AiModelSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbOptions = scope.ServiceProvider
                .GetRequiredService<DbContextOptions<AgentsDbContext>>();
            await using var db = new AgentsDbContext(dbOptions);

            var (addedModels, addedTiers) = await SeedAsync(db, cancellationToken);
            if (addedModels + addedTiers > 0)
                logger.LogInformation(
                    "Seeded {Models} AI model(s) and {Tiers} tier default(s)", addedModels, addedTiers);
        }
        catch (DbUpdateException ex) when (Orkka.SharedKernel.Persistence.SqliteDatabase.IsConstraintViolation(ex.InnerException))
        {
            // The same rows were seeded concurrently — already in the desired state.
        }
        catch (Exception ex)
        {
            // Seeding is a bootstrap convenience; never take the host down for it (e.g.
            // migrations not applied yet). Until it succeeds, DbModelCatalog serves the same
            // shipped catalog from memory, so the app is fully functional meanwhile.
            logger.LogError(ex, "AI model seeding failed; will retry on next start");
        }
    }

    /// <summary>The seeding itself, separated from the hosted-service plumbing so a caller that
    /// controls migration ordering (the integration fixture) can run it directly.</summary>
    internal static async Task<(int Models, int Tiers)> SeedAsync(
        AgentsDbContext db, CancellationToken ct)
    {
        var addedModels = await SeedModelsAsync(db, ct);
        var addedTiers = await SeedTierDefaultsAsync(db, ct);

        if (addedModels + addedTiers > 0)
            await db.SaveChangesAsync(ct);

        return (addedModels, addedTiers);
    }

    private static async Task<int> SeedModelsAsync(AgentsDbContext db, CancellationToken ct)
    {
        var existing = (await db.AiModels
                .AsNoTracking()
                .Select(m => m.ModelId)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = ModelCatalogSeed.Entries
            .Where(e => !existing.Contains(e.ModelId))
            .Select(e => new AiModel
            {
                // Assigned here, not left to ModuleDbContext.SetAuditFields: that runs at
                // SaveChanges, so a batch added with default ids would collide in the change
                // tracker on the second row (same as AgentSeeder.Create).
                Id = Ulid.NewUlid(),
                ModelId = e.ModelId,
                DisplayName = e.DisplayName,
                Provider = e.Provider,
                Kind = e.Kind,
                Description = e.Description,
                SupportsMcp = e.SupportsMcp,
                IsEnabled = e.IsEnabled,
                RequiresRunner = e.RequiresRunner,
                ImageProviderKey = e.ImageProviderKey,
                InputRatePer1M = e.InputRatePer1M,
                OutputRatePer1M = e.OutputRatePer1M,
                SortOrder = e.SortOrder,
                IsBuiltIn = true,
            })
            .ToList();

        db.AiModels.AddRange(missing);
        return missing.Count;
    }

    private static async Task<int> SeedTierDefaultsAsync(AgentsDbContext db, CancellationToken ct)
    {
        var existing = (await db.AiTierDefaults
                .AsNoTracking()
                .Select(t => new { t.Provider, t.Tier })
                .ToListAsync(ct))
            .Select(t => (t.Provider, t.Tier))
            .ToHashSet();

        var missing = ModelCatalogSeed.TierDefaults
            .Where(kv => !existing.Contains((kv.Key.Provider, kv.Key.Tier)))
            .Select(kv => new AiTierDefault
            {
                Id = Ulid.NewUlid(),
                Provider = kv.Key.Provider,
                Tier = kv.Key.Tier,
                ModelId = kv.Value,
            })
            .ToList();

        db.AiTierDefaults.AddRange(missing);
        return missing.Count;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
