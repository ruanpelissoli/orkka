namespace Orkka.Modules.Agents.Features.DesignAssets;

using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.SharedKernel.Storage;
using Orkka.Modules.Agents.Infrastructure;
using FastEndpoints;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

public sealed class DeleteDesignAssetEndpoint(
    AgentsDbContext db,
    ICurrentUser currentUser,
    IProjectResolver projectResolver,
    IBlobStorageService blobStorage,
    ILogger<DeleteDesignAssetEndpoint> logger)
    : EndpointWithoutRequest
{
    /// <summary>
    /// Deletes an asset and every other version of it.
    ///
    /// The gallery shows ONE card per lineage — the newest version, with the older ones behind
    /// it in the history modal — so a card is a lineage, not a row. Deleting a single row left
    /// the card standing with the previous version in it, and the user pressed delete once per
    /// version to make an image go away ("I had to press delete a couple of times for each
    /// image", 2026-09-08). An image that was regenerated five times took five presses, each
    /// one looking like it had failed.
    /// </summary>
    public override void Configure()
    {
        Delete("/api/projects/{projectId}/design-assets/{assetId:ulid}");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.DesignReview))
            return;

        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var assetId = Ulid.Parse(Route<string>("assetId"));

        var asset = await db.DesignAssets
            .FirstOrDefaultAsync(
                a => a.Id == assetId && a.ProjectId == projectId && a.OrgId == currentUser.OrgId, ct);

        if (asset is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var lineage = await db.DesignAssets
            .Lineage(currentUser.OrgId, projectId, asset.RootAssetId)
            .ToListAsync(ct);

        db.DesignAssets.RemoveRange(lineage);
        await db.SaveChangesAsync(ct);

        // Blobs after the rows: an orphaned blob is invisible and reapable, whereas a row
        // pointing at a blob that is already gone renders as a broken image.
        foreach (var version in lineage)
            await blobStorage.DeleteBlobAsync(version.BlobContainer, version.BlobName);

        logger.LogInformation(
            "Design asset {AssetId} deleted in project {ProjectId} with {Versions} version(s)",
            assetId, projectId, lineage.Count);

        await Send.NoContentAsync(ct);
    }
}
