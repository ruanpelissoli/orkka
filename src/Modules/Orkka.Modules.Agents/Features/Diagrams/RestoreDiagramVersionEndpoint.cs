namespace Orkka.Modules.Agents.Features.Diagrams;

using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Infrastructure;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;

/// <summary>Restore = append a new version carrying the old snapshot's state. History is never
/// rewritten; the restored-from version stays intact.</summary>
public sealed class RestoreDiagramVersionEndpoint(
    AgentsDbContext db,
    ICurrentUser currentUser,
    IProjectResolver projectResolver)
    : EndpointWithoutRequest<DiagramResponse>
{
    public override void Configure()
    {
        Post("/api/projects/{projectId}/diagrams/{diagramId:ulid}/versions/{version:int}/restore");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.AgentsChat))
            return;

        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var diagramId = Ulid.Parse(Route<string>("diagramId"));
        var version = Route<int>("version");

        var entity = await db.ArchitectureDiagrams
            .FirstOrDefaultAsync(d => d.Id == diagramId && d.ProjectId == projectId && d.OrgId == currentUser.OrgId, ct);
        if (entity is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var snapshot = await db.ArchitectureDiagramVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.DiagramId == diagramId && v.Version == version, ct);
        if (snapshot is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (snapshot.Version == entity.Version)
        {
            // Restoring the current version is a no-op.
            await Send.OkAsync(entity.ToResponse(), ct);
            return;
        }

        entity.Title = snapshot.Title;
        entity.Description = snapshot.Description;
        entity.DiagramData = snapshot.DiagramData;
        entity.Version++;
        db.ArchitectureDiagramVersions.Add(DiagramSnapshots.FromCurrentState(
            entity, currentUser.UserId, changedByAgentId: null));

        await db.SaveChangesAsync(ct);
        await Send.OkAsync(entity.ToResponse(), ct);
    }
}
