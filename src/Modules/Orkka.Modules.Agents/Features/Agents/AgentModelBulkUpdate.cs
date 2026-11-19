namespace Orkka.Modules.Agents.Features.Agents;

using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Infrastructure;

/// <summary>Counts of one bulk model change.</summary>
/// <param name="UpdatedCount">Agents moved onto their new model.</param>
/// <param name="UnchangedCount">Agents already on it.</param>
/// <param name="SkippedQaCount">QA agents left on their model because the target cannot drive
/// browser (MCP) tools.</param>
public sealed record AgentModelBulkResult(int UpdatedCount, int UnchangedCount, int SkippedQaCount);

/// <summary>
/// The one bulk model write, shared by "Set every agent's model" (one project, one model) and
/// the workspace AI provider switch (every project, a model per role). Same rules as the
/// per-agent update: a QA agent never moves onto a model without MCP support, and every agent
/// that actually changed gets its own <see cref="AgentAuditEventType.AgentConfigChanged"/> event
/// (<c>bulk: true</c>). Saves once, then audits — never inside an open transaction.
/// </summary>
internal static class AgentModelBulkUpdate
{
    public static async Task<AgentModelBulkResult> ApplyAsync(
        AgentsDbContext db,
        IAgentAuditLog audit,
        ModelCatalogSnapshot catalog,
        Ulid orgId,
        string userId,
        IReadOnlyList<ProjectAgent> agents,
        Func<ProjectAgent, string> targetModel,
        string reason,
        CancellationToken ct)
    {
        var changed = new List<(Ulid Id, Ulid ProjectId, string Name, AgentRole Role, string From, string To)>();
        var unchanged = 0;
        var skippedQa = 0;
        foreach (var agent in agents)
        {
            var target = targetModel(agent);
            // Store the catalog's own spelling, so ids compare equal everywhere downstream.
            target = catalog.Find(target)?.ModelId ?? target;

            if (agent.Role == AgentRole.QA && !catalog.SupportsMcp(target))
            {
                skippedQa++;
                continue;
            }
            if (string.Equals(agent.Model, target, StringComparison.Ordinal))
            {
                unchanged++;
                continue;
            }

            changed.Add((agent.Id, agent.ProjectId, agent.Name, agent.Role, agent.Model, target));
            agent.Model = target;
        }

        if (changed.Count > 0)
            await db.SaveChangesAsync(ct);

        foreach (var c in changed)
            await audit.RecordAsync(AgentAuditRecord.ByUser(
                orgId, userId, AgentAuditEventType.AgentConfigChanged,
                $"Agent \"{c.Name}\" ({c.Role}) reconfigured by {userId}: Model ({reason})",
                projectId: c.ProjectId, agentId: c.Id,
                metadata: new { changed = new { Model = new { from = c.From, to = c.To } }, bulk = true }), ct);

        return new AgentModelBulkResult(changed.Count, unchanged, skippedQa);
    }
}
