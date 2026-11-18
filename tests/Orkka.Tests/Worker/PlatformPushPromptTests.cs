namespace Orkka.Tests.Worker;

using FluentAssertions;
using Orkka.Modules.Execution.Orchestration;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// The platform pushes the task branch after every run, and the command guard refuses an
/// agent's <c>git push</c> — so the prompt tells the agent to commit only (it would otherwise
/// burn turns "troubleshooting" a push that can never succeed).
/// </summary>
public sealed class PlatformPushPromptTests
{
    private static TaskSnapshot MakeTask() => new(
        Id: Ulid.NewUlid(), OrgId: Ulid.NewUlid(), ProjectId: Ulid.NewUlid(), RepoId: Ulid.NewUlid(),
        Number: 3, Title: "Add todo list", Description: "CRUD for todos",
        Priority: Priority.High, Type: TaskType.Feature,
        CurrentLane: Lane.Developing, Status: TaskStatus.Active, IsArchived: false,
        RequesterLocale: null, TotalTokensUsed: 0, HasAgentWorking: true,
        BranchName: "orkka/FT-3", PrUrl: null, Tags: null,
        ContextFiles: null, ContextDocumentIds: null,
        SkipPlanning: false, SkipTesting: false, SkipCodeReview: false, IsMerged: false,
        ReviewCycleCount: 0, QaCycleCount: 0, QuestionSourceLane: null,
        AssignedAgentId: null, CurrentAgentId: null, SourceMessageId: null,
        StartedAt: null, CompletedAt: null, CheckCycleCount: 0);

    private static ProjectSummary MakeProject() => new(
        Ulid.NewUlid(), Ulid.NewUlid(), "FancyTodo", "fancytodo", "FT", Settings: null);

    private static RepoSummary MakeRepo() => new(
        Id: Ulid.NewUlid(), ProjectId: Ulid.NewUlid(), OrgId: Ulid.NewUlid(),
        Name: "fancytodo", GitUrl: "https://github.com/acme/fancytodo.git", DefaultBranch: "main",
        CardColor: "#000000", WorkspaceStatus: WorkspaceStatus.Ready);

    [Fact]
    public void TheAgentIsToldToCommitOnly()
    {
        var (_, user) = TaskOrchestrator.BuildDevelopingPrompt(
            MakeTask(), MakeProject(), MakeRepo(), agent: null, memories: "", plan: "{}",
            contextFilesSection: null, conversationSection: null);

        user.Should().Contain("git commit");
        user.Should().Contain("Do NOT run `git push`");
        user.Should().NotContain("git push origin");
        user.Should().Contain("pushes the branch itself", "the agent is told who does push, not just told not to");
    }

    [Fact]
    public void TheResumeMessage_FollowsTheSameRule()
    {
        var message = TaskOrchestrator.BuildResumeDevelopingUserMessage(MakeTask(), null);
        message.Should().Contain("Do NOT run `git push`");
        message.Should().NotContain("git push origin");
    }
}
