namespace Orkka.Modules.Agents.Features.DesignAssets;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Documents.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;

public sealed record MoveDesignAssetRequest(string? FolderPath);

/// <summary>
/// Files a design asset under a gallery folder. Moves the WHOLE lineage (every version
/// sharing the RootAssetId) — the gallery shows one card per lineage, so a move must not
/// scatter versions across folders.
/// </summary>
public sealed class MoveDesignAssetEndpoint(
    AgentsDbContext db,
    ICurrentUser currentUser,
    IProjectResolver projectResolver)
    : Endpoint<MoveDesignAssetRequest>
{
    public override void Configure()
    {
        Patch("/api/projects/{projectId}/design-assets/{assetId:ulid}/folder");
    }

    public override async Task HandleAsync(MoveDesignAssetRequest req, CancellationToken ct)
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

        var asset = await db.DesignAssets.FirstOrDefaultAsync(
            a => a.Id == assetId && a.ProjectId == projectId && a.OrgId == currentUser.OrgId, ct);
        if (asset is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Same path rules as Documents — unusable input collapses to "" (root).
        var folderPath = DocumentRules.NormalizePath(req.FolderPath);

        var lineage = await db.DesignAssets
            .Where(a => a.RootAssetId == asset.RootAssetId
                && a.ProjectId == projectId && a.OrgId == currentUser.OrgId)
            .ToListAsync(ct);
        foreach (var version in lineage)
            version.FolderPath = folderPath;

        await db.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}
