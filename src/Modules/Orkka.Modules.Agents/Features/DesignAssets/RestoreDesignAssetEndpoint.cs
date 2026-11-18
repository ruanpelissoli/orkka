namespace Orkka.Modules.Agents.Features.DesignAssets;

using Orkka.Modules.Agents.Features.AgentChat;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.SharedKernel.Storage;
using Orkka.Modules.Agents.Infrastructure;
using FastEndpoints;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Rolls a lineage back to an older version — non-destructively: the chosen version's
/// content is copied into a NEW latest version (parented on the restored one), so the
/// gallery card shows the restored art while the full history stays intact. No model
/// calls, no tokens — a pure blob copy.
/// </summary>
public sealed class RestoreDesignAssetEndpoint(
    AgentsDbContext db,
    ICurrentUser currentUser,
    IProjectResolver projectResolver,
    IBlobStorageService blobStorage,
    ILogger<RestoreDesignAssetEndpoint> logger)
    : EndpointWithoutRequest<DesignAssetResponse>
{
    public override void Configure()
    {
        Post("/api/projects/{projectId}/design-assets/{assetId:ulid}/restore");
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

        var source = await db.DesignAssets
            .AsNoTracking()
            .Include(a => a.Agent)
            .FirstOrDefaultAsync(
                a => a.Id == assetId && a.ProjectId == projectId && a.OrgId == currentUser.OrgId, ct);

        if (source is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var latestVersion = await db.DesignAssets.AsNoTracking()
            .Where(a => a.OrgId == currentUser.OrgId && a.RootAssetId == source.RootAssetId)
            .MaxAsync(a => a.Version, ct);

        if (source.Version == latestVersion)
        {
            ThrowError("This is already the current version.");
            return;
        }

        // Copy the restored version's binary into a fresh blob — versions never share
        // blobs, so deleting one version can never orphan another's content.
        await using var content = await blobStorage.DownloadAsync(
            source.BlobContainer, source.BlobName, ct);
        if (content is null)
        {
            ThrowError("The stored file for this version is missing.");
            return;
        }

        var newId = Ulid.NewUlid();
        var extension = Path.GetExtension(source.BlobName).TrimStart('.');
        var newBlobName = $"{currentUser.OrgId}/{projectId}/{newId}.{extension}";
        await blobStorage.UploadAsync(
            source.BlobContainer, newBlobName, content, source.MimeType, ct);

        var restored = new DesignAsset
        {
            Id = newId,
            OrgId = currentUser.OrgId,
            ProjectId = projectId,
            AgentId = source.AgentId,
            ConversationId = source.ConversationId,
            MessageId = source.MessageId,
            Name = source.Name,
            FileName = source.FileName,
            Kind = source.Kind,
            MimeType = source.MimeType,
            BlobContainer = source.BlobContainer,
            BlobName = newBlobName,
            SizeBytes = source.SizeBytes,
            Prompt = source.Prompt,
            SourceModel = source.SourceModel,
            ParentAssetId = source.Id,
            RootAssetId = source.RootAssetId,
            Version = latestVersion + 1,
            // Restoring is an explicit human choice of this art — it starts approved,
            // unlike agent deliveries which await review.
            Status = DesignAssetStatus.Approved,
            ApprovedAt = DateTimeOffset.UtcNow,
            FolderPath = source.FolderPath,
            RequestedByUserId = currentUser.UserId,
            ReviewedByUserId = currentUser.UserId,
            ReviewedAt = DateTimeOffset.UtcNow,
        };
        db.DesignAssets.Add(restored);

        // Same invariant Approve maintains: one approved version per lineage.
        await db.DesignAssets
            .Where(a => a.OrgId == currentUser.OrgId
                && a.RootAssetId == source.RootAssetId
                && a.Status == DesignAssetStatus.Approved)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, DesignAssetStatus.Rejected)
                .SetProperty(a => a.Feedback, "Superseded by a newer approved version.")
                .SetProperty(a => a.ReviewedByUserId, currentUser.UserId)
                .SetProperty(a => a.ReviewedAt, DateTimeOffset.UtcNow), ct);

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Design asset {Name} rolled back to v{SourceVersion} as v{NewVersion} in project {ProjectId}",
            source.Name, source.Version, restored.Version, projectId);

        var url = await blobStorage.GenerateDownloadUrlAsync(
            restored.BlobContainer, restored.BlobName, DesignAssetGenerator.DisplayUrlExpiry);
        var downloadUrl = await blobStorage.GenerateDownloadUrlAsync(
            restored.BlobContainer, restored.BlobName, DesignAssetGenerator.DisplayUrlExpiry, restored.FileName);

        await Send.OkAsync(restored.ToResponse(source.Agent?.Name, url, downloadUrl), ct);
    }
}
