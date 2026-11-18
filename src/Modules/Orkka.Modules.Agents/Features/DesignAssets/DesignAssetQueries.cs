namespace Orkka.Modules.Agents.Features.DesignAssets;

using Orkka.Modules.Agents.Domain;

/// <summary>
/// The two selections the gallery's destructive actions turn on. They live here, once, because
/// getting either subtly wrong is invisible until someone loses work or cannot delete
/// something: a lineage query that matches one row leaves the card standing on an older
/// version, and a folder query that forgets the subtree strands every asset nested under the
/// folder that was removed.
/// </summary>
public static class DesignAssetQueries
{
    /// <summary>
    /// Every version of one asset. <see cref="DesignAsset.RootAssetId"/> is the lineage, and a
    /// v1 asset is its own root, so passing any version selects the whole chain.
    /// </summary>
    public static IQueryable<DesignAsset> Lineage(
        this IQueryable<DesignAsset> assets, Ulid orgId, Ulid projectId, Ulid rootAssetId) =>
        assets.Where(a => a.OrgId == orgId && a.ProjectId == projectId
            && a.RootAssetId == rootAssetId);

    /// <summary>
    /// Everything filed in a folder or anywhere below it. The subtree is included on purpose:
    /// folders are virtual, so an asset left in "brand/logos" after "brand" is gone appears in
    /// no folder at all.
    /// </summary>
    public static IQueryable<DesignAsset> InFolderTree(
        this IQueryable<DesignAsset> assets, Ulid orgId, Ulid projectId, string folderPath)
    {
        var prefix = folderPath + "/";
        return assets.Where(a => a.OrgId == orgId && a.ProjectId == projectId
            && (a.FolderPath == folderPath || a.FolderPath.StartsWith(prefix)));
    }
}
