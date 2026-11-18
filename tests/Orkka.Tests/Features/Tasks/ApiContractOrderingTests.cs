namespace Orkka.Tests.Features.Tasks;

using FluentAssertions;
using Moq;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Infrastructure.Services;

/// <summary>
/// The deterministic half of API-before-client ordering. The model is asked to declare these
/// edges itself; this pass is what makes the outcome not depend on whether it did.
/// </summary>
public sealed class ApiContractOrderingTests
{
    private static readonly Ulid ApiRepo = Ulid.NewUlid();
    private static readonly Ulid WebRepo = Ulid.NewUlid();
    private static readonly Ulid MobileRepo = Ulid.NewUlid();

    private static DecomposedTask Task(string title, Ulid repo, IReadOnlyList<int>? dependsOn = null)
        => new(title, "desc", "Medium", "Feature", dependsOn, repo.ToString());

    /// <summary>Builds a layer map from a roster, the way the real one is derived.</summary>
    private static async Task<RepoLayerMap> LayersAsync(params (AgentRole Role, Ulid Repo)[] roster)
    {
        var agents = roster.Select(r => new AgentSummary(
            Ulid.NewUlid(), Ulid.NewUlid(), Ulid.NewUlid(),
            [r.Repo], r.Role.ToString(), r.Role, "model", "prompt", "")).ToList();

        var api = new Mock<IAgentsApi>();
        api.Setup(a => a.ListAgentsAsync(It.IsAny<Ulid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(agents);

        return await RepoLayerMap.BuildAsync(api.Object, Ulid.NewUlid());
    }

    private static Func<DecomposedTask, Ulid?> ByRepoName =>
        t => Ulid.TryParse(t.Repo, out var id) ? id : null;

    [Fact]
    public async Task ClientTaskGainsAnEdgeToTheApiTask_EvenAcrossRepos()
    {
        var layers = await LayersAsync((AgentRole.BackendDev, ApiRepo), (AgentRole.FrontendDev, WebRepo));

        // The order the model produced: UI first, API second, no edges. Two different repos, so
        // nothing is imported and the model's own rule reads them as independent.
        var result = ApiContractOrdering.Apply(
            [Task("Booking screen", WebRepo), Task("Bookings endpoint", ApiRepo)],
            layers, ByRepoName);

        result[0].Title.Should().Be("Bookings endpoint", "API work must come first");
        result[1].Title.Should().Be("Booking screen");
        result[1].DependsOn.Should().Equal([0], "the screen calls an endpoint that must exist first");
        result[0].DependsOn.Should().BeNull();
    }

    [Fact]
    public async Task MobileCountsAsAClientToo()
    {
        var layers = await LayersAsync((AgentRole.BackendDev, ApiRepo), (AgentRole.MobileDev, MobileRepo));

        var result = ApiContractOrdering.Apply(
            [Task("Mobile checkout", MobileRepo), Task("Orders API", ApiRepo)],
            layers, ByRepoName);

        result[0].Title.Should().Be("Orders API");
        result[1].DependsOn.Should().Equal([0]);
    }

    [Fact]
    public async Task ExistingModelEdgesSurviveTheReorder()
    {
        var layers = await LayersAsync((AgentRole.BackendDev, ApiRepo), (AgentRole.FrontendDev, WebRepo));

        // Index 2 depends on index 1 — both API tasks, and the model is right about the order
        // (schema before the endpoint that reads it). Reordering must remap that edge, not drop
        // it or slide it onto whichever task took the slot.
        var result = ApiContractOrdering.Apply(
            [
                Task("Booking screen", WebRepo),
                Task("Bookings schema", ApiRepo),
                Task("Bookings endpoint", ApiRepo, [1]),
            ],
            layers, ByRepoName);

        result[0].Title.Should().Be("Bookings schema");
        result[1].Title.Should().Be("Bookings endpoint");
        result[1].DependsOn.Should().Equal([0], "the model's own build order must be preserved");

        result[2].Title.Should().Be("Booking screen");
        result[2].DependsOn.Should().BeEquivalentTo([0, 1], "the screen waits for the whole surface");
    }

    [Fact]
    public async Task WithinALayerTheModelsOrderIsUntouched()
    {
        var layers = await LayersAsync((AgentRole.BackendDev, ApiRepo), (AgentRole.FrontendDev, WebRepo));

        var result = ApiContractOrdering.Apply(
            [
                Task("API one", ApiRepo),
                Task("API two", ApiRepo),
                Task("API three", ApiRepo),
                Task("Screen", WebRepo),
            ],
            layers, ByRepoName);

        result.Select(t => t.Title).Take(3).Should().Equal("API one", "API two", "API three");
    }

    [Fact]
    public async Task AllApiWork_IsLeftAlone()
    {
        var layers = await LayersAsync((AgentRole.BackendDev, ApiRepo), (AgentRole.FrontendDev, WebRepo));

        var tasks = new List<DecomposedTask> { Task("API one", ApiRepo), Task("API two", ApiRepo) };
        var result = ApiContractOrdering.Apply(tasks, layers, ByRepoName);

        // No cross-layer edge exists, so the pass must not reshuffle or add anything.
        result.Should().BeSameAs(tasks);
    }

    [Fact]
    public async Task AFullStackProject_IsNotLayered_AndIsLeftAlone()
    {
        // Managed projects: Nova writes the API route and the screen calling it in ONE task.
        // Imposing an order here would serialise a single change against itself.
        var layers = await LayersAsync((AgentRole.FullStackDev, WebRepo));
        layers.IsLayered.Should().BeFalse();

        var tasks = new List<DecomposedTask> { Task("Screen", WebRepo), Task("Route", WebRepo) };
        ApiContractOrdering.Apply(tasks, layers, ByRepoName).Should().BeSameAs(tasks);
    }

    [Fact]
    public async Task ASingleSidedRoster_IsNotLayered()
    {
        // Two backend repos and no client: every edge layering would add is pure serialisation
        // of work that could have run at once.
        var layers = await LayersAsync((AgentRole.BackendDev, ApiRepo), (AgentRole.DevOps, MobileRepo));
        layers.IsLayered.Should().BeFalse();
    }

    [Fact]
    public async Task ARepoServingBothSides_CollapsesToUnknown()
    {
        var layers = await LayersAsync(
            (AgentRole.BackendDev, ApiRepo),
            (AgentRole.FrontendDev, ApiRepo),
            (AgentRole.FrontendDev, WebRepo));

        layers.LayerOf(ApiRepo).Should().Be(WorkLayer.Unknown,
            "a repo both sides work in has no single layer, and guessing one would be worse");
    }

    [Fact]
    public async Task UnknownWork_NeitherBlocksTheClientNorRunsBeforeTheApi()
    {
        var layers = await LayersAsync((AgentRole.BackendDev, ApiRepo), (AgentRole.FrontendDev, WebRepo));
        var unknownRepo = Ulid.NewUlid();

        var result = ApiContractOrdering.Apply(
            [Task("Screen", WebRepo), Task("Docs", unknownRepo), Task("Endpoint", ApiRepo)],
            layers, ByRepoName);

        result.Select(t => t.Title).Should().Equal("Endpoint", "Docs", "Screen");
        result[1].DependsOn.Should().BeNull("unlayered work must not be forced to wait");
        result[2].DependsOn.Should().Equal([0], "and must not become a blocker for the client");
    }
}
