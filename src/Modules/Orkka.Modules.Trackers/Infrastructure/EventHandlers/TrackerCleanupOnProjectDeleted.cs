namespace Orkka.Modules.Trackers.Infrastructure.EventHandlers;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orkka.SharedKernel.Events;
using Orkka.SharedKernel.Events.Catalog;

/// <summary>
/// A deleted project's ticket source goes with it. Projects live in their own module (no
/// cross-module FKs), so without this the source stayed due and the runner kept polling the
/// tracker for a project that no longer existed — every minute, failing once that tracker went
/// away (R5 e2e run, 2026-11-18). Epic rules cascade from the config; ticket links have no FK.
/// </summary>
public sealed class TrackerCleanupOnProjectDeleted(
    TrackersDbContext db,
    ILogger<TrackerCleanupOnProjectDeleted> logger) : IIntegrationEventHandler<ProjectDeleted>
{
    public async Task HandleAsync(ProjectDeleted evt, CancellationToken ct)
    {
        var links = await db.TicketLinks.Where(l => l.ProjectId == evt.ProjectId).ExecuteDeleteAsync(ct);
        var configIds = db.ProjectConfigs.Where(c => c.ProjectId == evt.ProjectId).Select(c => c.Id);
        var rules = await db.EpicMappings.Where(m => configIds.Contains(m.ConfigId)).ExecuteDeleteAsync(ct);
        var configs = await db.ProjectConfigs.Where(c => c.ProjectId == evt.ProjectId).ExecuteDeleteAsync(ct);

        if (configs + links + rules > 0)
            logger.LogInformation(
                "Removed the ticket source of deleted project {ProjectId}: {Configs} source(s), {Rules} rule(s), {Links} ticket link(s)",
                evt.ProjectId, configs, rules, links);
    }
}
