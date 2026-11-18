namespace Orkka.Modules.Agents.Infrastructure.EventHandlers;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.SharedKernel.Events;
using Orkka.SharedKernel.Events.Catalog;

// Seeds the default agent pod when the Projects module announces a new project.
// Idempotent: a republished event only tops up missing roles.
internal sealed class AgentSeedOnProjectCreated(
    AgentsDbContext db,
    IProviderReadiness providerReadiness,
    IModelCatalog modelCatalog,
    ILogger<AgentSeedOnProjectCreated> logger) : IIntegrationEventHandler<ProjectCreated>
{
    public async Task HandleAsync(ProjectCreated evt, CancellationToken ct)
    {
        // Seed models from the preferred connected provider so a codex-only or
        // gemini-only user gets a pod that can run. Hosted (always-ready default) and
        // claude-connected local both resolve to Anthropic — today's exact models.
        var provider = providerReadiness.PreferredProvider();
        var catalog = await modelCatalog.GetAsync(ct);

        var existingRoles = await db.ProjectAgents
            .Where(a => a.ProjectId == evt.ProjectId)
            .Select(a => a.Role)
            .ToListAsync(ct);

        if (existingRoles.Count > 0)
        {
            // Retried/republished event on an already-seeded project — just top up any
            // specialist roles added to the roster since it was seeded.
            var missing = AgentSeeder.CreateMissingSpecialists(
                evt.OrgId, evt.ProjectId, existingRoles, provider, catalog);

            if (missing.Count > 0)
            {
                db.ProjectAgents.AddRange(missing);
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Topped up {Count} specialist agent(s) for project {ProjectId}",
                    missing.Count, evt.ProjectId);
            }

            await SeedSkillsAsync(evt.OrgId, evt.ProjectId, ct);
            return;
        }

        var agents = AgentSeeder.CreateDefaultPod(evt.OrgId, evt.ProjectId, catalog, provider);
        db.ProjectAgents.AddRange(agents);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Seeded {Count} agents for project {ProjectId} (provider={Provider})",
            agents.Count, evt.ProjectId, provider);

        await SeedSkillsAsync(evt.OrgId, evt.ProjectId, ct);
    }

    /// <summary>Starter skills, attached to their default roles. Idempotent per built-in key
    /// (guarded by the filtered unique index), so a republished event is a no-op.</summary>
    private async Task SeedSkillsAsync(Ulid orgId, Ulid projectId, CancellationToken ct)
    {
        var existingKeys = await db.AgentSkills
            .Where(s => s.ProjectId == projectId && s.BuiltInKey != null)
            .Select(s => s.BuiltInKey!)
            .ToListAsync(ct);

        var seeded = SkillSeeder.CreateMissing(orgId, projectId, existingKeys);
        if (seeded.Count == 0) return;

        db.AgentSkills.AddRange(seeded);
        var agents = await db.ProjectAgents.Where(a => a.ProjectId == projectId).ToListAsync(ct);
        var assigned = SkillSeeder.AssignDefaults(seeded, agents);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Seeded {Count} starter skill(s) for project {ProjectId} ({Assigned} role assignment(s))",
            seeded.Count, projectId, assigned);
    }
}
