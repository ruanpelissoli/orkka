namespace Orkka.SharedKernel.Ai;

/// <summary>
/// Catalog rates for a model, per 1M tokens. The seam lives here so usage recording can read DB rates without SharedKernel
/// referencing a module. The Agents module replaces the default with a DB-backed
/// implementation; the default returns null so <see cref="CostCalculator"/> falls through to
/// its shipped table (and then to the unknown-model rate).
/// </summary>
public interface IModelRateProvider
{
    ValueTask<(decimal Input, decimal Output)?> TryGetRatesAsync(
        string modelId, CancellationToken ct = default);

    /// <summary>Absolute vendor cache prices (read hit, write) per 1M tokens, when the
    /// catalog row carries them. Null keeps
    /// <see cref="CostCalculator"/> on its multiplier tables. Default so hosts without a
    /// catalog need no implementation.</summary>
    ValueTask<(decimal Read, decimal Write)?> TryGetCacheRatesAsync(
        string modelId, CancellationToken ct = default) =>
        new(((decimal, decimal)?)null);
}

/// <summary>Default for hosts with no model catalog (and for the Migrator): always null, so
/// cost calculation behaves exactly as it did before rates became admin-managed.</summary>
public sealed class NullModelRateProvider : IModelRateProvider
{
    public ValueTask<(decimal Input, decimal Output)?> TryGetRatesAsync(
        string modelId, CancellationToken ct = default) => new((ValueTuple<decimal, decimal>?)null);
}
