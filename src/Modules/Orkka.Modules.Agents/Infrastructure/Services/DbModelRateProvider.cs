namespace Orkka.Modules.Agents.Infrastructure.Services;

using Orkka.Modules.Agents.Contracts;
using Orkka.SharedKernel.Ai;

/// <summary>
/// Per-model vendor rates from the admin-managed catalog. Reads through the same
/// <see cref="IModelCatalog"/> cache entry as model resolution, so within a request the billed
/// cost and the displayed cost can never disagree.
///
/// Returns null when the model has no row or the row has no rates, letting
/// <see cref="CostCalculator"/> fall through to its shipped table.
/// </summary>
public sealed class DbModelRateProvider(IModelCatalog catalog) : IModelRateProvider
{
    public async ValueTask<(decimal Input, decimal Output)?> TryGetRatesAsync(
        string modelId, CancellationToken ct = default)
    {
        var entry = await FindAsync(modelId, ct);
        return entry is { InputRatePer1M: { } input, OutputRatePer1M: { } output }
            ? (input, output)
            : null;
    }

    public async ValueTask<(decimal Read, decimal Write)?> TryGetCacheRatesAsync(
        string modelId, CancellationToken ct = default)
    {
        var entry = await FindAsync(modelId, ct);
        // Both or neither: a row with only a read price would leave the write term at the
        // multiplier-table value of a DIFFERENT input rate — an accidental hybrid.
        return entry is { CacheReadRatePer1M: { } read, CacheWriteRatePer1M: { } write }
            ? (read, write)
            : null;
    }

    private async ValueTask<AiModelEntry?> FindAsync(string modelId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return null;

        var snapshot = await catalog.GetAsync(ct);

        // Exact first, then the same date-suffix normalization CostCalculator applies — without
        // it a date-suffixed id ("claude-sonnet-4-6-20260101") misses its repriced row and
        // silently bills at the shipped rate.
        return snapshot.Find(modelId)
            ?? snapshot.Find(CostCalculator.NormalizeForRates(modelId));
    }
}
