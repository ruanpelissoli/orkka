namespace Orkka.Modules.Agents.Features.DesignAssets;

using Orkka.Modules.Agents.Features.AgentChat;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.SharedKernel.Storage;
using Orkka.Modules.Agents.Infrastructure;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;

public sealed class GetDesignAssetEndpoint(
    AgentsDbContext db,
    ICurrentUser currentUser,
    IProjectResolver projectResolver,
    IBlobStorageService blobStorage,
    IUserDirectory userDirectory)
    : EndpointWithoutRequest<DesignAssetResponse>
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/design-assets/{assetId:ulid}");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var assetId = Ulid.Parse(Route<string>("assetId"));

        var asset = await db.DesignAssets
            .AsNoTracking()
            .Include(a => a.Agent)
            .FirstOrDefaultAsync(
                a => a.Id == assetId && a.ProjectId == projectId && a.OrgId == currentUser.OrgId, ct);

        if (asset is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var url = await blobStorage.GenerateDownloadUrlAsync(
            asset.BlobContainer, asset.BlobName, DesignAssetGenerator.DisplayUrlExpiry);
        var downloadUrl = await blobStorage.GenerateDownloadUrlAsync(
            asset.BlobContainer, asset.BlobName, DesignAssetGenerator.DisplayUrlExpiry, asset.FileName);

        // SVG markup rides along so the client can rasterize to PNG locally — reading it
        // here (server-side blob access) works identically on Azure and Orkka Local,
        // where the browser can't always fetch the blob URL cross-origin.
        string? svgText = null;
        if (asset.Kind == DesignAssetKind.Svg)
        {
            await using var stream = await blobStorage.DownloadAsync(
                asset.BlobContainer, asset.BlobName, ct);
            if (stream is not null)
            {
                using var reader = new StreamReader(stream);
                svgText = await reader.ReadToEndAsync(ct);
            }
        }

        var users = await userDirectory.GetUsersAsync(
            new[] { asset.RequestedByUserId, asset.ReviewedByUserId }.OfType<string>().ToList(), ct);
        await Send.OkAsync(asset.ToResponse(asset.Agent?.Name, url, downloadUrl,
            users.DisplayNameFor(asset.RequestedByUserId),
            users.DisplayNameFor(asset.ReviewedByUserId),
            svgText), ct);
    }
}
