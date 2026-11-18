namespace Orkka.Tests.Worker;

using FluentAssertions;
using Orkka.Modules.Execution.Orchestration;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// Resuming a session that was CUT OFF is a different instruction from resuming one that
/// finished and is being re-entered. "The task was re-queued for another development pass"
/// invites a fresh survey of the work; an interrupted run needs the opposite — carry on from
/// where the stream died, and leave the half-written worktree alone rather than starting over.
/// </summary>
public sealed class InterruptedResumePromptTests
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

    [Fact]
    public void AnInterruptedRun_IsToldToCarryOn_NotToStartOver()
    {
        var message = TaskOrchestrator.BuildResumeDevelopingUserMessage(
            MakeTask(), newConversation: null, interrupted: true);

        message.Should().Contain("CUT OFF");
        message.Should().Contain("Do not start the task over");
        message.Should().Contain("still in your worktree",
            "the edits the dead run wrote are the reason not to begin again");
        message.Should().NotContain("re-queued for another development pass",
            "that framing invites exactly the fresh survey this is meant to prevent");
    }

    [Fact]
    public void ACleanReEntry_KeepsItsOriginalFraming()
    {
        var message = TaskOrchestrator.BuildResumeDevelopingUserMessage(
            MakeTask(), newConversation: null);

        message.Should().Contain("re-queued for another development pass");
        message.Should().NotContain("CUT OFF");
    }

    /// <summary>
    /// A run cut off mid-tool-call leaves the CLI's own cancellation notice in the resumed
    /// session's transcript, worded as a human refusal ("The user doesn't want to take this
    /// action right now", toolDenialKind "cancelled"). DEN-5 (2026-08-24) read four of those
    /// as a permissions restriction and parked the task to ask whether creating files was
    /// forbidden. The disarm is unconditional: the poisoned entry can sit in a session whose
    /// run still COMPLETED normally afterwards, so it resumes with interrupted: false.
    /// </summary>
    [Fact]
    public void EveryResume_DisarmsTheCliCancellationNotice_AsNotAHumanRefusal()
    {
        foreach (var interrupted in new[] { true, false })
        {
            var message = TaskOrchestrator.BuildResumeDevelopingUserMessage(
                MakeTask(), newConversation: null, interrupted: interrupted);

            message.Should().Contain("The user doesn't want to take this action right now",
                "the disarm must quote the CLI's exact denial wording so the agent can match it");
            message.Should().Contain("NOT a human refusing the action",
                $"interrupted: {interrupted} resumes can carry the poisoned transcript entry");
            message.Should().Contain("NOT a permissions restriction");
        }
    }

    [Fact]
    public void AnInterruptedRun_StillGetsTheNewFeedback()
    {
        var message = TaskOrchestrator.BuildResumeDevelopingUserMessage(
            MakeTask(),
            newConversation: "New feedback since your last run\nThe login button is the wrong colour.",
            interrupted: true);

        message.Should().Contain("CUT OFF");
        message.Should().Contain("The login button is the wrong colour.");
        message.Should().Contain("Address EVERY point above");
    }
}
