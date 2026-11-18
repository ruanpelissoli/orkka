namespace Orkka.Modules.Usage.Domain;

using Orkka.SharedKernel.Domain;

/// <summary>
/// What one agent run (or chat turn, or utility completion) used: tokens per bucket and the
/// cost at list price. Runs go through the developer's own AI subscription, so the recorded
/// cost is normally 0 (<see cref="Contracts.RecordUsageCommand.FlatCostBase"/>); the row exists
/// for visibility, never for billing.
/// </summary>
public sealed class UsageRecord : TenantEntity
{
    public Ulid ProjectId { get; set; }
    public Ulid? TaskId { get; set; }
    public Ulid? AgentId { get; set; }
    public Ulid? ExecutionId { get; set; }

    /// <summary>Who asked for the work: the task's creator for task-driven runs, the caller
    /// for chat/design runs, null for work nobody asked for.</summary>
    public string? UserId { get; set; }
    public required string Model { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CacheReadTokens { get; set; }
    public int CacheWriteTokens { get; set; }
    public decimal CostTotal { get; set; }

    /// <summary>
    /// What the vendor's own tooling said this run cost, when it reports one (the Claude CLI's
    /// <c>total_cost_usd</c>). Null for providers that report none.
    /// </summary>
    public decimal? VendorReportedCostUsd { get; set; }
}
