namespace Orkka.Tests.Services;

using FluentAssertions;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Infrastructure.Services;
using Orkka.SharedKernel.Ai;

file sealed class FixedCatalog(params AiModelEntry[] entries) : IModelCatalog
{
    private readonly ModelCatalogSnapshot _snapshot =
        new(entries, new Dictionary<(AiProvider, AiModelTier), string>());

    public ValueTask<ModelCatalogSnapshot> GetAsync(CancellationToken ct = default) => new(_snapshot);
}

public sealed class ModelRateProviderTests
{
    private static AiModelEntry Entry(string id, decimal? input, decimal? output) =>
        new(id, id, AiProvider.Anthropic, AiModelKind.Chat, "d", true, true, false, null, input, output, 0);

    // ── The "nothing changed" guard ─────────────────────────────────────────

    [Fact]
    public async Task NullProvider_YieldsShippedRates_ForEveryPricedModel()
    {
        // Hosts with no catalog (and the Migrator) must bill exactly as they did before rates
        // became admin-managed.
        var provider = new NullModelRateProvider();

        foreach (var id in CostCalculator.PricedModelIds)
        {
            (await provider.TryGetRatesAsync(id)).Should().BeNull();

            var withNull = CostCalculator.Calculate(id, 1_000_000, 1_000_000, rates: null);
            var shipped = CostCalculator.TryGetBaseRates(id)!.Value;
            var expected = CostCalculator.Calculate(id, 1_000_000, 1_000_000, rates: shipped);

            withNull.Should().Be(expected, id);
        }
    }

    [Fact]
    public async Task DbRates_OverrideTheShippedTable()
    {
        var rates = new DbModelRateProvider(new FixedCatalog(Entry("claude-sonnet-4-6", 99m, 199m)));

        (await rates.TryGetRatesAsync("claude-sonnet-4-6")).Should().Be((99m, 199m));

        var costBase = CostCalculator.Calculate(
            "claude-sonnet-4-6", 1_000_000, 0,
            rates: await rates.TryGetRatesAsync("claude-sonnet-4-6"));
        costBase.Should().Be(99m, "the repriced row wins over the shipped $3");
    }

    [Fact]
    public async Task DateSuffixedIds_ResolveTheRepricedRow()
    {
        // Without CostCalculator.NormalizeForRates, a date-suffixed id would miss its row,
        // return null, and silently bill at the shipped rate.
        var rates = new DbModelRateProvider(new FixedCatalog(Entry("claude-sonnet-4-6", 7m, 21m)));

        (await rates.TryGetRatesAsync("claude-sonnet-4-6-20260101")).Should().Be((7m, 21m));
    }

    [Fact]
    public async Task RowWithNoRates_FallsThroughToTheShippedTable()
    {
        var rates = new DbModelRateProvider(new FixedCatalog(Entry("pixellab-pixflux", null, null)));

        (await rates.TryGetRatesAsync("pixellab-pixflux")).Should().BeNull();
    }

    [Fact]
    public async Task UnknownAndEmptyIds_YieldNull()
    {
        var rates = new DbModelRateProvider(new FixedCatalog(Entry("claude-opus-5", 5m, 25m)));

        (await rates.TryGetRatesAsync("who-knows")).Should().BeNull();
        (await rates.TryGetRatesAsync("")).Should().BeNull();
    }

    [Fact]
    public async Task DisabledRowsStillBill_AtTheirOwnRate()
    {
        // Disabling a model must never change what running agents cost.
        var rates = new DbModelRateProvider(new FixedCatalog(
            new AiModelEntry("claude-opus-4-1", "Claude Opus 4.1", AiProvider.Anthropic,
                AiModelKind.Chat, "retired", false, IsEnabled: false, false, null, 15m, 75m, 0)));

        (await rates.TryGetRatesAsync("claude-opus-4-1")).Should().Be((15m, 75m));
    }
}
