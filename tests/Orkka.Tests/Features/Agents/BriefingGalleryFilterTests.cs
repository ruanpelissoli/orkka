namespace Orkka.Tests.Features.Agents;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Features.DesignAssets;
using Orkka.Modules.Agents.Infrastructure;

/// <summary>
/// What the Design gallery shows once a briefing has finished.
///
/// The briefing renders three options per category and does it again on every regeneration
/// round, so a project that took a few passes to settle arrives at provisioning with a dozen
/// logo sketches and every mockup screen of every direction sitting in the gallery. The brand
/// kit — the assets that actually ship — was buried underneath alternates the user rejected by
/// not picking them.
///
/// The picks are recoverable from the data (the briefing approves them and moves them under
/// brand/ on its way out), so the gallery filters on read rather than sweeping at completion:
/// a completion-time sweep would only ever help the NEXT briefing, and the cluttered galleries
/// are the ones already on screen.
/// </summary>
public sealed class BriefingGalleryFilterTests : IDisposable
{
    private readonly AgentsDbContext _db;
    private readonly Ulid _orgId = Ulid.NewUlid();
    private readonly Ulid _projectId = Ulid.NewUlid();

    public BriefingGalleryFilterTests()
    {
        var options = new DbContextOptionsBuilder<AgentsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AgentsDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    private DesignAsset Add(string name, string folder, DesignAssetStatus status)
    {
        var asset = new DesignAsset
        {
            Id = Ulid.NewUlid(),
            OrgId = _orgId,
            ProjectId = _projectId,
            Name = name,
            FileName = name + ".png",
            Kind = DesignAssetKind.Image,
            MimeType = "image/png",
            BlobContainer = "design-assets",
            BlobName = $"{_projectId}/{name}.png",
            SourceModel = "test",
            FolderPath = folder,
            Status = status,
        };
        _db.DesignAssets.Add(asset);
        return asset;
    }

    private List<string> VisibleNames()
        => ListDesignAssetsEndpoint
            .ExcludeUnpickedBriefingAssets(_db.DesignAssets.AsNoTracking())
            .Select(a => a.Name)
            .ToList();

    [Fact]
    public async Task TheGallery_KeepsTheBrandKitAndTheUsersPicks()
    {
        Add("logo-icon", "brand/identity", DesignAssetStatus.Approved);
        Add("wordmark", "brand/identity", DesignAssetStatus.Approved);
        // The chosen sketch, approved and moved out of briefing/ when the brief completed.
        Add("Orbit Mark — logo concept", "brand/identity", DesignAssetStatus.Approved);
        await _db.SaveChangesAsync();

        VisibleNames().Should().BeEquivalentTo(
            ["logo-icon", "wordmark", "Orbit Mark — logo concept"]);
    }

    [Fact]
    public async Task TheGallery_DropsTheOptionsTheUserDidNotPick()
    {
        Add("logo-icon", "brand/identity", DesignAssetStatus.Approved);
        Add("Spark — logo concept", "briefing/logos", DesignAssetStatus.Pending);
        Add("Flint — logo concept", "briefing/logos", DesignAssetStatus.Pending);
        Add("Dense — dashboard", "briefing/mockups", DesignAssetStatus.Pending);
        Add("moodboard.png", "briefing/references", DesignAssetStatus.Pending);
        await _db.SaveChangesAsync();

        VisibleNames().Should().BeEquivalentTo(["logo-icon"]);
    }

    // A pick that was approved but never moved out of briefing/ still belongs to the user —
    // the filter keys on the decision, not only on where the file happens to sit.
    [Fact]
    public async Task AnApprovedBriefingAsset_SurvivesEvenInTheBriefingFolder()
    {
        Add("Spark — logo concept", "briefing/logos", DesignAssetStatus.Approved);
        Add("Flint — logo concept", "briefing/logos", DesignAssetStatus.Pending);
        await _db.SaveChangesAsync();

        VisibleNames().Should().BeEquivalentTo(["Spark — logo concept"]);
    }

    // Rejecting an option is an explicit "not this one"; it must not reappear.
    [Fact]
    public async Task ARejectedBriefingAsset_StaysHidden()
    {
        Add("Flint — logo concept", "briefing/logos", DesignAssetStatus.Rejected);
        await _db.SaveChangesAsync();

        VisibleNames().Should().BeEmpty();
    }

    // The prefix must not swallow an unrelated folder that merely starts with the same letters.
    [Fact]
    public async Task AFolderThatMerelyLooksLikeBriefing_IsUntouched()
    {
        Add("brief-notes", "briefings-archive", DesignAssetStatus.Pending);
        await _db.SaveChangesAsync();

        VisibleNames().Should().BeEquivalentTo(["brief-notes"]);
    }
}
