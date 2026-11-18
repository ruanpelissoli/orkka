namespace Orkka.Modules.Agents.Features.Diagrams;

using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Infrastructure;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;

public sealed class UpdateDiagramEndpoint(AgentsDbContext db, ICurrentUser currentUser, IProjectResolver projectResolver)
    : Endpoint<UpdateDiagramRequest, DiagramResponse>
{
    public override void Configure()
    {
        Put("/api/projects/{projectId}/diagrams/{diagramId:ulid}");
    }

    public override async Task HandleAsync(UpdateDiagramRequest req, CancellationToken ct)
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

        var entity = await db.ArchitectureDiagrams
            .FirstOrDefaultAsync(d => d.Id == diagramId && d.ProjectId == projectId && d.OrgId == currentUser.OrgId, ct);

        if (entity is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var title = req.Title ?? entity.Title;
        var diagramData = req.DiagramData is { } data ? data.GetRawText() : entity.DiagramData;
        var description = req.Description ?? entity.Description;

        // No-op updates shouldn't burn a version number.
        if (title == entity.Title && diagramData == entity.DiagramData && description == entity.Description)
        {
            await Send.OkAsync(entity.ToResponse(), ct);
            return;
        }

        entity.Title = title;
        entity.DiagramData = diagramData;
        entity.Description = description;
        entity.Version++;
        db.ArchitectureDiagramVersions.Add(DiagramSnapshots.FromCurrentState(
            entity, currentUser.UserId, changedByAgentId: null));

        await db.SaveChangesAsync(ct);

        await Send.OkAsync(entity.ToResponse(), ct);
    }
}
