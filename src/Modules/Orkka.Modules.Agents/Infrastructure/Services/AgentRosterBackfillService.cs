namespace Orkka.Modules.Agents.Infrastructure.Services;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;

/// <summary>
/// One-shot startup sweep that tops every existing project up to the full always-seeded
/// roster (AgentSeeder.Specialists). New projects get the full pod from
/// AgentSeedOnProjectCreated; this covers projects created before a specialist role
/// existed (e.g. Legal). Runs in every host; the filtered unique index on
/// (ProjectId, Role) makes concurrent hosts safe — the loser of an insert race just
/// skips that project.
/// </summary>
internal sealed class AgentRosterBackfillService(
    IServiceScopeFactory scopeFactory,
    ILogger<AgentRosterBackfillService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
            var workspaceProvider = scope.ServiceProvider.GetRequiredService<IWorkspaceAiProviderResolver>();
            var catalog = await scope.ServiceProvider
                .GetRequiredService<IModelCatalog>().GetAsync(stoppingToken);

            // System-wide sweep: bypass the ambient tenant filter (no request scope here).
            var rows = await db.ProjectAgents.IgnoreQueryFilters().AsNoTracking()
                .Select(a => new { a.ProjectId, a.OrgId, a.Role })
                .ToListAsync(stoppingToken);

            var toppedUp = 0;
            // The provider is per workspace (its AI provider choice + its runner), resolved once
            // per workspace that actually needs a top-up.
            var providers = new Dictionary<Ulid, AiProvider>();

            foreach (var project in rows.GroupBy(r => new { r.ProjectId, r.OrgId }))
            {
                var roles = project.Select(r => r.Role).ToList();
                if (AgentSeeder.Specialists.All(s => roles.Contains(s.Role)))
                    continue;

                if (!providers.TryGetValue(project.Key.OrgId, out var provider))
                {
                    provider = (await workspaceProvider.ResolveAsync(
                        project.Key.OrgId, userId: null, stoppingToken)).Provider;
                    providers[project.Key.OrgId] = provider;
                }

                var missing = AgentSeeder.CreateMissingSpecialists(
                    project.Key.OrgId, project.Key.ProjectId, roles, provider, catalog);

                if (missing.Count == 0)
                    continue;

                db.ProjectAgents.AddRange(missing);
                try
                {
                    await db.SaveChangesAsync(stoppingToken);
                    toppedUp += missing.Count;
                }
                catch (DbUpdateException)
                {
                    // Another host won the insert race on the singleton-role unique index.
                    foreach (var agent in missing)
                        db.Entry(agent).State = EntityState.Detached;
                }
            }

            if (toppedUp > 0)
                logger.LogInformation("Agent roster backfill added {Count} specialist agent(s)", toppedUp);
        }
        catch (OperationCanceledException)
        {
            // Host shutting down.
        }
        catch (Exception ex)
        {
            // Never take the host down over a backfill — the sweep reruns on next start.
            logger.LogError(ex, "Agent roster backfill failed");
        }
    }
}
