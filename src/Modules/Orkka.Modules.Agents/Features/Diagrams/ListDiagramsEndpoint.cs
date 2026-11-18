namespace Orkka.Modules.Agents.Features.Diagrams;

using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.Modules.Agents.Infrastructure;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;

public sealed class ListDiagramsEndpoint(AgentsDbContext db, ICurrentUser currentUser, IProjectResolver projectResolver)
    : EndpointWithoutRequest<List<DiagramResponse>>
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/diagrams");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var conversationId = Query<string>("conversationId", isRequired: false);

        var query = db.ArchitectureDiagrams
            .AsNoTracking()
            .Where(d => d.OrgId == currentUser.OrgId && d.ProjectId == projectId);

        if (Ulid.TryParse(conversationId, out var convId))
            query = query.Where(d => d.ConversationId == convId);

        var diagrams = await query
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(ct);

        await Send.OkAsync(diagrams.Select(d => d.ToResponse()).ToList(), ct);
    }
}
