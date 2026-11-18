namespace Orkka.Modules.Agents.Features.DesignAssets;

using Orkka.Modules.Agents.Features.AgentChat;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.SharedKernel.Storage;
using Orkka.Modules.Agents.Infrastructure;
using FastEndpoints;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

public sealed class RejectDesignAssetEndpoint(
    AgentsDbContext db,
    ICurrentUser currentUser,
    IProjectResolver projectResolver,
    IBlobStorageService blobStorage,
    IUserDirectory userDirectory,
    ILogger<RejectDesignAssetEndpoint> logger)
    : Endpoint<RejectDesignAssetRequest, DesignAssetResponse>
{
    public override void Configure()
    {
        Post("/api/projects/{projectId}/design-assets/{assetId:ulid}/reject");
    }

    public override async Task HandleAsync(RejectDesignAssetRequest req, CancellationToken ct)
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
            .Include(a => a.Agent)
            .FirstOrDefaultAsync(
                a => a.Id == assetId && a.ProjectId == projectId && a.OrgId == currentUser.OrgId, ct);

        if (asset is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        asset.Status = DesignAssetStatus.Rejected;
        asset.ApprovedAt = null;
        asset.Feedback = req.Feedback;
        asset.ReviewedByUserId = currentUser.UserId;
        asset.ReviewedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Design asset {AssetId} rejected in project {ProjectId}", assetId, projectId);

        var url = await blobStorage.GenerateDownloadUrlAsync(
            asset.BlobContainer, asset.BlobName, DesignAssetGenerator.DisplayUrlExpiry);
        var downloadUrl = await blobStorage.GenerateDownloadUrlAsync(
            asset.BlobContainer, asset.BlobName, DesignAssetGenerator.DisplayUrlExpiry, asset.FileName);

        var users = await userDirectory.GetUsersAsync(
            new[] { asset.RequestedByUserId, asset.ReviewedByUserId }.OfType<string>().ToList(), ct);
        await Send.OkAsync(asset.ToResponse(asset.Agent?.Name, url, downloadUrl,
            users.DisplayNameFor(asset.RequestedByUserId),
            users.DisplayNameFor(asset.ReviewedByUserId)), ct);
    }
}
