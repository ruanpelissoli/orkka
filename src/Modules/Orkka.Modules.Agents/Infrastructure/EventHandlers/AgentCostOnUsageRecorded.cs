namespace Orkka.Modules.Agents.Infrastructure.EventHandlers;

using Microsoft.EntityFrameworkCore;
using Orkka.SharedKernel.Events;
using Orkka.SharedKernel.Events.Catalog;

// Keeps ProjectAgent.TotalCostUsd in sync with recorded usage.
internal sealed class AgentCostOnUsageRecorded(AgentsDbContext db) : IIntegrationEventHandler<UsageRecorded>
{
    public async Task HandleAsync(UsageRecorded evt, CancellationToken ct)
    {
        if (evt.AgentId is null)
            return;

        await db.ProjectAgents
            .Where(a => a.Id == evt.AgentId.Value)
            .ExecuteUpdateAsync(s => s.SetProperty(
                a => a.TotalCostUsd,
                a => a.TotalCostUsd + evt.CostTotal), ct);
    }
}
