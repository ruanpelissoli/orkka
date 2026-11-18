namespace Orkka.Modules.Agents.Features.Diagrams;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Documents.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;

public sealed record MoveDiagramRequest(string? FolderPath);

/// <summary>Files a diagram under an Architecture-board folder. Pure organization — the
/// diagram's Version and history snapshots are untouched.</summary>
public sealed class MoveDiagramEndpoint(
    AgentsDbContext db,
    ICurrentUser currentUser,
    IProjectResolver projectResolver)
    : Endpoint<MoveDiagramRequest>
{
    public override void Configure()
    {
        Patch("/api/projects/{projectId}/diagrams/{diagramId:ulid}/folder");
    }

    public override async Task HandleAsync(MoveDiagramRequest req, CancellationToken ct)
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

        var diagram = await db.ArchitectureDiagrams.FirstOrDefaultAsync(
            d => d.Id == diagramId && d.ProjectId == projectId && d.OrgId == currentUser.OrgId, ct);
        if (diagram is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        diagram.FolderPath = DocumentRules.NormalizePath(req.FolderPath);
        await db.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}
