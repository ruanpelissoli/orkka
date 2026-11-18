namespace Orkka.Modules.Agents.Features.Diagrams;

using Orkka.SharedKernel.Security;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Agents.Infrastructure;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;

public sealed class DeleteDiagramEndpoint(AgentsDbContext db, ICurrentUser currentUser, IProjectResolver projectResolver)
    : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("/api/projects/{projectId}/diagrams/{diagramId:ulid}");
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

        var entity = await db.ArchitectureDiagrams
            .FirstOrDefaultAsync(d => d.Id == diagramId && d.ProjectId == projectId && d.OrgId == currentUser.OrgId, ct);

        if (entity is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        db.ArchitectureDiagrams.Remove(entity);
        await db.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}
