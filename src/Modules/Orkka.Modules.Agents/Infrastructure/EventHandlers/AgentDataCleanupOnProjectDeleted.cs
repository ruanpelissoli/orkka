namespace Orkka.Modules.Agents.Infrastructure.EventHandlers;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orkka.SharedKernel.Events;
using Orkka.SharedKernel.Events.Catalog;

// Cross-module FKs to the projects table do not exist, so the Agents module removes its
// project-scoped rows when a project is deleted. Dependent rows (agent messages/memories,
// design assets, per-agent diagrams) cascade from module-internal FKs. Audit events are
// intentionally retained — the audit log is append-only and survives project deletion.
internal sealed class AgentDataCleanupOnProjectDeleted(
    AgentsDbContext db,
    ILogger<AgentDataCleanupOnProjectDeleted> logger) : IIntegrationEventHandler<ProjectDeleted>
{
    public async Task HandleAsync(ProjectDeleted evt, CancellationToken ct)
    {
        var conversations = await db.AgentConversations.Where(c => c.ProjectId == evt.ProjectId).ExecuteDeleteAsync(ct);
        var diagrams = await db.ArchitectureDiagrams.Where(d => d.ProjectId == evt.ProjectId).ExecuteDeleteAsync(ct);
        var agents = await db.ProjectAgents.Where(a => a.ProjectId == evt.ProjectId).ExecuteDeleteAsync(ct);
        var skills = await db.AgentSkills.Where(s => s.ProjectId == evt.ProjectId).ExecuteDeleteAsync(ct);

        logger.LogInformation(
            "Cleaned up agents data for deleted project {ProjectId}: {Agents} agents, " +
            "{Conversations} conversations, {Diagrams} diagrams, {Skills} skills",
            evt.ProjectId, agents, conversations, diagrams, skills);
    }
}
