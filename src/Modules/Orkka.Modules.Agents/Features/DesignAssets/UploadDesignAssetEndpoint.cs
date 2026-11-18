namespace Orkka.Modules.Agents.Features.DesignAssets;

using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Features.AgentChat;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Documents.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Ai;
using Orkka.SharedKernel.Security;
using Orkka.SharedKernel.Storage;

public sealed class UploadDesignAssetRequest
{
    public IFormFile File { get; set; } = null!;
}

/// <summary>
/// Adds a user-provided image to the Design gallery — the manual counterpart of the
/// Designer's ```design pipeline. The asset starts Approved (uploading is an explicit
/// human choice of this art, same reasoning as Restore), so it flows into dev-agent task
/// context alongside approved agent work. AgentId stays null: there is no authoring agent.
/// </summary>
public sealed class UploadDesignAssetEndpoint(
    AgentsDbContext db,
    ICurrentUser currentUser,
    IProjectResolver projectResolver,
    IBlobStorageService blobStorage,
    IUserDirectory userDirectory)
    : Endpoint<UploadDesignAssetRequest, DesignAssetResponse>
{
    private const long MaxFileSizeBytes = 10 * 1024 * 1024;

    // Only formats the gallery can render via <img> and agents can consume as references.
    private static readonly Dictionary<string, string> MimeByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".gif"] = "image/gif",
        [".svg"] = "image/svg+xml",
    };

    public override void Configure()
    {
        Post("/api/projects/{projectId}/design-assets/upload");
        AllowFileUploads();
    }

    public override async Task HandleAsync(UploadDesignAssetRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.DesignReview))
            return;

        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var file = req.File;
        if (file is null || file.Length == 0)
        {
            ThrowError("No file was uploaded.", 400);
            return;
        }
        if (file.Length > MaxFileSizeBytes)
        {
            ThrowError($"The file exceeds the {MaxFileSizeBytes / (1024 * 1024)} MB upload limit.", 400);
            return;
        }

        var extension = Path.GetExtension(file.FileName ?? "");
        if (!MimeByExtension.TryGetValue(extension, out var mimeType))
        {
            ThrowError($"File type \"{extension}\" is not supported as a design asset.", 400);
            return;
        }

        var fileName = Path.GetFileName(file.FileName ?? $"upload{extension}");
        var name = DocumentRules.NormalizeTitle(Path.GetFileNameWithoutExtension(fileName)) ?? "Upload";
        var folderPath = DocumentRules.NormalizePath(Query<string>("folderPath", isRequired: false));

        // The original name is display-only; the blob path uses the id so a crafted file
        // name can never influence storage layout.
        var assetId = Ulid.NewUlid();
        var blobName = $"{currentUser.OrgId}/{projectId}/{assetId}{extension.ToLowerInvariant()}";

        await blobStorage.UploadAsync(
            DesignAssetGenerator.BlobContainerName, blobName, file.OpenReadStream(), mimeType, ct);

        var now = DateTimeOffset.UtcNow;
        var asset = new DesignAsset
        {
            Id = assetId,
            OrgId = currentUser.OrgId,
            ProjectId = projectId,
            AgentId = null,
            Name = name,
            FileName = fileName.Length <= 500 ? fileName : fileName[..500],
            Kind = extension.ToLowerInvariant() switch
            {
                ".svg" => DesignAssetKind.Svg,
                ".gif" => DesignAssetKind.Gif,
                _ => DesignAssetKind.Image,
            },
            MimeType = mimeType,
            BlobContainer = DesignAssetGenerator.BlobContainerName,
            BlobName = blobName,
            SizeBytes = file.Length,
            SourceModel = "user-upload",
            RootAssetId = assetId,
            // Uploading is an explicit human choice of this art — it starts approved,
            // unlike agent deliveries which await review.
            Status = DesignAssetStatus.Approved,
            ApprovedAt = now,
            FolderPath = folderPath,
            RequestedByUserId = currentUser.UserId,
            ReviewedByUserId = currentUser.UserId,
            ReviewedAt = now,
        };
        db.DesignAssets.Add(asset);

        await db.SaveChangesAsync(ct);

        var url = await blobStorage.GenerateDownloadUrlAsync(
            asset.BlobContainer, asset.BlobName, DesignAssetGenerator.DisplayUrlExpiry);
        var downloadUrl = await blobStorage.GenerateDownloadUrlAsync(
            asset.BlobContainer, asset.BlobName, DesignAssetGenerator.DisplayUrlExpiry, asset.FileName);
        var users = await userDirectory.GetUsersAsync([currentUser.UserId], ct);
        var uploaderName = users.DisplayNameFor(currentUser.UserId);

        await Send.OkAsync(asset.ToResponse(agentName: null, url, downloadUrl,
            requestedByUserName: uploaderName, reviewedByUserName: uploaderName), ct);
    }
}
