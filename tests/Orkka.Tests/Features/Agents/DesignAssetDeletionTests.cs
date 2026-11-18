namespace Orkka.Tests.Features.Agents;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Features.AgentChat;
using Orkka.Modules.Agents.Features.DesignAssets;
using Orkka.Modules.Agents.Infrastructure;

/// <summary>
/// What the Design page's destructive actions actually select.
///
/// The gallery draws ONE card per lineage — the newest version, with the older ones behind it
/// in the history modal — so a card is a lineage, not a row. Deleting a single row left the
/// card standing with the previous version in it, and an image regenerated five times took
/// five presses to remove, each press looking like it had failed ("I had to press delete a
/// couple of times for each image", 2026-09-08).
///
/// Folders have the mirror-image trap: they are virtual, derived from the assets' paths, so a
/// delete that matched the folder alone would leave everything nested below it filed under a
/// folder that no longer appears in the tree — present, paid for, and unreachable.
/// </summary>
public sealed class DesignAssetDeletionTests : IDisposable
{
    private readonly AgentsDbContext _db;
    private readonly Ulid _orgId = Ulid.NewUlid();
    private readonly Ulid _projectId = Ulid.NewUlid();

    public DesignAssetDeletionTests()
    {
        _db = new AgentsDbContext(new DbContextOptionsBuilder<AgentsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
    }

    public void Dispose() => _db.Dispose();

    /// <summary>Adds one version. Passing no root starts a new lineage at v1.</summary>
    private DesignAsset Add(string name, int version = 1, Ulid? root = null, string folder = "")
    {
        var id = Ulid.NewUlid();
        var asset = new DesignAsset
        {
            Id = id,
            OrgId = _orgId,
            ProjectId = _projectId,
            RootAssetId = root ?? id,
            Version = version,
            Name = name,
            FolderPath = folder,
            Kind = DesignAssetKind.Image,
            Status = DesignAssetStatus.Pending,
            BlobContainer = "design-assets",
            BlobName = $"{id}.png",
            FileName = $"{name}.png",
            MimeType = "image/png",
            SourceModel = "gpt-image-2",
        };
        _db.DesignAssets.Add(asset);
        _db.SaveChanges();
        return asset;
    }

    [Fact]
    public void Deleting_an_asset_selects_every_version_of_it()
    {
        var v1 = Add("launch-banner");
        Add("launch-banner", 2, v1.RootAssetId);
        Add("launch-banner", 3, v1.RootAssetId);
        var unrelated = Add("logo");

        var lineage = _db.DesignAssets
            .Lineage(_orgId, _projectId, v1.RootAssetId)
            .ToList();

        lineage.Should().HaveCount(3, "the card the user pressed delete on is the whole lineage");
        lineage.Should().NotContain(a => a.Id == unrelated.Id);
    }

    [Fact]
    public void The_newest_version_selects_the_same_lineage_as_the_first()
    {
        // The card shows the newest version, so that is the id the delete button sends.
        var v1 = Add("launch-banner");
        var v5 = Add("launch-banner", 5, v1.RootAssetId);

        _db.DesignAssets.Lineage(_orgId, _projectId, v5.RootAssetId).Should().HaveCount(2);
    }

    [Fact]
    public void A_lineage_never_reaches_another_project_or_org()
    {
        var mine = Add("shared-name");
        _db.DesignAssets.Add(new DesignAsset
        {
            Id = Ulid.NewUlid(),
            OrgId = Ulid.NewUlid(),
            ProjectId = Ulid.NewUlid(),
            RootAssetId = mine.RootAssetId, // same lineage id, someone else's data
            Name = "shared-name",
            Kind = DesignAssetKind.Image,
            BlobContainer = "design-assets",
            BlobName = "other.png",
            FileName = "other.png",
            MimeType = "image/png",
            SourceModel = "gpt-image-2",
        });
        _db.SaveChanges();

        _db.DesignAssets.Lineage(_orgId, _projectId, mine.RootAssetId).Should().ContainSingle();
    }

    [Fact]
    public void Deleting_a_folder_takes_everything_nested_under_it()
    {
        Add("logo", folder: "brand");
        Add("wordmark", folder: "brand/logos");
        Add("icon", folder: "brand/logos/small");
        var outside = Add("banner", folder: "marketing");

        var inTree = _db.DesignAssets.InFolderTree(_orgId, _projectId, "brand").ToList();

        inTree.Should().HaveCount(3);
        inTree.Should().NotContain(a => a.Id == outside.Id);
    }

    [Fact]
    public void A_folder_does_not_swallow_a_sibling_whose_name_starts_the_same()
    {
        Add("inside", folder: "brand");
        var sibling = Add("elsewhere", folder: "branding");

        var inTree = _db.DesignAssets.InFolderTree(_orgId, _projectId, "brand").ToList();

        inTree.Should().ContainSingle("\"branding\" is a different folder, not a child of \"brand\"");
        inTree.Should().NotContain(a => a.Id == sibling.Id);
    }

    /// <summary>
    /// Where a newly generated asset is filed. The rule is deliberately not the model's to
    /// make: the folder is a fact about where the user is standing, and asking the agent to
    /// be told it and remember it is how assets end up at the root and have to be moved by
    /// hand after every request.
    /// </summary>
    [Theory]
    [InlineData("marketing", null, "marketing")]      // new asset, folder open
    [InlineData(null, null, "")]                       // new asset, "All items"
    [InlineData("marketing", "brand", "brand")]        // iteration follows its parent
    [InlineData(null, "brand", "brand")]               // ...even with no folder open
    public void A_new_asset_is_filed_where_the_user_is_looking(
        string? openFolder, string? parentFolder, string expected)
    {
        var parent = parentFolder is null ? null : Add("earlier", folder: parentFolder);

        DesignAssetGenerator.FolderFor(parent, openFolder).Should().Be(expected);
    }

    [Fact]
    public void A_folder_delete_takes_every_version_in_it_too()
    {
        var v1 = Add("launch-banner", folder: "marketing");
        Add("launch-banner", 2, v1.RootAssetId, "marketing");

        _db.DesignAssets.InFolderTree(_orgId, _projectId, "marketing")
            .Should().HaveCount(2, "leaving old versions behind would strand their blobs");
    }
}
