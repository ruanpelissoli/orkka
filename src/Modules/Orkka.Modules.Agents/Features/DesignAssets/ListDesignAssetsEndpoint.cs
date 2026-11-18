namespace Orkka.Modules.Agents.Features.DesignAssets;

using Orkka.Modules.Agents.Features.AgentChat;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.SharedKernel.Storage;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Infrastructure;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;

public sealed class ListDesignAssetsEndpoint(
    AgentsDbContext db,
    ICurrentUser currentUser,
    IProjectResolver projectResolver,
    IBlobStorageService blobStorage,
    IUserDirectory userDirectory)
    : EndpointWithoutRequest<List<DesignAssetResponse>>
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/design-assets");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var query = db.DesignAssets
            .AsNoTracking()
            .Include(a => a.Agent)
            .Where(a => a.OrgId == currentUser.OrgId && a.ProjectId == projectId);

        var status = Query<string>("status", isRequired: false);
        if (Enum.TryParse<DesignAssetStatus>(status, ignoreCase: true, out var parsedStatus))
            query = query.Where(a => a.Status == parsedStatus);

        // The briefing renders three options per category per regeneration round, and every
        // one of them landed here — burying the actual brand kit under the alternates the user
        // rejected by not picking them. Only the picks survive into the gallery; they are
        // approved and moved under brand/ when the briefing completes.
        //
        // Filtered on READ rather than marked at completion, deliberately: a completion-time
        // sweep would only help briefings that have not run yet, and the galleries that are
        // already cluttered are the ones the user is looking at. Nothing is deleted —
        // ?includeBriefing=true brings them back.
        if (!Query<bool>("includeBriefing", isRequired: false))
            query = ExcludeUnpickedBriefingAssets(query);

        var assets = await query
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);

        var userIds = assets
            .SelectMany(a => new[] { a.RequestedByUserId, a.ReviewedByUserId })
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var users = userIds.Count > 0
            ? await userDirectory.GetUsersAsync(userIds, ct)
            : new Dictionary<string, UserInfo>(StringComparer.Ordinal);

        var responses = new List<DesignAssetResponse>(assets.Count);
        foreach (var asset in assets)
        {
            var url = await blobStorage.GenerateDownloadUrlAsync(
                asset.BlobContainer, asset.BlobName, DesignAssetGenerator.DisplayUrlExpiry);
            var downloadUrl = await blobStorage.GenerateDownloadUrlAsync(
                asset.BlobContainer, asset.BlobName, DesignAssetGenerator.DisplayUrlExpiry, asset.FileName);
            responses.Add(asset.ToResponse(asset.Agent?.Name, url, downloadUrl,
                users.DisplayNameFor(asset.RequestedByUserId),
                users.DisplayNameFor(asset.ReviewedByUserId)));
        }

        await Send.OkAsync(responses, ct);
    }

    /// <summary>Drops what the retired design briefing left under <c>briefing/</c> —
    /// unpicked option renders and uploaded references — while keeping anything the user
    /// actually chose, which the briefing marked Approved on its way out.</summary>
    internal const string BriefingFolderPrefix = "briefing/";

    internal static IQueryable<DesignAsset> ExcludeUnpickedBriefingAssets(
        IQueryable<DesignAsset> query) =>
        query.Where(a =>
            !a.FolderPath.StartsWith(BriefingFolderPrefix)
            || a.Status == DesignAssetStatus.Approved);
}
