namespace Orkka.Modules.Agents.Features.Diagrams;

using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.Modules.Agents.Infrastructure;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;

public sealed class ListDiagramVersionsEndpoint(
    AgentsDbContext db,
    ICurrentUser currentUser,
    IProjectResolver projectResolver,
    IUserDirectory userDirectory)
    : EndpointWithoutRequest<List<DiagramVersionResponse>>
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/diagrams/{diagramId:ulid}/versions");
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

        var diagramExists = await db.ArchitectureDiagrams.AsNoTracking()
            .AnyAsync(d => d.Id == diagramId && d.ProjectId == projectId && d.OrgId == currentUser.OrgId, ct);
        if (!diagramExists)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Slim projection — the scene JSON can be large and the list never shows it.
        var versions = await db.ArchitectureDiagramVersions
            .AsNoTracking()
            .Where(v => v.DiagramId == diagramId)
            .OrderByDescending(v => v.Version)
            .Select(v => new
            {
                v.Version, v.Title, v.Description, v.ChangedByUserId, v.ChangedByAgentId,
                v.RequestedByUserId, v.SourceConversationId, v.SourceMessageId, v.CreatedAt,
            })
            .ToListAsync(ct);

        var (agentNames, users) = await DiagramVersionActorNames.ResolveAsync(
            db, userDirectory,
            versions.Select(v => v.ChangedByAgentId),
            versions.SelectMany(v => new[] { v.ChangedByUserId, v.RequestedByUserId }),
            ct);

        var response = versions.Select(v => new DiagramVersionResponse(
            v.Version, v.Title, v.Description,
            v.ChangedByUserId, users.NameFor(v.ChangedByUserId),
            v.ChangedByAgentId, agentNames.NameFor(v.ChangedByAgentId),
            v.RequestedByUserId, users.NameFor(v.RequestedByUserId),
            v.SourceConversationId, v.SourceMessageId, v.CreatedAt)).ToList();

        await Send.OkAsync(response, ct);
    }
}
