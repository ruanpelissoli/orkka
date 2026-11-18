namespace Orkka.Tests.Caching;

using System.Text.Json;
using FluentAssertions;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Organizations.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Caching;
using Orkka.SharedKernel.Security;

/// <summary>
/// Round-trips every payload shape the cache stores through the <b>configured</b> serializer.
/// </summary>
/// <remarks>
/// These matter on EVERY read, distributed tier or not: HybridCache only hands an entry back by
/// reference when it considers the type immutable, and none of these are — so an in-process hit
/// is a serialize-then-deserialize round trip like any other. A payload the serializer cannot
/// rebuild therefore throws on every read, not just once L2 is switched on. Two defaults would
/// corrupt the data even when it does rebuild: <c>Ulid</c> has no built-in converter, and enums
/// serialize as ordinals.
/// </remarks>
public sealed class CacheSerializationTests
{
    private static readonly JsonSerializerOptions Options = OrkkaJsonSerializerFactory.Options;

    private static T RoundTrip<T>(T value)
        => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;

    /// <summary>
    /// The premise the rest of this file rests on: an in-process hit is NOT the factory's object
    /// handed back. If it were, an unserializable payload would only break once Redis was turned
    /// on, and every test here would be about a hypothetical.
    /// </summary>
    [Fact]
    public async Task InProcessHit_ComesBackThroughTheSerializer()
    {
        using var cache = new TestCache();
        var built = new ModelCatalogPayload([], []);

        var miss = await cache.Cache.GetOrCreateAsync(
            CacheKeys.ModelCatalog, _ => ValueTask.FromResult(built), CachePolicies.PlatformReference);
        var hit = await cache.Cache.GetOrCreateAsync(
            CacheKeys.ModelCatalog, _ => ValueTask.FromResult(built), CachePolicies.PlatformReference);

        miss.Should().NotBeSameAs(built, "HybridCache rebuilds a mutable payload even on the miss path");
        hit.Should().NotBeSameAs(built);
    }

    [Fact]
    public void Ulid_RoundTripsAsItsCanonicalString()
    {
        var id = Ulid.NewUlid();

        JsonSerializer.Serialize(id, Options).Should().Be($"\"{id}\"");
        RoundTrip(id).Should().Be(id);
    }

    [Fact]
    public void Enums_SerializeByNameNotOrdinal()
    {
        // Ordinals would pin the cache format to declaration order: reordering Permission
        // would silently reinterpret every entry written by the previous deploy as a
        // different permission. Names survive reordering.
        JsonSerializer.Serialize(Permission.TasksCreate, Options)
            .Should().Be($"\"{nameof(Permission.TasksCreate)}\"");
    }

    [Fact]
    public void MembershipInfo_RoundTrips()
    {
        var value = new MembershipInfo(
            MemberId: Ulid.NewUlid(),
            OrgId: Ulid.NewUlid(),
            Role: OrgRole.Owner,
            Permissions: [Permission.TasksCreate, Permission.AuditView]);

        RoundTrip(value).Should().BeEquivalentTo(value);
    }

    [Fact]
    public void ProjectSummary_RoundTrips()
    {
        var value = new ProjectSummary(
            Id: Ulid.NewUlid(),
            OrgId: Ulid.NewUlid(),
            Name: "Test Project",
            Slug: "test-project",
            Key: "TP",
            Settings: null);

        RoundTrip(value).Should().BeEquivalentTo(value);
    }

    [Fact]
    public void ModelCatalogPayload_RoundTrips_IncludingTierDefaults()
    {
        var value = new ModelCatalogPayload(
            [
                new AiModelEntry(
                    ModelId: "claude-sonnet-4-6",
                    DisplayName: "Claude Sonnet 4.6",
                    Provider: AiProvider.Anthropic,
                    Kind: AiModelKind.Chat,
                    Description: "Everyday development",
                    SupportsMcp: true,
                    IsEnabled: true,
                    RequiresRunner: false,
                    ImageProviderKey: null,
                    InputRatePer1M: 3.00m,
                    OutputRatePer1M: 15.00m,
                    SortOrder: 20),
            ],
            [new AiTierDefaultEntry(AiProvider.Anthropic, AiModelTier.Fast, "claude-haiku-4-5")]);

        var restored = RoundTrip(value);

        restored.Should().BeEquivalentTo(value);
        // The tier map is the half that a snapshot-shaped payload silently dropped: it is
        // private state behind a tuple-keyed dictionary, which no serializer can rebuild.
        restored.ToSnapshot().TierModel(AiProvider.Anthropic, AiModelTier.Fast)
            .Should().Be("claude-haiku-4-5");
        restored.ToSnapshot().Find("claude-sonnet-4-6")!.InputRatePer1M.Should().Be(3.00m);
    }

    /// <summary>
    /// The catalog must be cached as a payload, never as a snapshot. A snapshot in the cache
    /// throws on the way back out, and <c>DbModelCatalog</c> catches that and serves the shipped
    /// catalog — so admin edits appear to work, the table is correct, and every read is stale.
    /// </summary>
    [Fact]
    public void ModelCatalogSnapshot_IsNotCacheSerializable()
    {
        var json = JsonSerializer.Serialize(ModelCatalogSnapshot.Static, Options);

        FluentActions.Invoking(() => JsonSerializer.Deserialize<ModelCatalogSnapshot>(json, Options))
            .Should().Throw<InvalidOperationException>(
                "the snapshot's constructor binds to no property of the same name — cache the "
                + "payload and call ToSnapshot() instead");
    }

    [Fact]
    public void RepoSummary_RoundTrips_IncludingNullableUlid()
    {
        var value = new RepoSummary(
            Id: Ulid.NewUlid(),
            ProjectId: Ulid.NewUlid(),
            OrgId: Ulid.NewUlid(),
            Name: "api",
            GitUrl: "https://github.com/orkka/api.git",
            DefaultBranch: "main",
            CardColor: "#3b82f6",
            WorkspaceStatus: WorkspaceStatus.Ready);

        RoundTrip(value).Should().BeEquivalentTo(value);
    }
}
