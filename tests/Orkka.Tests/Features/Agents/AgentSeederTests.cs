namespace Orkka.Tests.Features.Agents;

using FluentAssertions;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Features.AgentChat;

/// <summary>
/// Every available agent is seeded with the project — there is no add/enable flow.
/// The singleton specialists (GameDev, GameDesigner, DBA, Legal, MobileDev) are part of every pod
/// regardless of project type, and the roster backfill relies on
/// AgentSeeder.CreateMissingSpecialists to top existing projects up.
/// </summary>
public sealed class AgentSeederTests
{
    /// <summary>Seeding resolves tier models through the catalog; the shipped snapshot is what
    /// a host with no admin overrides sees.</summary>
    private static readonly ModelCatalogSnapshot Catalog = ModelCatalogSnapshot.Static;

    private static readonly AgentRole[] SpecialistRoles =
        [AgentRole.GameDev, AgentRole.GameDesigner, AgentRole.DBA, AgentRole.Legal, AgentRole.MobileDev];

    [Fact]
    public void Specialists_ContainExactlyTheSingletonRoles()
        => AgentSeeder.Specialists.Select(s => s.Role).Should().BeEquivalentTo(SpecialistRoles);

    [Fact]
    public void EverySpecialist_HasCuratedNameAvatarAndPersona()
    {
        foreach (var spec in AgentSeeder.Specialists)
        {
            spec.Name.Should().NotBeNullOrWhiteSpace();
            spec.Avatar.Should().NotBeNullOrWhiteSpace();
                DefaultPrompts.ForRole(spec.Role).Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void DefaultPod_SeedsEverySpecialist()
    {
        {
            var seeded = AgentSeeder.CreateDefaultPod(Ulid.NewUlid(), Ulid.NewUlid(), Catalog)
                .Select(a => a.Role).ToList();
            seeded.Should().Contain(SpecialistRoles, $"all available agents are always seeded");
            seeded.Should().OnlyHaveUniqueItems($"the pod has one agent per role");
        }
    }

    [Fact]
    public void DefaultPod_GetsTheFullDevBench()
    {
        var pod = AgentSeeder.CreateDefaultPod(Ulid.NewUlid(), Ulid.NewUlid(), Catalog)
            .Select(a => a.Role).ToList();
        pod.Should().Contain([AgentRole.FrontendDev, AgentRole.BackendDev, AgentRole.FullStackDev, AgentRole.DevOps]);
    }

    [Fact]
    public void EverySeededAgent_IsDefault_WithANonEmptyPersona()
    {
            AgentSeeder.CreateDefaultPod(Ulid.NewUlid(), Ulid.NewUlid(), Catalog)
                .Should().AllSatisfy(a =>
                {
                    a.IsDefault.Should().BeTrue("seeded agents are permanent");
                    a.SystemPrompt.Should().NotBeNullOrWhiteSpace();
                });
    }

    [Fact]
    public void CreateMissingSpecialists_TopsUpOnlyTheMissingRoles()
    {
        // A project seeded before Legal existed: has every other specialist.
        var existing = new[]
        {
            AgentRole.PM, AgentRole.FullStackDev, AgentRole.QA,
            AgentRole.GameDev, AgentRole.GameDesigner, AgentRole.DBA, AgentRole.MobileDev,
        };

        var missing = AgentSeeder.CreateMissingSpecialists(
            Ulid.NewUlid(), Ulid.NewUlid(), existing, AiProvider.Anthropic, Catalog);

        missing.Should().ContainSingle().Which.Role.Should().Be(AgentRole.Legal);
        missing[0].Name.Should().Be("Lex");
        missing[0].IsDefault.Should().BeTrue();
        missing[0].Model.Should().Be(ProviderModelDefaults.CapableModel(AiProvider.Anthropic));
    }

    [Fact]
    public void CreateMissingSpecialists_FullRoster_YieldsNothing()
        => AgentSeeder.CreateMissingSpecialists(
                Ulid.NewUlid(), Ulid.NewUlid(),
                AgentSeeder.Specialists.Select(s => s.Role).ToList(), AiProvider.Anthropic, Catalog)
            .Should().BeEmpty();

    // GameDesigner delivers assets exactly like Designer — via the runtime-injected
    // design block. Advisory roles (DBA, Legal) and GameDev must not receive
    // asset-creation instructions.
    [Theory]
    [InlineData(AgentRole.GameDesigner, true)]
    [InlineData(AgentRole.Designer, true)]
    [InlineData(AgentRole.DBA, false)]
    [InlineData(AgentRole.GameDev, false)]
    [InlineData(AgentRole.Legal, false)]
    [InlineData(AgentRole.MobileDev, false)]
    public void ChatSystemPrompt_InjectsDesignBlockOnlyForDesignerRoles(AgentRole role, bool expectsDesignBlock)
    {
        var agent = new ProjectAgent
        {
            OrgId = Ulid.NewUlid(),
            ProjectId = Ulid.NewUlid(),
            Name = "Test",
            Role = role,
            SystemPrompt = DefaultPrompts.ForRole(role),
        };

        var prompt = SendMessageEndpoint.BuildSystemPrompt(
            agent, memories: [], history: [], repos: [],
            contextSummary: null, locale: null,
            designAssetContext: null);

        prompt.Contains(DefaultPrompts.DesignerAssetCreation).Should().Be(expectsDesignBlock);
    }
}
