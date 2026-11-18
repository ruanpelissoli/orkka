namespace Orkka.Tests.Worker;

using FluentAssertions;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Execution.Orchestration;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// The task create form lets a user pick a model alongside the dev agent that will pick the
/// task up. That override must beat the agent's own Team-page model for dev work — and must
/// NOT leak into the PM, QA or Code Reviewer phases, which run different agents (QA in
/// particular needs an MCP-capable Claude model to drive Playwright).
/// </summary>
public sealed class TaskModelResolutionTests
{
    private static TaskSnapshot MakeTask(string? model) => new(
        Id: Ulid.NewUlid(), OrgId: Ulid.NewUlid(), ProjectId: Ulid.NewUlid(), RepoId: null,
        Number: 1, Title: "Add player dash", Description: null,
        Priority: Priority.Medium, Type: TaskType.Feature,
        CurrentLane: Lane.Developing, Status: TaskStatus.Active, IsArchived: false,
        RequesterLocale: null, TotalTokensUsed: 0, HasAgentWorking: false,
        BranchName: null, PrUrl: null, Tags: null,
        ContextFiles: null, ContextDocumentIds: null,
        SkipPlanning: false, SkipTesting: false, SkipCodeReview: false, IsMerged: false,
        ReviewCycleCount: 0, QaCycleCount: 0, QuestionSourceLane: null,
        AssignedAgentId: null, CurrentAgentId: null, SourceMessageId: null,
        StartedAt: null, CompletedAt: null,
        Model: model);

    private static AgentSummary MakeAgent(AgentRole role, string model = "claude-sonnet-4-6") => new(
        Id: Ulid.NewUlid(), OrgId: Ulid.NewUlid(), ProjectId: Ulid.NewUlid(), RepoIds: [],
        Name: role.ToString(), Role: role, Model: model,
        SystemPrompt: "persona", UserInstructions: "");

    private static ProjectSummary MakeProject(string? defaultModel) => new(
        Id: Ulid.NewUlid(), OrgId: Ulid.NewUlid(), Name: "Orkka", Slug: "orkka", Key: "ORK",
        Settings: new ProjectSettingsDto(MaxParallelTasks: 1, SameRepoParallelism: false,
            DefaultModel: defaultModel));

    [Fact]
    public void DevAgent_UsesTaskOverride_OverItsOwnModel()
        => TaskOrchestrator.ResolveModel(
                MakeTask("claude-opus-5"),
                MakeAgent(AgentRole.FullStackDev),
                MakeProject(null))
            .Should().Be("claude-opus-5");

    [Theory]
    [InlineData(AgentRole.QA)]
    [InlineData(AgentRole.PM)]
    [InlineData(AgentRole.CodeReviewer)]
    public void NonDevAgent_KeepsItsOwnModel_DespiteTaskOverride(AgentRole role)
        => TaskOrchestrator.ResolveModel(
                MakeTask("gpt-5.5-codex"),
                MakeAgent(role),
                MakeProject(null))
            .Should().Be("claude-sonnet-4-6");

    [Fact]
    public void NoOverride_FallsBackToAgentModel()
        => TaskOrchestrator.ResolveModel(
                MakeTask(null),
                MakeAgent(AgentRole.BackendDev, "claude-haiku-4-5"),
                MakeProject("claude-opus-4-6"))
            .Should().Be("claude-haiku-4-5");

    [Fact]
    public void BlankOverride_FallsBackToAgentModel()
        => TaskOrchestrator.ResolveModel(
                MakeTask("   "),
                MakeAgent(AgentRole.BackendDev),
                MakeProject(null))
            .Should().Be("claude-sonnet-4-6");

    [Fact]
    public void NoAgent_FallsBackToProjectDefault()
        // Without an agent there is no role to check, so an override can't apply — the
        // utility/fallback paths that pass no agent keep today's behavior.
        => TaskOrchestrator.ResolveModel(MakeTask("claude-opus-5"), null, MakeProject("claude-sonnet-5"))
            .Should().Be("claude-sonnet-5");
}
