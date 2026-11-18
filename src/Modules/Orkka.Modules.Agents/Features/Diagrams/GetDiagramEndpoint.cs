namespace Orkka.Modules.Agents.Features.Diagrams;

using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.Modules.Agents.Infrastructure;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;

public sealed class GetDiagramEndpoint(AgentsDbContext db, ICurrentUser currentUser, IProjectResolver projectResolver)
    : EndpointWithoutRequest<DiagramResponse>
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/diagrams/{diagramId:ulid}");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var diagramId = Ulid.Parse(Route<string>("diagramId"));

        var entity = await db.ArchitectureDiagrams
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == diagramId && d.ProjectId == projectId && d.OrgId == currentUser.OrgId, ct);

        if (entity is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(entity.ToResponse(), ct);
    }
}
