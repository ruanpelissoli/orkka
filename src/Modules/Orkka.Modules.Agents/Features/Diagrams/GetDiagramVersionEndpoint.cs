namespace Orkka.Modules.Agents.Features.Diagrams;

using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.Modules.Agents.Infrastructure;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;

public sealed class GetDiagramVersionEndpoint(
    AgentsDbContext db,
    ICurrentUser currentUser,
    IProjectResolver projectResolver,
    IUserDirectory userDirectory)
    : EndpointWithoutRequest<DiagramVersionDetailResponse>
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/diagrams/{diagramId:ulid}/versions/{version:int}");
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
        var version = Route<int>("version");

        var entity = await db.ArchitectureDiagramVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.DiagramId == diagramId && v.Version == version
                                      && v.OrgId == currentUser.OrgId
                                      && v.Diagram.ProjectId == projectId, ct);

        if (entity is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var (agentNames, users) = await DiagramVersionActorNames.ResolveAsync(
            db, userDirectory,
            [entity.ChangedByAgentId],
            [entity.ChangedByUserId, entity.RequestedByUserId],
            ct);

        await Send.OkAsync(entity.ToDetailResponse(
            users.NameFor(entity.ChangedByUserId),
            agentNames.NameFor(entity.ChangedByAgentId),
            users.NameFor(entity.RequestedByUserId)), ct);
    }
}
