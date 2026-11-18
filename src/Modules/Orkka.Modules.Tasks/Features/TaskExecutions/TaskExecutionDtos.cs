namespace Orkka.Modules.Tasks.Features.TaskExecutions;

using Orkka.Modules.Tasks.Domain;

public sealed record TaskExecutionResponse(
    Ulid Id,
    Ulid TaskId,
    Ulid? AgentId,
    string? AgentName,
    string? AgentRole,
    string Lane,
    string Status,
    string? AgentLog,
    IReadOnlyList<PlannedFileChange> FilesChanged,
    int TokensUsed,
    int PromptTokens,
    int CompletionTokens,
    decimal Cost,
    ExecutionTokenBreakdown? Billed,
    string? ErrorMessage,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset CreatedAt);

/// <summary>
/// What the run was actually charged for, sourced from the Usage module's rows rather than from
/// <c>TaskExecution</c>.
///
/// This exists because the execution row carries only Prompt/Completion — the two SMALLEST
/// buckets on a long agentic run. Cached prefix reads routinely outweigh them by two orders of
/// magnitude (measured: 5.9M cache reads against 801 input tokens on one 17-minute run), so a UI
/// showing <c>TokensUsed</c> next to a dollar figure displays ~1% of the billed volume and reads
/// as an overcharge. Every field here comes from the same rows the invoice is built from.
///
/// Null when the execution has no usage rows at all — a queued or still-running execution, or a
/// host that does not bill. Zero-cost is NOT null: Orkka Local records real token counts against
/// the user's own subscription, and those are worth showing.
/// </summary>
/// <param name="CacheReadTokens">Cached prompt prefix re-read on each turn. Billed at a fraction
/// of the input rate (10% for Anthropic) but by far the largest bucket — usually the single
/// biggest line item in a run's cost.</param>
/// <param name="CacheWriteTokens">Prompt prefix committed to cache. Billed at a PREMIUM over
/// input (125% for Anthropic), so a small count here can outweigh a large count of reads.</param>
public sealed record ExecutionTokenBreakdown(
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens)
{
    /// <summary>Every token the vendor charged for. The honest headline number.</summary>
    public long TotalTokens =>
        InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;
}
