namespace Orkka.Tests.Features.Agents;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Execution.Agents;
using Orkka.Modules.Tasks.Contracts;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// The two shared answers the Tasks module, the forecaster and the orchestrator must agree
/// on: which model a dev run uses, and which dev agent picks up an unpinned task. A second
/// copy of either rule is how a task gets routed (and billed) for one model and run on another.
/// </summary>
public sealed class DevModelResolutionTests
{
    private static readonly Ulid ProjectId = Ulid.NewUlid();

    // ── DevModelResolution.Resolve ──────────────────────────────────────────

    [Theory]
    [InlineData("claude-opus-5", "gpt-5.6-sol", "claude-sonnet-5", "claude-opus-5")]
    [InlineData(null, "gpt-5.6-sol", "claude-sonnet-5", "gpt-5.6-sol")]
    [InlineData("", "gpt-5.6-sol", "claude-sonnet-5", "gpt-5.6-sol")]
    [InlineData("   ", "gpt-5.6-sol", "claude-sonnet-5", "gpt-5.6-sol")]
    [InlineData(null, null, "claude-sonnet-5", "claude-sonnet-5")]
    [InlineData(null, "", "claude-sonnet-5", "claude-sonnet-5")]
    [InlineData("  ", "\t", "claude-sonnet-5", "claude-sonnet-5")]
    [InlineData(null, null, null, null)]
    [InlineData("", "", null, null)]
    public void Resolve_PrefersOverride_ThenAgent_ThenProject_IgnoringWhitespace(
        string? taskOverride, string? agentModel, string? projectDefault, string? expected)
        => DevModelResolution.Resolve(taskOverride, agentModel, projectDefault).Should().Be(expected);

    [Fact]
    public void Resolve_ReturnsTheOverrideVerbatim()
    {
        // Trimming is the caller's job (the endpoints trim before storing); the rule itself
        // must not rewrite an id it was handed.
        DevModelResolution.Resolve("gpt-5.6-sol", null, null).Should().Be("gpt-5.6-sol");
    }

    // ── DevAgentSelection.FindDevAgent ──────────────────────────────────────

    private static AgentSummary Agent(AgentRole role, params Ulid[] repoIds) => new(
        Id: Ulid.NewUlid(), OrgId: Ulid.NewUlid(), ProjectId: ProjectId, RepoIds: repoIds,
        Name: role.ToString(), Role: role, Model: "claude-sonnet-5",
        SystemPrompt: "persona", UserInstructions: "");

    [Fact]
    public void DevRoles_AreInPriorityOrder_GeneralistFirst_NicheLast()
    {
        DevAgentSelection.DevRoles.Should().Equal(
            AgentRole.FullStackDev, AgentRole.BackendDev, AgentRole.FrontendDev,
            AgentRole.GameDev, AgentRole.MobileDev);
    }

    [Fact]
    public void DevRoles_MatchIsDevRole_ForEveryRole()
    {
        // The assigner's eligibility and the endpoints' pin validation must agree on membership.
        foreach (var role in Enum.GetValues<AgentRole>())
            DevAgentSelection.DevRoles.Contains(role).Should().Be(role.IsDevRole(),
                $"{role} must be a dev role for the selector iff AgentRoleExtensions says so");
    }

    [Fact]
    public void RoleOrder_DecidesAmongCrossRepoAgents_NotListingOrder()
    {
        var roles = DevAgentSelection.DevRoles;
        for (var i = 0; i < roles.Length; i++)
        {
            // Everything from priority i downwards, listed in REVERSE priority.
            var roster = roles.Skip(i).Reverse().Select(r => Agent(r)).ToList();

            DevAgentSelection.FindDevAgent(roster, repoId: null)!.Role.Should().Be(roles[i],
                $"with {roles[i]} present as the highest-priority dev role it must win");
        }
    }

    [Fact]
    public void RepoScopedAgent_WinsTasksForItsRepo()
    {
        var repo = Ulid.NewUlid();
        var gameDev = Agent(AgentRole.GameDev, repo);
        var fullStack = Agent(AgentRole.FullStackDev);

        DevAgentSelection.FindDevAgent([fullStack, gameDev], repo)!.Id.Should().Be(gameDev.Id);
        DevAgentSelection.FindDevAgent([fullStack, gameDev], Ulid.NewUlid())!.Id.Should().Be(fullStack.Id,
            "outside its scope the cross-repo generalist wins");
    }

    [Fact]
    public void RepoScopedAgent_WinsEachOfItsRepos_ByPriorityWithinTheScope()
    {
        var repoA = Ulid.NewUlid();
        var repoB = Ulid.NewUlid();
        var mobile = Agent(AgentRole.MobileDev, repoA, repoB);
        var frontend = Agent(AgentRole.FrontendDev, repoB);
        var fullStack = Agent(AgentRole.FullStackDev);

        DevAgentSelection.FindDevAgent([mobile, frontend, fullStack], repoA)!.Id.Should().Be(mobile.Id);
        DevAgentSelection.FindDevAgent([mobile, frontend, fullStack], repoB)!.Id.Should().Be(frontend.Id,
            "two agents scoped to the same repo are ordered by role priority");
    }

    [Fact]
    public void CrossRepoAgent_BeatsAHigherPriorityAgentScopedElsewhere()
    {
        var otherRepo = Ulid.NewUlid();
        var backendElsewhere = Agent(AgentRole.BackendDev, otherRepo);
        var frontendEverywhere = Agent(AgentRole.FrontendDev);

        var picked = DevAgentSelection.FindDevAgent([backendElsewhere, frontendEverywhere], Ulid.NewUlid());

        picked!.Id.Should().Be(frontendEverywhere.Id,
            "an agent scoped to another repo is a worse fit than one with no scope at all");
    }

    [Fact]
    public void WhenEveryAgentIsScopedElsewhere_TheHighestPriorityOneIsTheFallback()
    {
        var otherRepo = Ulid.NewUlid();
        var frontend = Agent(AgentRole.FrontendDev, otherRepo);
        var backend = Agent(AgentRole.BackendDev, otherRepo);

        DevAgentSelection.FindDevAgent([frontend, backend], Ulid.NewUlid())!.Id.Should().Be(backend.Id);
    }

    [Fact]
    public void TaskWithoutARepo_PrefersACrossRepoAgent_OverAScopedOne()
    {
        var fullStackScoped = Agent(AgentRole.FullStackDev, Ulid.NewUlid());
        var mobileEverywhere = Agent(AgentRole.MobileDev);

        DevAgentSelection.FindDevAgent([fullStackScoped, mobileEverywhere], repoId: null)!.Id
            .Should().Be(mobileEverywhere.Id);
    }

    [Fact]
    public void TaskWithoutARepo_FallsBackToPriority_WhenEveryoneIsScoped()
    {
        var mobile = Agent(AgentRole.MobileDev, Ulid.NewUlid());
        var backend = Agent(AgentRole.BackendDev, Ulid.NewUlid());

        DevAgentSelection.FindDevAgent([mobile, backend], repoId: null)!.Id.Should().Be(backend.Id);
    }

    [Fact]
    public void NonDevRoles_AreNeverPicked_EvenWhenScopedToTheRepo()
    {
        var repo = Ulid.NewUlid();

        DevAgentSelection.FindDevAgent(
            [Agent(AgentRole.PM, repo), Agent(AgentRole.QA, repo), Agent(AgentRole.GameDesigner, repo),
             Agent(AgentRole.DBA), Agent(AgentRole.CodeReviewer)],
            repo).Should().BeNull();
    }

    [Fact]
    public void NoDevAgents_ReturnsNull()
    {
        DevAgentSelection.FindDevAgent([], Ulid.NewUlid()).Should().BeNull();
        DevAgentSelection.FindDevAgent([], null).Should().BeNull();
        DevAgentSelection.FindDevAgent([Agent(AgentRole.PM)], null).Should().BeNull();
    }

    // ── AgentAssigner still agrees with the shared selector ─────────────────

    private static TaskSnapshot Task(Ulid? repoId, Ulid? assignedAgentId = null) => new(
        Id: Ulid.NewUlid(), OrgId: Ulid.NewUlid(), ProjectId: ProjectId, RepoId: repoId,
        Number: 1, Title: "Add player dash", Description: "Add a dash ability",
        Priority: Priority.Medium, Type: TaskType.Feature,
        CurrentLane: Lane.Developing, Status: TaskStatus.Active, IsArchived: false,
        RequesterLocale: null, TotalTokensUsed: 0, HasAgentWorking: false,
        BranchName: null, PrUrl: null, Tags: null,
        ContextFiles: null, ContextDocumentIds: null,
        SkipPlanning: false, SkipTesting: false, SkipCodeReview: false, IsMerged: false,
        ReviewCycleCount: 0, QaCycleCount: 0, QuestionSourceLane: null,
        AssignedAgentId: assignedAgentId, CurrentAgentId: null, SourceMessageId: null,
        StartedAt: null, CompletedAt: null);

    private static async Task<AgentSummary?> AssignAsync(TaskSnapshot task, IReadOnlyList<AgentSummary> agents)
    {
        var api = new Mock<IAgentsApi>();
        api.Setup(a => a.ListAgentsAsync(ProjectId, It.IsAny<CancellationToken>())).ReturnsAsync(agents);
        foreach (var agent in agents)
            api.Setup(a => a.GetAgentAsync(agent.Id, It.IsAny<CancellationToken>())).ReturnsAsync(agent);
        return await new AgentAssigner(NullLogger<AgentAssigner>.Instance)
            .AssignAgentAsync(api.Object, task, Lane.Developing);
    }

    public static IEnumerable<object?[]> Rosters()
    {
        var repo = Ulid.NewUlid();
        var other = Ulid.NewUlid();
        yield return [repo, new[] { Agent(AgentRole.GameDev), Agent(AgentRole.FullStackDev) }];
        yield return [repo, new[] { Agent(AgentRole.FullStackDev), Agent(AgentRole.GameDev, repo) }];
        yield return [repo, new[] { Agent(AgentRole.BackendDev, other), Agent(AgentRole.FrontendDev) }];
        yield return [repo, new[] { Agent(AgentRole.FrontendDev, other), Agent(AgentRole.BackendDev, other) }];
        yield return [null, new[] { Agent(AgentRole.FullStackDev, repo), Agent(AgentRole.MobileDev) }];
        yield return [null, new[] { Agent(AgentRole.PM), Agent(AgentRole.GameDev) }];
        yield return [repo, new[] { Agent(AgentRole.PM), Agent(AgentRole.QA) }];
        yield return [repo, Array.Empty<AgentSummary>()];
    }

    [Theory]
    [MemberData(nameof(Rosters))]
    public async Task AgentAssigner_PicksExactlyWhatTheSharedSelectorPicks(Ulid? repoId, AgentSummary[] roster)
    {
        var expected = DevAgentSelection.FindDevAgent(roster, repoId);

        var assigned = await AssignAsync(Task(repoId), roster);

        (assigned is null).Should().Be(expected is null);
        (assigned?.Id).Should().Be(expected?.Id);
    }

    [Fact]
    public async Task AgentAssigner_HonoursAnExplicitPin_WhichTheSelectorNeverSees()
    {
        // The routing decision reads the pinned agent directly (DevModelLookup) — the selector
        // only answers for unpinned tasks. Pin the lowest-priority dev role to prove the
        // assigner still takes it.
        var fullStack = Agent(AgentRole.FullStackDev);
        var mobile = Agent(AgentRole.MobileDev);

        var assigned = await AssignAsync(Task(null, assignedAgentId: mobile.Id), [fullStack, mobile]);

        assigned!.Id.Should().Be(mobile.Id);
        DevAgentSelection.FindDevAgent([fullStack, mobile], null)!.Id.Should().Be(fullStack.Id);
    }
}
