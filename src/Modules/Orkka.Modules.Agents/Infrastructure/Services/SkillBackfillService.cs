namespace Orkka.Modules.Agents.Infrastructure.Services;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Agents.Domain;

/// <summary>
/// One-shot startup sweep that gives every existing project the built-in starter skills
/// (SkillSeeder.BuiltIns) and attaches them to their default roles. New projects get them
/// from AgentSeedOnProjectCreated; this covers projects created before skills existed, or
/// before a given built-in shipped. Keyed on BuiltInKey, so a built-in the user renamed or
/// disabled is left alone; the filtered unique index on (ProjectId, BuiltInKey) makes
/// concurrent hosts safe.
/// </summary>
internal sealed class SkillBackfillService(
    IServiceScopeFactory scopeFactory,
    ILogger<SkillBackfillService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();

            // System-wide sweep: bypass the ambient tenant filter (no request scope here).
            var projects = await db.ProjectAgents.IgnoreQueryFilters().AsNoTracking()
                .Select(a => new { a.ProjectId, a.OrgId })
                .Distinct()
                .ToListAsync(stoppingToken);

            var existing = await db.AgentSkills.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.BuiltInKey != null)
                .Select(s => new { s.ProjectId, s.BuiltInKey })
                .ToListAsync(stoppingToken);
            var keysByProject = existing
                .GroupBy(s => s.ProjectId)
                .ToDictionary(g => g.Key, g => g.Select(s => s.BuiltInKey!).ToList());

            var added = 0;
            foreach (var project in projects)
            {
                var have = keysByProject.GetValueOrDefault(project.ProjectId) ?? [];
                var seeded = SkillSeeder.CreateMissing(project.OrgId, project.ProjectId, have);
                if (seeded.Count == 0) continue;

                db.AgentSkills.AddRange(seeded);
                var agents = await db.ProjectAgents.IgnoreQueryFilters()
                    .Where(a => a.ProjectId == project.ProjectId)
                    .ToListAsync(stoppingToken);
                SkillSeeder.AssignDefaults(seeded, agents);

                try
                {
                    await db.SaveChangesAsync(stoppingToken);
                    added += seeded.Count;
                }
                catch (DbUpdateException)
                {
                    // Another host won the insert race on the built-in unique index.
                    foreach (var skill in seeded)
                        db.Entry(skill).State = EntityState.Detached;
                    foreach (var agent in agents)
                        db.Entry(agent).State = EntityState.Detached;
                }
            }

            if (added > 0)
                logger.LogInformation("Skill backfill added {Count} starter skill(s)", added);

            // Second pass: built-ins nobody edited pick up the current playbook text. Without
            // this an improved skill reached only projects created after the improvement.
            var unedited = await db.AgentSkills.IgnoreQueryFilters()
                .Where(s => s.BuiltInKey != null && s.UpdatedByUserId == null)
                .ToListAsync(stoppingToken);
            var refreshed = SkillSeeder.RefreshUnedited(unedited);
            if (refreshed > 0)
            {
                await db.SaveChangesAsync(stoppingToken);
                logger.LogInformation("Skill backfill refreshed {Count} unedited built-in skill(s)", refreshed);
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutting down.
        }
        catch (Exception ex)
        {
            // Never take the host down over a backfill — the sweep reruns on next start.
            logger.LogError(ex, "Skill backfill failed");
        }
    }
}
