namespace Orkka.Modules.Usage.Features;

public sealed record DailyUsageResponse(
    List<DailyUsageItem> Items);

/// <summary>
/// One day of usage. Carries all four token buckets for the same reason
/// <see cref="UsageBreakdownItem"/> does: input+output alone is a small and misleading fraction
/// of what <see cref="CostUsd"/> was computed from on agentic runs.
/// </summary>
public sealed record DailyUsageItem(
    DateOnly Date,
    decimal CostUsd,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    int TaskCount)
{
    /// <summary>Every token used that day — what the cost was actually computed from.</summary>
    public long TotalTokens =>
        InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;
}
