namespace Orkka.Tests.Features.Models;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Agents.Infrastructure.Services;

/// <summary>
/// The seeder against a real change tracker. The shape of the seed data is asserted in
/// ListModelsEndpointTests; what this covers is the act of tracking and saving it — the whole
/// batch at once, then again on a second boot.
/// </summary>
public sealed class AiModelSeederTests : IDisposable
{
    private readonly DbContextOptions<AgentsDbContext> _dbOptions =
        new DbContextOptionsBuilder<AgentsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private AgentsDbContext NewContext() => new(_dbOptions);

    [Fact]
    public async Task Seed_InsertsTheWholeShippedCatalogInOneBatch()
    {
        // Regression guard: the context only assigns ids at SaveChanges, so rows added with
        // default ids collide in the change tracker on the SECOND row — which failed every
        // integration test, not just a seeding one.
        await using var db = NewContext();

        var (models, tiers) = await AiModelSeeder.SeedAsync(db, default);

        models.Should().Be(ModelCatalogSeed.Entries.Count);
        tiers.Should().Be(ModelCatalogSeed.TierDefaults.Count);

        await using var read = NewContext();
        var rows = await read.AiModels.AsNoTracking()
            .ToListAsync();

        rows.Select(r => r.ModelId).Should()
            .BeEquivalentTo(ModelCatalogSeed.Entries.Select(e => e.ModelId));
        rows.Select(r => r.Id).Should().OnlyHaveUniqueItems();
        rows.Should().OnlyContain(r => r.Id != Ulid.Empty);
        rows.Should().OnlyContain(r => r.IsBuiltIn);

        var tierRows = await read.AiTierDefaults.AsNoTracking()
            .ToListAsync();
        tierRows.Select(t => t.Id).Should().OnlyHaveUniqueItems();
        tierRows.Should().OnlyContain(t => t.Id != Ulid.Empty);
    }

    [Fact]
    public async Task Seed_IsIdempotent_AcrossRestarts()
    {
        await using var first = NewContext();
        await AiModelSeeder.SeedAsync(first, default);

        await using var second = NewContext();
        var (models, tiers) = await AiModelSeeder.SeedAsync(second, default);

        models.Should().Be(0, "a restart must not duplicate rows");
        tiers.Should().Be(0);

        await using var read = NewContext();
        (await read.AiModels.CountAsync())
            .Should().Be(ModelCatalogSeed.Entries.Count);
    }

    [Fact]
    public async Task Seed_NeverOverwritesAnAdminEdit()
    {
        await using var first = NewContext();
        await AiModelSeeder.SeedAsync(first, default);

        // An admin disables a model and reprices it.
        await using var edit = NewContext();
        var sonnet = await edit.AiModels
            .FirstAsync(m => m.ModelId == "claude-sonnet-4-6");
        sonnet.IsEnabled = false;
        sonnet.InputRatePer1M = 42m;
        await edit.SaveChangesAsync();

        // A deploy restarts the host and the seeder runs again.
        await using var second = NewContext();
        await AiModelSeeder.SeedAsync(second, default);

        await using var read = NewContext();
        var after = await read.AiModels.AsNoTracking()
            .FirstAsync(m => m.ModelId == "claude-sonnet-4-6");
        after.IsEnabled.Should().BeFalse("the seeder only inserts missing rows");
        after.InputRatePer1M.Should().Be(42m);
    }

    [Fact]
    public async Task Seed_TopsUpOnlyWhatIsMissing()
    {
        await using var first = NewContext();
        await AiModelSeeder.SeedAsync(first, default);

        // Simulate the previous release: a model that ships in this one was never seeded.
        await using var remove = NewContext();
        remove.AiModels.Remove(await remove.AiModels
            .FirstAsync(m => m.ModelId == "claude-opus-5"));
        await remove.SaveChangesAsync();

        await using var second = NewContext();
        var (models, _) = await AiModelSeeder.SeedAsync(second, default);

        models.Should().Be(1);
    }

    public void Dispose()
    {
        using var db = NewContext();
        db.Database.EnsureDeleted();
    }
}
