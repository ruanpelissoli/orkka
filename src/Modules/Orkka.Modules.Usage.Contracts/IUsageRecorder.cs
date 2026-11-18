namespace Orkka.Modules.Usage.Contracts;

public interface IUsageRecorder
{
    Task RecordUsageAsync(RecordUsageCommand command, CancellationToken ct = default);
}

/// <param name="FlatCostBase">The run's cost when it is known up front. Every run on the
/// developer's own subscription passes 0; null prices the tokens at list price from the model
/// catalog (<c>CostCalculator</c>).</param>
/// <param name="UserId">Who asked for the work: the task's creator for task-driven runs, the
/// caller for chat/design runs, null when nobody asked.</param>
/// <param name="VendorReportedCostUsd">What the vendor's own tooling reported the run cost, when
/// it reports one. Recorded alongside, never used to compute anything.</param>
public sealed record RecordUsageCommand(
    Ulid OrgId, Ulid ProjectId, Ulid? TaskId, Ulid? AgentId, Ulid? ExecutionId,
    string Model, int InputTokens, int OutputTokens,
    int CacheReadTokens = 0, int CacheWriteTokens = 0,
    decimal? FlatCostBase = null,
    string? UserId = null,
    decimal? VendorReportedCostUsd = null);
