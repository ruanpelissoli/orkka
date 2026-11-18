namespace Orkka.Modules.Usage.Features;

/// <summary>
/// Usage totals for a period. Runs use the developer's own AI subscription, so the cost is
/// normally 0 — tokens are the figure with content.
/// </summary>
public sealed record UsageSummaryResponse(
    decimal TotalCostUsd,
    long TotalInputTokens,
    long TotalOutputTokens,
    long TotalCacheReadTokens,
    long TotalCacheWriteTokens,
    int TaskCount)
{
    /// <summary>Every token behind <see cref="TotalCostUsd"/>.</summary>
    public long TotalTokens =>
        TotalInputTokens + TotalOutputTokens + TotalCacheReadTokens + TotalCacheWriteTokens;
}
