namespace Orkka.Modules.Agents.Features.Diagrams;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Agents.Features.DesignAssets;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Documents.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;

/// <summary>Bulk path-prefix rename for Architecture-board folders — mirror of
/// <see cref="RenameDesignFolderEndpoint"/>.</summary>
public sealed class RenameDiagramFolderEndpoint(
    AgentsDbContext db,
    ICurrentUser currentUser,
    IProjectResolver projectResolver)
    : Endpoint<RenameFolderRequest>
{
    public override void Configure()
    {
        Post("/api/projects/{projectId}/diagrams/folders/rename");
    }

    public override async Task HandleAsync(RenameFolderRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.AgentsChat))
            return;

        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var fromPath = DocumentRules.NormalizePath(req.FromPath);
        var toPath = DocumentRules.NormalizePath(req.ToPath);
        if (fromPath.Length == 0)
        {
            ThrowError("The folder to rename must not be the root.", 400);
            return;
        }
        if (toPath == fromPath)
        {
            await Send.NoContentAsync(ct);
            return;
        }
        if (toPath.StartsWith(fromPath + "/", StringComparison.Ordinal))
        {
            ThrowError("A folder cannot be moved into itself.", 400);
            return;
        }

        var prefix = fromPath + "/";
        var affected = await db.ArchitectureDiagrams
            .Where(d => d.OrgId == currentUser.OrgId && d.ProjectId == projectId
                && (d.FolderPath == fromPath || d.FolderPath.StartsWith(prefix)))
            .ToListAsync(ct);

        foreach (var diagram in affected)
        {
            var suffix = diagram.FolderPath.Length > fromPath.Length
                ? diagram.FolderPath[fromPath.Length..]
                : "";
            diagram.FolderPath = DocumentRules.NormalizePath(toPath + suffix);
        }

        await db.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}
