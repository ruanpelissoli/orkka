namespace Orkka.Modules.Agents.Features.DesignAssets;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Documents.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.SharedKernel.Storage;

/// <param name="Path">The folder to remove.</param>
/// <param name="DeleteContents">
/// Permission to delete the assets filed there. Without it a folder holding anything is
/// refused, so "remove this empty folder" can never turn into "delete this work".
/// </param>
public sealed record DeleteFolderRequest(string? Path, bool DeleteContents = false);

/// <summary>
/// Deletes a gallery folder. Folders are virtual — a folder exists because assets carry its
/// path — so an empty one only lives in the client and this endpoint has nothing to do; the
/// case that reaches the database is a folder with assets in it.
///
/// The tree offered rename and create but no way to remove a folder, so a mistyped or
/// finished folder stayed forever (reported 2026-09-08).
/// </summary>
public sealed class DeleteDesignFolderEndpoint(
    AgentsDbContext db,
    ICurrentUser currentUser,
    IProjectResolver projectResolver,
    IBlobStorageService blobStorage,
    ILogger<DeleteDesignFolderEndpoint> logger)
    : Endpoint<DeleteFolderRequest>
{
    public override void Configure()
    {
        Post("/api/projects/{projectId}/design-assets/folders/delete");
    }

    public override async Task HandleAsync(DeleteFolderRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.DesignReview))
            return;

        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var path = DocumentRules.NormalizePath(req.Path);
        if (path.Length == 0)
        {
            ThrowError("The root folder cannot be deleted.", 400);
            return;
        }

        // The folder and everything nested under it, every version of every asset.
        var assets = await db.DesignAssets
            .InFolderTree(currentUser.OrgId, projectId, path)
            .ToListAsync(ct);

        if (assets.Count > 0 && !req.DeleteContents)
        {
            // 409, not 400: the request is well formed, the folder is simply not empty. The
            // count travels with it so the caller can say what is about to be lost.
            await Send.ResponseAsync(
                new { folderPath = path, assetCount = assets.Count }, 409, ct);
            return;
        }

        if (assets.Count > 0)
        {
            db.DesignAssets.RemoveRange(assets);
            await db.SaveChangesAsync(ct);

            foreach (var asset in assets)
                await blobStorage.DeleteBlobAsync(asset.BlobContainer, asset.BlobName);
        }

        logger.LogInformation(
            "Design folder {Folder} deleted in project {ProjectId} with {Assets} asset(s)",
            path, projectId, assets.Count);

        await Send.NoContentAsync(ct);
    }
}
