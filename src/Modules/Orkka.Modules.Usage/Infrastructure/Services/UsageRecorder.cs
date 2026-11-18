namespace Orkka.Modules.Usage.Infrastructure.Services;

using Orkka.Modules.Usage.Contracts;
using Orkka.Modules.Usage.Domain;
using Orkka.SharedKernel.Ai;
using Orkka.SharedKernel.Events;
using Orkka.SharedKernel.Events.Catalog;

internal sealed class UsageRecorder(
    UsageDbContext db,
    IModelRateProvider modelRates,
    IEventBus eventBus) : IUsageRecorder
{
    public async Task RecordUsageAsync(RecordUsageCommand command, CancellationToken ct = default)
    {
        var cost = command.FlatCostBase ?? await ListPriceAsync(command, ct);

        db.UsageRecords.Add(new UsageRecord
        {
            OrgId = command.OrgId,
            ProjectId = command.ProjectId,
            TaskId = command.TaskId,
            AgentId = command.AgentId,
            ExecutionId = command.ExecutionId,
            UserId = command.UserId,
            Model = command.Model,
            InputTokens = command.InputTokens,
            OutputTokens = command.OutputTokens,
            CacheReadTokens = command.CacheReadTokens,
            CacheWriteTokens = command.CacheWriteTokens,
            CostTotal = cost,
            VendorReportedCostUsd = command.VendorReportedCostUsd,
        });
        await db.SaveChangesAsync(ct);

        // Reactions (e.g. agent cost tracking in the Agents module) subscribe to this.
        await eventBus.PublishAsync(new UsageRecorded(
            command.OrgId, command.ProjectId, command.TaskId, command.AgentId,
            command.Model, command.InputTokens, command.OutputTokens, cost), ct);
    }

    /// <summary>The catalog's rates for the model when it prices it, else CostCalculator's
    /// shipped table.</summary>
    private async Task<decimal> ListPriceAsync(RecordUsageCommand command, CancellationToken ct) =>
        CostCalculator.Calculate(
            command.Model, command.InputTokens, command.OutputTokens,
            command.CacheReadTokens, command.CacheWriteTokens,
            rates: await modelRates.TryGetRatesAsync(command.Model, ct),
            cacheRates: await modelRates.TryGetCacheRatesAsync(command.Model, ct));
}
