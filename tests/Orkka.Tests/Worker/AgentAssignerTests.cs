namespace Orkka.Tests.Worker;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Execution.Agents;
using Orkka.Modules.Tasks.Contracts;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// Dev-lane assignment must be deterministic and role-prioritized: the generalist
/// FullStackDev outranks specialists, and GameDev comes last so it never
/// steals generic tasks — it wins only via repo scoping, explicit assignment, or
/// being the sole dev agent.
/// </summary>
public sealed class AgentAssignerTests
{
    private static readonly Ulid ProjectId = Ulid.NewUlid();

    private static TaskSnapshot MakeTask(Ulid? repoId = null) => new(
        Id: Ulid.NewUlid(), OrgId: Ulid.NewUlid(), ProjectId: ProjectId, RepoId: repoId,
        Number: 1, Title: "Add player dash", Description: "Add a dash ability",
        Priority: Priority.Medium, Type: TaskType.Feature,
        CurrentLane: Lane.Developing, Status: TaskStatus.Active, IsArchived: false,
        RequesterLocale: null, TotalTokensUsed: 0, HasAgentWorking: false,
        BranchName: null, PrUrl: null, Tags: null,
        ContextFiles: null, ContextDocumentIds: null,
        SkipPlanning: false, SkipTesting: false, SkipCodeReview: false, IsMerged: false,
        ReviewCycleCount: 0, QaCycleCount: 0, QuestionSourceLane: null,
        AssignedAgentId: null, CurrentAgentId: null, SourceMessageId: null,
        StartedAt: null, CompletedAt: null);

    private static AgentSummary MakeAgent(AgentRole role, params Ulid[] repoIds) => new(
        Id: Ulid.NewUlid(), OrgId: Ulid.NewUlid(), ProjectId: ProjectId, RepoIds: repoIds,
        Name: role.ToString(), Role: role, Model: "claude-sonnet-4-6",
        SystemPrompt: "persona", UserInstructions: "");

    private static Task<AgentSummary?> AssignAsync(TaskSnapshot task, params AgentSummary[] agents)
    {
        var api = new Mock<IAgentsApi>();
        api.Setup(a => a.ListAgentsAsync(ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(agents);
        return new AgentAssigner(NullLogger<AgentAssigner>.Instance)
            .AssignAgentAsync(api.Object, task, Lane.Developing);
    }

    [Fact]
    public async Task CrossRepoFullStackDev_OutranksCrossRepoGameDev()
    {
        var gameDev = MakeAgent(AgentRole.GameDev);
        var fullStack = MakeAgent(AgentRole.FullStackDev);

        // GameDev listed first — priority ordering, not listing order, must decide.
        var assigned = await AssignAsync(MakeTask(), gameDev, fullStack);

        assigned!.Id.Should().Be(fullStack.Id);
    }

    [Fact]
    public async Task RepoScopedGameDev_WinsTasksForItsRepo()
    {
        var repoId = Ulid.NewUlid();
        var gameDev = MakeAgent(AgentRole.GameDev, repoId);
        var fullStack = MakeAgent(AgentRole.FullStackDev);

        var assigned = await AssignAsync(MakeTask(repoId), fullStack, gameDev);

        assigned!.Id.Should().Be(gameDev.Id);
    }

    [Fact]
    public async Task MultiRepoScopedGameDev_WinsTasksForEachOfItsRepos()
    {
        var gameRepo = Ulid.NewUlid();
        var assetsRepo = Ulid.NewUlid();
        var otherRepo = Ulid.NewUlid();
        var gameDev = MakeAgent(AgentRole.GameDev, gameRepo, assetsRepo);
        var fullStack = MakeAgent(AgentRole.FullStackDev);

        (await AssignAsync(MakeTask(gameRepo), fullStack, gameDev))!.Id.Should().Be(gameDev.Id);
        (await AssignAsync(MakeTask(assetsRepo), fullStack, gameDev))!.Id.Should().Be(gameDev.Id);
        // Outside its scope the cross-repo generalist wins.
        (await AssignAsync(MakeTask(otherRepo), fullStack, gameDev))!.Id.Should().Be(fullStack.Id);
    }

    [Fact]
    public async Task GameDev_IsAssignedWhenItIsTheOnlyDevAgent()
    {
        var gameDev = MakeAgent(AgentRole.GameDev);

        var assigned = await AssignAsync(MakeTask(), MakeAgent(AgentRole.PM), gameDev);

        assigned!.Id.Should().Be(gameDev.Id);
    }

    [Fact]
    public async Task NonDevSpecialistRoles_AreNeverPickedForDevLanes()
    {
        var assigned = await AssignAsync(
            MakeTask(), MakeAgent(AgentRole.GameDesigner), MakeAgent(AgentRole.DBA),
            MakeAgent(AgentRole.Legal));

        assigned.Should().BeNull();
    }

    // AgentAssigner.DevRoles (ordered priority list) and AgentRoleExtensions.IsDevRole
    // (used by task-assignment validation) must agree on membership — a role the
    // endpoints allow pinning but the assigner won't auto-pick is fine, the reverse
    // silently starves lanes. Pinned behaviorally since DevRoles is private.
    [Fact]
    public async Task DevLaneEligibility_MatchesIsDevRole_ForEveryRole()
    {
        foreach (var role in Enum.GetValues<AgentRole>())
        {
            var assigned = await AssignAsync(MakeTask(), MakeAgent(role));
            (assigned is not null).Should().Be(role.IsDevRole(),
                $"{role} eligibility must match AgentRoleExtensions.IsDevRole");
        }
    }
}
