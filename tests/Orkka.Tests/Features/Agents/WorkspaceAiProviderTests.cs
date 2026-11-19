namespace Orkka.Tests.Features.Agents;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Features.Agents;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Agents.Infrastructure.EventHandlers;
using Orkka.Modules.Agents.Infrastructure.Services;
using Orkka.Modules.Organizations.Contracts;
using Orkka.Modules.Runners.Contracts;
using Orkka.SharedKernel.Events.Catalog;

/// <summary>Configurable readiness: the listed providers can run, the rest are blocked.</summary>
file sealed class ListReadiness(params AiProvider[] connected) : IProviderReadiness
{
    public string? GetBlocker(AiProvider provider) =>
        connected.Contains(provider) ? null : $"{provider} is not signed in";

    public IReadOnlyList<AiProvider> Connected { get; } = connected;

    public string? GetAnyProviderBlocker() => connected.Length > 0 ? null : "nothing signed in";
}

/// <summary>
/// The workspace "AI provider" setting: a developer whose employer pays for Copilot says so once,
/// and every new team of that workspace runs on Copilot Auto — while a choice that cannot run
/// never strands work (it falls back to the Automatic pick and says why).
/// </summary>
public sealed class WorkspaceAiProviderTests
{
    private static readonly ModelCatalogSnapshot Catalog = ModelCatalogSnapshot.Static;
    private const string User = "local_user";

    // ── PreferredProvider(chosen) ───────────────────────────────────────────

    [Fact]
    public void Chosen_AndReady_IsUsed_EvenWhenClaudeCanRun()
        => new ListReadiness(AiProvider.Anthropic, AiProvider.GitHubCopilot)
            .PreferredProvider(AiProvider.GitHubCopilot).Should().Be(AiProvider.GitHubCopilot);

    [Fact]
    public void Chosen_ButNotReady_FallsBackToClaude_WhenClaudeCanRun()
        => new ListReadiness(AiProvider.Anthropic)
            .PreferredProvider(AiProvider.GitHubCopilot).Should().Be(AiProvider.Anthropic);

    [Fact]
    public void Chosen_ButNotReady_FallsBackToTheFirstConnected_WhenClaudeCannotRun()
        => new ListReadiness(AiProvider.OpenAI)
            .PreferredProvider(AiProvider.GitHubCopilot).Should().Be(AiProvider.OpenAI);

    [Fact]
    public void Automatic_IsTheOldRule()
    {
        new ListReadiness(AiProvider.Anthropic, AiProvider.GitHubCopilot)
            .PreferredProvider(null).Should().Be(AiProvider.Anthropic);
        new ListReadiness(AiProvider.GitHubCopilot)
            .PreferredProvider(null).Should().Be(AiProvider.GitHubCopilot);
        new ListReadiness().PreferredProvider(null).Should().Be(AiProvider.Anthropic);
    }

    // ── The choice keys ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("claude", AiProvider.Anthropic)]
    [InlineData("codex", AiProvider.OpenAI)]
    [InlineData("Copilot", AiProvider.GitHubCopilot)]
    [InlineData("gemini", AiProvider.Google)]
    [InlineData("grok", AiProvider.XAI)]
    public void EveryChoice_NamesAProvider_AndRoundTripsThroughItsCliKey(string key, AiProvider provider)
    {
        WorkspaceAiProviders.ToProvider(key).Should().Be(provider);
        WorkspaceAiProviders.CliKey(provider).Should().Be(key.ToLowerInvariant());
        WorkspaceAiProviders.Normalize(key).Should().Be(key.ToLowerInvariant());
        WorkspaceAiProviders.ToStored(key.ToLowerInvariant()).Should().Be(key.ToLowerInvariant());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("automatic")]
    public void Automatic_IsStoredAsNull(string? value)
    {
        WorkspaceAiProviders.Normalize(value).Should().Be(WorkspaceAiProviders.Automatic);
        WorkspaceAiProviders.ToStored(WorkspaceAiProviders.Automatic).Should().BeNull();
        WorkspaceAiProviders.ToProvider(value).Should().BeNull();
    }

    [Theory]
    [InlineData("ollama")] // models depend on what the machine pulled — no workspace default
    [InlineData("opencode")]
    [InlineData("pixellab")]
    [InlineData("nope")]
    public void ProvidersWithoutFixedTierModels_AreNotChoices(string value)
        => WorkspaceAiProviders.Normalize(value).Should().BeNull();

    // ── The resolver (choice + the workspace's runner coverage) ─────────────

    private static RunnerProviderCoverage Coverage(params string[] ready) => new(
        "ruan-laptop", DateTimeOffset.UtcNow, Online: true,
        [
            .. new[] { RunnerProviders.Claude, RunnerProviders.Codex, RunnerProviders.Copilot }
                .Select(p => new RunnerProviderStatus(p, CliFound: true, Authenticated: ready.Contains(p),
                    Detail: ready.Contains(p) ? null : $"{p} is not logged in")),
        ]);

    private static WorkspaceAiProviderResolver Resolver(
        string? stored, RunnerProviderCoverage? coverage, Ulid orgId, string? presenceUser = null)
    {
        var settings = new Mock<IWorkspaceAiSettings>();
        settings.Setup(s => s.GetAiProviderAsync(orgId, It.IsAny<CancellationToken>())).ReturnsAsync(stored);
        var fleet = new Mock<IRunnerFleet>();
        fleet.Setup(f => f.GetProviderCoverageAsync(orgId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(coverage);
        fleet.Setup(f => f.ListPresenceAsync(orgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(presenceUser is null
                ? []
                : [new RunnerPresenceSnapshot(orgId, presenceUser, true, "ruan-laptop", null, "w", default, default, "[]", null)]);
        return new WorkspaceAiProviderResolver(settings.Object, fleet.Object,
            new AlwaysReadyProviderReadiness(), NullLogger<WorkspaceAiProviderResolver>.Instance);
    }

    [Fact]
    public async Task Copilot_ChosenAndSignedIn_IsWhatNewAgentsGet()
    {
        var orgId = Ulid.NewUlid();
        var r = await Resolver("copilot", Coverage(RunnerProviders.Claude, RunnerProviders.Copilot), orgId)
            .ResolveAsync(orgId, User);

        r.Should().Be(new WorkspaceProviderResolution("copilot", AiProvider.GitHubCopilot, null));
    }

    [Fact]
    public async Task Copilot_ChosenButSignedOut_FallsBackToClaude_AndSaysWhy()
    {
        var orgId = Ulid.NewUlid();
        var r = await Resolver("copilot", Coverage(RunnerProviders.Claude), orgId).ResolveAsync(orgId, User);

        r.Choice.Should().Be("copilot");
        r.Provider.Should().Be(AiProvider.Anthropic);
        r.Blocker.Should().Be("copilot is not logged in", "the runner's own detail is the actionable reason");
    }

    [Fact]
    public async Task Chosen_WithNoRunnerEverSeen_IsTrusted()
    {
        // Nothing is known yet (a fresh install whose runner has not said hello): the choice
        // stands — the run-time route adapts and says so if it really cannot run.
        var orgId = Ulid.NewUlid();
        var r = await Resolver("copilot", coverage: null, orgId).ResolveAsync(orgId, User);

        r.Provider.Should().Be(AiProvider.GitHubCopilot);
        r.Blocker.Should().BeNull();
    }

    [Fact]
    public async Task Automatic_ClaudeWhenItCanRun_ElseTheSignedInProvider()
    {
        var orgId = Ulid.NewUlid();
        (await Resolver(null, Coverage(RunnerProviders.Claude, RunnerProviders.Copilot), orgId)
            .ResolveAsync(orgId, User)).Provider.Should().Be(AiProvider.Anthropic);
        (await Resolver(null, Coverage(RunnerProviders.Copilot), orgId)
            .ResolveAsync(orgId, User)).Should().Be(
                new WorkspaceProviderResolution(WorkspaceAiProviders.Automatic, AiProvider.GitHubCopilot, null),
                "a Copilot-only developer already gets Copilot on Automatic");
        (await Resolver(null, coverage: null, orgId)
            .ResolveAsync(orgId, User)).Provider.Should().Be(AiProvider.Anthropic,
                "nothing known = the behaviour before the setting existed");
    }

    [Fact]
    public async Task WithoutAUser_TheWorkspacesRunnerIsRead()
    {
        // The startup roster backfill has no request user.
        var orgId = Ulid.NewUlid();
        var r = await Resolver("copilot", Coverage(RunnerProviders.Claude), orgId, presenceUser: User)
            .ResolveAsync(orgId, userId: null);

        r.Provider.Should().Be(AiProvider.Anthropic);
        r.Blocker.Should().NotBeNull();
    }

    [Fact]
    public async Task AnUnknownStoredKey_ReadsAsAutomatic()
    {
        var orgId = Ulid.NewUlid();
        var r = await Resolver("ollama", Coverage(RunnerProviders.Claude), orgId).ResolveAsync(orgId, User);
        r.Choice.Should().Be(WorkspaceAiProviders.Automatic);
        r.Provider.Should().Be(AiProvider.Anthropic);
    }

    // ── Seeding ─────────────────────────────────────────────────────────────

    [Fact]
    public void ACopilotPod_IsCopilotAutoEverywhere_ExceptQa()
    {
        var pod = AgentSeeder.CreateDefaultPod(Ulid.NewUlid(), Ulid.NewUlid(), Catalog, AiProvider.GitHubCopilot);

        pod.Where(a => a.Role != AgentRole.QA).Should().AllSatisfy(a => a.Model.Should().Be("copilot/auto"));
        pod.Single(a => a.Role == AgentRole.QA).Model.Should().Be(Catalog.QaModel,
            "QA drives Playwright MCP and no Copilot model can");
        Catalog.SupportsMcp(Catalog.QaModel).Should().BeTrue();
    }

    [Fact]
    public async Task ProjectCreated_InACopilotWorkspace_SeedsCopilotAuto()
    {
        var orgId = Ulid.NewUlid();
        var projectId = Ulid.NewUlid();
        await using var db = new AgentsDbContext(new DbContextOptionsBuilder<AgentsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var resolver = new Mock<IWorkspaceAiProviderResolver>();
        resolver.Setup(r => r.ResolveAsync(orgId, User, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkspaceProviderResolution("copilot", AiProvider.GitHubCopilot, null));
        var catalog = new Mock<IModelCatalog>();
        catalog.Setup(c => c.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Catalog);

        await new AgentSeedOnProjectCreated(db, resolver.Object, catalog.Object,
                NullLogger<AgentSeedOnProjectCreated>.Instance)
            .HandleAsync(new ProjectCreated(projectId, orgId, User), CancellationToken.None);

        var agents = await db.ProjectAgents.IgnoreQueryFilters().Where(a => a.ProjectId == projectId).ToListAsync();
        agents.Should().NotBeEmpty();
        agents.Where(a => a.Role != AgentRole.QA).Should().AllSatisfy(a => a.Model.Should().Be("copilot/auto"));
        agents.Single(a => a.Role == AgentRole.QA).Model.Should().Be(Catalog.QaModel);
    }

    // ── The bulk switch ─────────────────────────────────────────────────────

    [Fact]
    public async Task BulkSwitch_MovesEveryProjectsAgents_SkipsQa_AndAuditsEachChange()
    {
        var orgId = Ulid.NewUlid();
        await using var db = new AgentsDbContext(new DbContextOptionsBuilder<AgentsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var projectA = AgentSeeder.CreateDefaultPod(orgId, Ulid.NewUlid(), Catalog);
        var projectB = AgentSeeder.CreateDefaultPod(orgId, Ulid.NewUlid(), Catalog);
        db.ProjectAgents.AddRange([.. projectA, .. projectB]);
        await db.SaveChangesAsync();
        var audit = new Mock<IAgentAuditLog>();

        var agents = await db.ProjectAgents.IgnoreQueryFilters().ToListAsync();
        var result = await AgentModelBulkUpdate.ApplyAsync(db, audit.Object, Catalog, orgId, User, agents,
            a => WorkspaceAiProviders.ModelForRole(a.Role, AiProvider.GitHubCopilot, Catalog),
            "workspace AI provider set to copilot", CancellationToken.None);

        result.SkippedQaCount.Should().Be(2, "one QA agent per project stays on Claude");
        result.UpdatedCount.Should().Be(agents.Count - 2);
        result.UnchangedCount.Should().Be(0);
        var after = await db.ProjectAgents.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        after.Where(a => a.Role != AgentRole.QA).Should().AllSatisfy(a => a.Model.Should().Be("copilot/auto"));
        after.Where(a => a.Role == AgentRole.QA).Should().AllSatisfy(a => a.Model.Should().Be(Catalog.QaModel));
        audit.Verify(a => a.RecordAsync(
            It.Is<AgentAuditRecord>(r => r.EventType == AgentAuditEventType.AgentConfigChanged && r.OrgId == orgId),
            It.IsAny<CancellationToken>()), Times.Exactly(agents.Count - 2));

        // Back to Claude: the tier models per role, QA included (Claude drives the browser).
        var back = await AgentModelBulkUpdate.ApplyAsync(db, audit.Object, Catalog, orgId, User,
            await db.ProjectAgents.IgnoreQueryFilters().ToListAsync(),
            a => WorkspaceAiProviders.ModelForRole(a.Role, AiProvider.Anthropic, Catalog),
            "workspace AI provider set to claude", CancellationToken.None);
        back.SkippedQaCount.Should().Be(0);
        var claude = await db.ProjectAgents.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        claude.Single(a => a.Role == AgentRole.PM && a.ProjectId == projectA[0].ProjectId)
            .Model.Should().Be(Catalog.FastModel(AiProvider.Anthropic));
        claude.Where(a => a.Role != AgentRole.PM).Should().AllSatisfy(a =>
            a.Model.Should().Be(Catalog.CapableModel(AiProvider.Anthropic)));
    }
}
