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

public sealed class ApproveDesignAssetEndpoint(
    AgentsDbContext db,
    ICurrentUser currentUser,
    IProjectResolver projectResolver,
    IBlobStorageService blobStorage,
    IUserDirectory userDirectory,
    ILogger<ApproveDesignAssetEndpoint> logger)
    : EndpointWithoutRequest<DesignAssetResponse>
{
    public override void Configure()
    {
        Post("/api/projects/{projectId}/design-assets/{assetId:ulid}/approve");
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
            .Include(a => a.Agent)
            .FirstOrDefaultAsync(
                a => a.Id == assetId && a.ProjectId == projectId && a.OrgId == currentUser.OrgId, ct);

        if (asset is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        asset.Status = DesignAssetStatus.Approved;
        asset.ApprovedAt = now;
        asset.Feedback = null;
        asset.ReviewedByUserId = currentUser.UserId;
        asset.ReviewedAt = now;

        // One approved version per lineage: approving a version supersedes earlier approvals.
        await db.DesignAssets
            .Where(a => a.OrgId == currentUser.OrgId
                && a.RootAssetId == asset.RootAssetId
                && a.Id != asset.Id
                && a.Status == DesignAssetStatus.Approved)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, DesignAssetStatus.Rejected)
                .SetProperty(a => a.Feedback, "Superseded by a newer approved version.")
                .SetProperty(a => a.ReviewedByUserId, currentUser.UserId)
                .SetProperty(a => a.ReviewedAt, now), ct);

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Design asset {AssetId} approved in project {ProjectId}", assetId, projectId);

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
