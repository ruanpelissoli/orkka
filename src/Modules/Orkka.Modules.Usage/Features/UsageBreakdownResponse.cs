namespace Orkka.Modules.Usage.Features;

public sealed record UsageBreakdownResponse(
    List<UsageBreakdownItem> Items);

/// <summary>
/// One row of the usage breakdown. All four token buckets are carried, not just input/output:
/// on agentic runs the cached prefix dominates both volume and cost, so a "Tokens" column built
/// from input+output alone shows a fraction of what <see cref="CostUsd"/> was computed from.
/// </summary>
public sealed record UsageBreakdownItem(
    string GroupKey,
    string GroupName,
    decimal CostUsd,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    int TaskCount,
    // Set only by the task grouping: the row is a real task the reader can open, so the UI
    // renders it as a link to /p/{LinkProjectId}/tasks/{LinkTaskNumber}. Null everywhere
    // else — a project or model row has no single page to land on.
    string? LinkProjectId = null,
    int? LinkTaskNumber = null)
{
    /// <summary>Every token used in this group — what the cost was actually computed from.</summary>
    public long TotalTokens =>
        InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;
}
