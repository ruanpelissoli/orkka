namespace Orkka.Modules.Agents.Features.DesignAssets;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Documents.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;

public sealed record RenameFolderRequest(string? FromPath, string? ToPath);

/// <summary>
/// Renames/moves a gallery folder: every asset filed at FromPath (or below it) gets the
/// prefix replaced with ToPath. Folders are virtual — derived from asset paths — so this
/// is a bulk path update, not a folder-entity rename.
/// </summary>
public sealed class RenameDesignFolderEndpoint(
    AgentsDbContext db,
    ICurrentUser currentUser,
    IProjectResolver projectResolver)
    : Endpoint<RenameFolderRequest>
{
    public override void Configure()
    {
        Post("/api/projects/{projectId}/design-assets/folders/rename");
    }

    public override async Task HandleAsync(RenameFolderRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.DesignReview))
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
        // Renaming a folder into its own subtree would orphan the moved items.
        if (toPath.StartsWith(fromPath + "/", StringComparison.Ordinal))
        {
            ThrowError("A folder cannot be moved into itself.", 400);
            return;
        }

        var prefix = fromPath + "/";
        var affected = await db.DesignAssets
            .Where(a => a.OrgId == currentUser.OrgId && a.ProjectId == projectId
                && (a.FolderPath == fromPath || a.FolderPath.StartsWith(prefix)))
            .ToListAsync(ct);

        foreach (var asset in affected)
        {
            var suffix = asset.FolderPath.Length > fromPath.Length
                ? asset.FolderPath[fromPath.Length..] // includes the leading '/'
                : "";
            asset.FolderPath = DocumentRules.NormalizePath(toPath + suffix);
        }

        await db.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}
