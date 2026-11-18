namespace Orkka.Tests.Worker;

using FluentAssertions;
using Orkka.Modules.Execution.Orchestration;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Utilities;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// Pipeline prompts follow the PROJECT's language setting, not the locale frozen on the task
/// when it was created. Changing the project language must reach existing tasks on their next
/// run — resolution happens at prompt-build time, and these tests are what pin that.
/// </summary>
public sealed class PromptProjectLanguageTests
{
    private static TaskSnapshot MakeTask(string? requesterLocale) => new(
        Id: Ulid.NewUlid(), OrgId: Ulid.NewUlid(), ProjectId: Ulid.NewUlid(), RepoId: Ulid.NewUlid(),
        Number: 7, Title: "Active bets endpoint", Description: "GET /v1/bets/active",
        Priority: Priority.High, Type: TaskType.Feature,
        CurrentLane: Lane.Developing, Status: TaskStatus.Active, IsArchived: false,
        RequesterLocale: requesterLocale, TotalTokensUsed: 0, HasAgentWorking: true,
        BranchName: "orkka/TAP-13", PrUrl: null, Tags: null,
        ContextFiles: null, ContextDocumentIds: null,
        SkipPlanning: false, SkipTesting: false, SkipCodeReview: false, IsMerged: false,
        ReviewCycleCount: 0, QaCycleCount: 0, QuestionSourceLane: null,
        AssignedAgentId: null, CurrentAgentId: null, SourceMessageId: null,
        StartedAt: null, CompletedAt: null);

    private static ProjectSummary MakeProject(string? language) => new(
        Ulid.NewUlid(), Ulid.NewUlid(), "Tapago", "tapago", "TAP", new ProjectSettingsDto(MaxParallelTasks: 1, SameRepoParallelism: false) with { Language = language });

    private static AgentSummary MakeAgent() => new(
        Ulid.NewUlid(), Ulid.NewUlid(), Ulid.NewUlid(), [],
        "Atlas", AgentRole.BackendDev, "claude-opus-5",
        "You are Atlas, a Senior Backend Developer.", "");

    [Fact]
    public void ProjectLanguageOverridesTheTaskFrozenLocale()
    {
        // Task was created while the requester's UI was German; the project has since been
        // set to Portuguese. The next run speaks Portuguese.
        var (system, _) = TaskOrchestrator.BuildDevelopingPrompt(
            MakeTask(requesterLocale: "de"), MakeProject(language: "pt"), repo: null, MakeAgent(),
            memories: "", plan: "{}", contextFilesSection: null, conversationSection: null);

        system.Should().Contain("Portuguese (Brazilian)");
        system.Should().NotContain("German");
    }

    [Fact]
    public void ProjectPinnedToEnglishSaysNothingRegardlessOfTheRequester()
    {
        var (system, _) = TaskOrchestrator.BuildDevelopingPrompt(
            MakeTask(requesterLocale: "pt"), MakeProject(language: "en"), repo: null, MakeAgent(),
            memories: "", plan: "{}", contextFilesSection: null, conversationSection: null);

        system.Should().NotContain("## Response Language");
    }

    [Fact]
    public void LegacyProjectWithoutALanguageKeepsFollowingTheTaskLocale()
    {
        var (system, _) = TaskOrchestrator.BuildDevelopingPrompt(
            MakeTask(requesterLocale: "es"), MakeProject(language: null), repo: null, MakeAgent(),
            memories: "", plan: "{}", contextFilesSection: null, conversationSection: null);

        system.Should().Contain("Spanish");
    }

    [Fact]
    public void LanguageForResolvesProjectFirst()
    {
        TaskOrchestrator.LanguageFor(MakeTask("de"), MakeProject("fr")).Should().Be("fr");
        TaskOrchestrator.LanguageFor(MakeTask("de"), project: null).Should().Be("de");
    }

    [Fact]
    public void InstructionCoversThinkingAndDocuments()
    {
        // The setting exists for "chat replies, thinking and document generation" — the
        // instruction has to say so, or models keep narrating progress in English.
        var instruction = AgentLanguage.Instruction("pt");

        instruction.Should().Contain("reasoning you narrate");
        instruction.Should().Contain("document");
        instruction.Should().Contain("project's language setting");
    }
}
