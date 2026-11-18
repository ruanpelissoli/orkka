namespace Orkka.Tests.Worker;

using Orkka.Modules.Execution.Agents;
using Orkka.Modules.Execution.Orchestration;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Utilities;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

/// <summary>
/// A local-folder task whose branch the merge cannot find in the user's repository. The
/// platform pushes the branch after every run; a task whose run ended before that existed
/// (prod, 2026-09-15: "Branch 'orkka/STO-2' is not in your repository") or whose push
/// failed still has the commits in its worktree on the machine that ran it. The merge sweep
/// pushes from there and merges again — instead of parking the task on a human retry.
/// </summary>
public sealed class LocalFolderMergeRepushTests
{
    private static readonly Ulid OrgId = Ulid.NewUlid();
    private static readonly Ulid ProjectId = Ulid.NewUlid();
    private static readonly Ulid RepoId = Ulid.NewUlid();
    private static readonly Ulid TaskId = Ulid.NewUlid();
    private const string Branch = "orkka/STO-2";
    private const string MissingHead =
        "Branch 'orkka/STO-2' is not in your repository — the agent may not have pushed its work.";

    [Fact]
    public async Task ABranchMissingFromTheFolder_IsPushedFromTheWorktree_AndTheMergeIsRetried()
    {
        var h = new Harness();
        h.MergeFailsThenSucceeds(MissingHead);
        h.PushSucceeds();

        await h.Processor.MergeUnmergedPullRequestsAsync(CancellationToken.None);

        h.Git.Verify(g => g.EnsurePushedAsync(OrgId, RepoId, TaskId, Branch, It.IsAny<CancellationToken>()), Times.Once);
        h.GitHub.Verify(g => g.MergeAsync(It.IsAny<Ulid>(), 1, It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        h.TasksApi.Verify(a => a.MarkTaskMergedAsync(TaskId, "merge-sha", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WhenTheWorktreeIsGoneToo_TheMergeIsNotRetried_AndTheBlockIsRecorded()
    {
        var h = new Harness();
        h.MergeFailsThenSucceeds(MissingHead);
        h.PushFails("Worktree does not exist");

        await h.Processor.MergeUnmergedPullRequestsAsync(CancellationToken.None);

        h.GitHub.Verify(g => g.MergeAsync(It.IsAny<Ulid>(), 1, It.IsAny<CancellationToken>()),
            Times.Once);
        h.TasksApi.Verify(a => a.MarkTaskMergedAsync(It.IsAny<Ulid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        h.TasksApi.Verify(a => a.SetTaskMergeFailureAsync(TaskId, It.Is<string>(s => s.Contains("not in your repository")),
            MergeFailureKind.EnvironmentBlocked, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AMissingBaseBranch_IsTheUsersToFix_NothingIsPushed()
    {
        var h = new Harness();
        h.MergeFailsThenSucceeds("Branch 'main' is not in your repository.");

        await h.Processor.MergeUnmergedPullRequestsAsync(CancellationToken.None);

        h.Git.Verify(g => g.EnsurePushedAsync(It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
        h.TasksApi.Verify(a => a.MarkTaskMergedAsync(It.IsAny<Ulid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>A task a failed merge sent back to Developing is still a candidate (it was
    /// Completed once), but the sweep leaves it alone until that lap brings it back to Done —
    /// re-flagging the same conflict every cycle while the agent is mid-fix helps nobody.</summary>
    [Fact]
    public async Task ATaskSentBackToDeveloping_IsLeftAloneUntilItReturnsToDone()
    {
        var h = new Harness(currentLane: Lane.Developing, status: TaskStatus.InQueue);
        h.MergeFailsThenSucceeds(MissingHead);

        await h.Processor.MergeUnmergedPullRequestsAsync(CancellationToken.None);

        h.GitHub.Verify(g => g.GetAsync(It.IsAny<Ulid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        h.GitHub.Verify(g => g.MergeAsync(It.IsAny<Ulid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>A conflicted PR starts ONE automatic resolution: the sweep records the attempt
    /// and fires the resolver. On the cycles that follow, while that resolver (or a dev pass
    /// reworking the collision) is still running on this replica, the sweep must not touch the
    /// task — not re-notify, not re-fire, and above all not hand it back to Developing on top of
    /// the run that is already in the worktree (prod, 2026-09-15).</summary>
    [Fact]
    public async Task AConflictedPr_StartsOneResolution_AndIsLeftAloneWhileThatRunIsInFlight()
    {
        var h = new Harness(mergeable: false);
        h.MergeFailsThenSucceeds("Merge conflict between 'orkka/STO-2' and 'main'");
        h.TasksApi.Setup(a => a.TryAcquireTaskLeaseAsync(TaskId, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await h.Processor.MergeUnmergedPullRequestsAsync(CancellationToken.None);

        h.TasksApi.Verify(a => a.SetTaskMergeFailureAsync(TaskId, It.Is<string>(s => s.Contains("automatic resolution")),
            It.IsAny<MergeFailureKind>(), It.IsAny<CancellationToken>()), Times.Once);

        // The resolver is now in flight (the sweep put the task in its running set). Next cycle:
        await h.Processor.MergeUnmergedPullRequestsAsync(CancellationToken.None);

        h.TasksApi.Verify(a => a.SetTaskMergeFailureAsync(TaskId, It.IsAny<string>(),
            It.IsAny<MergeFailureKind>(), It.IsAny<CancellationToken>()), Times.Once);
        h.TasksApi.Verify(a => a.RequeueForBaseSyncAsync(It.IsAny<Ulid>(), It.IsAny<int>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>The last member of a multi-repository Story: its own pull request merges cleanly,
    /// but the story's branch conflicts with the default branch of ANOTHER of its repositories.
    /// That is still a conflict for the resolver (which works through the other repository) —
    /// not a "mergeable but blocked" forge rejection retried forever.</summary>
    [Fact]
    public async Task AStoryConflictInAnotherRepository_StartsTheResolver_EvenThoughThisPrIsMergeable()
    {
        var h = new Harness(mergeable: true);
        h.MergeFailsThenSucceeds(StoryLandingErrors.Conflict(Branch, [("web", "develop")]));
        h.TasksApi.Setup(a => a.TryAcquireTaskLeaseAsync(TaskId, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await h.Processor.MergeUnmergedPullRequestsAsync(CancellationToken.None);

        h.TasksApi.Verify(a => a.SetTaskMergeFailureAsync(TaskId, It.Is<string>(s => s.Contains("automatic resolution")),
            It.IsAny<MergeFailureKind>(), It.IsAny<CancellationToken>()), Times.Once);
        h.TasksApi.Verify(a => a.SetTaskMergeFailureAsync(TaskId, It.IsAny<string>(),
            MergeFailureKind.ForgeRejected, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ATaskWithARunInFlightHere_IsNotTouchedByTheSweepAtAll()
    {
        var h = new Harness(mergeable: false);
        h.Processor.MarkRunningForTests(TaskId);

        await h.Processor.MergeUnmergedPullRequestsAsync(CancellationToken.None);

        h.TasksApi.Verify(a => a.SetTaskMergeFailureAsync(It.IsAny<Ulid>(), It.IsAny<string>(),
            It.IsAny<MergeFailureKind>(), It.IsAny<CancellationToken>()), Times.Never);
        h.TasksApi.Verify(a => a.RequeueForBaseSyncAsync(It.IsAny<Ulid>(), It.IsAny<int>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>A task parked on the merge page (demoted to CodeReview/WaitingCodeReview by a
    /// merge-failure notice) is NOT a rework pass: the sweep keeps looking at it. And when its
    /// conflict notice is stale — the resolver pushed a fix and the PR merges cleanly again —
    /// the sweep clears the notice, so the task returns to Done and its group's gate stops
    /// counting it as stuck (prod, 2026-09-16: #18 held by "#18 (CodeReview)").</summary>
    [Fact]
    public async Task AParkedTaskWhosePrMergesAgain_HasItsStaleConflictNoticeCleared_AndIsMerged()
    {
        var h = new Harness(currentLane: Lane.CodeReview, mergeable: true,
            status: TaskStatus.WaitingCodeReview, hasMergeFailure: true);
        h.MergeFailsThenSucceeds("unused");
        h.GitHub.Setup(g => g.MergeAsync(It.IsAny<Ulid>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<GitHubPullRequest>.Success(new GitHubPullRequest(1, 1, "url", "closed", "merge-sha")));
        h.TasksApi.Setup(a => a.ClearTaskMergeFailureAsync(TaskId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await h.Processor.MergeUnmergedPullRequestsAsync(CancellationToken.None);

        h.TasksApi.Verify(a => a.ClearTaskMergeFailureAsync(TaskId, It.IsAny<CancellationToken>()), Times.Once);
        h.TasksApi.Verify(a => a.MarkTaskMergedAsync(TaskId, "merge-sha", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>The task's own run owns its merge. While the conflict resolver is in flight on
    /// this replica — it has pushed the resolved branch and is about to merge it — the sweep
    /// must not land the PR it now finds mergeable. It did (prod, 2026-09-16, StoreFront #19),
    /// and the resolver's own push was then refused NonFastForward and wrote "Merge failed"
    /// onto a task that had just merged.</summary>
    [Fact]
    public async Task AMergeablePr_IsLeftToItsOwnRun_WhileThatRunIsInFlight()
    {
        var h = new Harness(mergeable: true);
        h.Processor.MarkRunningForTests(TaskId);

        await h.Processor.MergeUnmergedPullRequestsAsync(CancellationToken.None);

        h.GitHub.Verify(g => g.MergeAsync(It.IsAny<Ulid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        h.TasksApi.Verify(a => a.MarkTaskMergedAsync(It.IsAny<Ulid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>…and its repository with it, for the cycle: a sibling landing in the same
    /// folder at that moment races the resolver's push, and one of the two is refused. The
    /// cycle after the run ends, merging resumes.</summary>
    [Fact]
    public async Task ASiblingInTheSameRepository_WaitsACycle_WhileTheResolverIsInFlight()
    {
        var h = new Harness(mergeable: true);
        h.AddMergeableSibling(Ulid.NewUlid());
        h.Processor.MarkRunningForTests(TaskId);

        await h.Processor.MergeUnmergedPullRequestsAsync(CancellationToken.None);

        h.GitHub.Verify(g => g.MergeAsync(It.IsAny<Ulid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);

        h.Processor.UnmarkRunningForTests(TaskId);
        h.GitHub.Setup(g => g.MergeAsync(It.IsAny<Ulid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<GitHubPullRequest>.Success(new GitHubPullRequest(1, 1, "url", "closed", "merge-sha")));

        await h.Processor.MergeUnmergedPullRequestsAsync(CancellationToken.None);

        h.GitHub.Verify(g => g.MergeAsync(It.IsAny<Ulid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once, "one merge per repo per cycle");
    }

    /// <summary>A gated pair lands all-or-nothing within the cycle too. The gate reads the
    /// siblings' state and cannot see that the provider half is failing to land right now on
    /// the retry path — a dirty working copy in the user's API folder (prod, 2026-09-16,
    /// StoreFront #15) — so the UI half went onto main alone. The caller waits for the cycle
    /// in which the provider actually lands.</summary>
    [Fact]
    public async Task AGatedCaller_IsHeld_WhenTheProvidersMergeFailedThisCycle()
    {
        var h = new Harness(mergeable: true);
        var ui = h.AddGatedUiHalf();
        h.GitHub.Setup(g => g.MergeAsync(It.IsAny<Ulid>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<GitHubPullRequest>.Failure(
                "Your repository has uncommitted changes on 'main'. Commit or stash them, then merge again."));
        h.GitHub.Setup(g => g.MergeAsync(It.IsAny<Ulid>(), 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<GitHubPullRequest>.Success(new GitHubPullRequest(2, 2, "url", "closed", "ui-sha")));

        await h.Processor.MergeUnmergedPullRequestsAsync(CancellationToken.None);

        h.GitHub.Verify(g => g.MergeAsync(It.IsAny<Ulid>(), 1, It.IsAny<CancellationToken>()), Times.Once);
        h.GitHub.Verify(g => g.MergeAsync(It.IsAny<Ulid>(), 2, It.IsAny<CancellationToken>()), Times.Never, "the caller waits for the provider");
        h.TasksApi.Verify(a => a.MarkTaskMergedAsync(ui, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AGatedCaller_Lands_RightAfterItsProviderDoes()
    {
        var h = new Harness(mergeable: true);
        var ui = h.AddGatedUiHalf();
        h.GitHub.Setup(g => g.MergeAsync(It.IsAny<Ulid>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<GitHubPullRequest>.Success(new GitHubPullRequest(1, 1, "url", "closed", "api-sha")));
        h.GitHub.Setup(g => g.MergeAsync(It.IsAny<Ulid>(), 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<GitHubPullRequest>.Success(new GitHubPullRequest(2, 2, "url", "closed", "ui-sha")));

        await h.Processor.MergeUnmergedPullRequestsAsync(CancellationToken.None);

        h.TasksApi.Verify(a => a.MarkTaskMergedAsync(TaskId, "api-sha", It.IsAny<CancellationToken>()), Times.Once);
        h.TasksApi.Verify(a => a.MarkTaskMergedAsync(ui, "ui-sha", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>The runner dropped while the resolver was working (prod, 2026-09-17). The
    /// resolver records EnvironmentBlocked; when the runner is back and the PR still conflicts,
    /// the resolver runs again — the drop did not spend its one automatic attempt.</summary>
    [Fact]
    public async Task AResolverCutOffByTheRunner_GetsItsAttemptBack()
    {
        var h = new Harness(mergeable: false);
        h.MergeAlwaysFails("Merge conflict between 'orkka/STO-2' and 'main'");
        h.TasksApi.Setup(a => a.TryAcquireTaskLeaseAsync(TaskId, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await h.Processor.MergeUnmergedPullRequestsAsync(CancellationToken.None);
        h.Processor.UnmarkRunningForTests(TaskId);

        // The resolver's run ended on "Your runner disconnected mid-run".
        h.ListAs(hasMergeFailure: true, mergeFailureKind: MergeFailureKind.EnvironmentBlocked);
        await h.Processor.MergeUnmergedPullRequestsAsync(CancellationToken.None);

        h.TasksApi.Verify(a => a.SetTaskMergeFailureAsync(TaskId, It.Is<string>(s => s.Contains("automatic resolution")),
            It.IsAny<MergeFailureKind>(), It.IsAny<CancellationToken>()), Times.Exactly(2),
            "the resolver starts again");
        h.TasksApi.Verify(a => a.RequeueForBaseSyncAsync(It.IsAny<Ulid>(), It.IsAny<int>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never, "no dev pass for a collision the resolver never got to look at");
    }

    /// <summary>The contrast: a resolver that RAN and could not land the merge spent its
    /// attempt, and the conflict goes to a dev pass as before.</summary>
    [Fact]
    public async Task AResolverThatRanAndFailed_HandsTheConflictToADevPass()
    {
        var h = new Harness(mergeable: false);
        h.MergeAlwaysFails("Merge conflict between 'orkka/STO-2' and 'main'");
        h.TasksApi.Setup(a => a.TryAcquireTaskLeaseAsync(TaskId, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await h.Processor.MergeUnmergedPullRequestsAsync(CancellationToken.None);
        h.Processor.UnmarkRunningForTests(TaskId);

        h.ListAs(hasMergeFailure: true, mergeFailureKind: MergeFailureKind.Unknown);
        await h.Processor.MergeUnmergedPullRequestsAsync(CancellationToken.None);

        h.TasksApi.Verify(a => a.SetTaskMergeFailureAsync(TaskId, It.Is<string>(s => s.Contains("automatic resolution")),
            It.IsAny<MergeFailureKind>(), It.IsAny<CancellationToken>()), Times.Once);
        h.TasksApi.Verify(a => a.RequeueForBaseSyncAsync(TaskId, It.IsAny<int>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Mergeability unknown (an older runner without the dry-run), and the merge
    /// itself reports a conflict: that is confirmation, and the conflict goes to the resolver
    /// rather than being retried every cycle.</summary>
    [Fact]
    public async Task UnknownMergeability_AndAConflictFromTheMergeItself_StartsResolution()
    {
        var h = new Harness(mergeable: null);
        h.MergeFailsThenSucceeds("Merge conflict between 'orkka/STO-2' and 'main': CONFLICT (content)");
        h.TasksApi.Setup(a => a.TryAcquireTaskLeaseAsync(TaskId, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await h.Processor.MergeUnmergedPullRequestsAsync(CancellationToken.None);

        h.TasksApi.Verify(a => a.SetTaskMergeFailureAsync(TaskId, It.Is<string>(s => s.Contains("automatic resolution")),
            It.IsAny<MergeFailureKind>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Unknown mergeability never clears a conflict notice — only a clean dry-run does.
    /// Prod, 2026-09-17: with the runner offline the PR read "mergeable" and a real conflict
    /// notice was cleared as stale.</summary>
    [Fact]
    public async Task UnknownMergeability_NeverClearsAConflictNotice()
    {
        var h = new Harness(currentLane: Lane.CodeReview, mergeable: null,
            status: TaskStatus.WaitingCodeReview, hasMergeFailure: true);
        h.MergeFailsThenSucceeds("This repository is a folder on your machine, and your runner is not connected.");

        await h.Processor.MergeUnmergedPullRequestsAsync(CancellationToken.None);

        h.TasksApi.Verify(a => a.ClearTaskMergeFailureAsync(It.IsAny<Ulid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>A member held by its group's gate takes nothing from its repository: a ready
    /// task in the SAME repository, sorted behind it, still merges this cycle. Prod, 2026-09-17:
    /// a held API half claimed storefront-api's one merge every cycle, a ready pair's API half
    /// behind it never merged, and three tasks depending on that merge never started — one of
    /// them the held half's own partner.</summary>
    [Fact]
    public async Task AHeldGroupMember_DoesNotTakeItsRepositorysMerge()
    {
        var h = new Harness(mergeable: true);
        var ready = Ulid.NewUlid();
        var heldGroup = Ulid.NewUlid();
        h.TasksApi.Setup(a => a.ListTasksAwaitingMergeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                // Completed first, grouped, and its partner has not started: held.
                new MergeCandidateTask(TaskId, OrgId, ProjectId, RepoId, "orkka-local://pull/1", GroupId: heldGroup),
                // Same repository, ready to land.
                new MergeCandidateTask(ready, OrgId, ProjectId, RepoId, "orkka-local://pull/2"),
            ]);
        h.TasksApi.Setup(a => a.GetGroupMergeStatesAsync(It.IsAny<IReadOnlyCollection<Ulid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TaskGroupMergeState(heldGroup, TaskGroupMergePolicy.Gated, false, [TaskId], "Waiting on #35 (Ready)")]);
        h.GitHub.Setup(g => g.GetAsync(It.IsAny<Ulid>(), 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<GitHubPullRequestDetail>.Success(new GitHubPullRequestDetail(
                2, 2, "orkka-local://pull/2", "open", "STO-28", null, true, false,
                0, 0, 0, "head-2", "main", "orkka/STO-28")));
        h.GitHub.Setup(g => g.MergeAsync(It.IsAny<Ulid>(), 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<GitHubPullRequest>.Success(new GitHubPullRequest(2, 2, "url", "closed", "sha-28")));

        await h.Processor.MergeUnmergedPullRequestsAsync(CancellationToken.None);

        h.GitHub.Verify(g => g.MergeAsync(It.IsAny<Ulid>(), 1, It.IsAny<CancellationToken>()), Times.Never, "the held member lands nothing");
        h.TasksApi.Verify(a => a.MarkTaskMergedAsync(ready, "sha-28", It.IsAny<CancellationToken>()), Times.Once,
            "the ready task behind it in the same repository is not blocked by it");
    }

    /// <summary>A conflict the resolver fixed is over; when the base moves again the new
    /// conflict gets its own automatic resolution instead of going straight to a dev pass.
    /// Prod, 2026-09-17: StoreFront #30 resolved at 06:00, held for its partner, and a new
    /// conflict at 07:31 was handed to Developing because the fixed one had spent the attempt.</summary>
    [Fact]
    public async Task AConflictResolvedEarlier_DoesNotSpendTheAttemptForTheNextOne()
    {
        var h = new Harness();
        h.MergeAlwaysFails("Merge conflict between 'orkka/STO-2' and 'main'");
        h.TasksApi.Setup(a => a.TryAcquireTaskLeaseAsync(TaskId, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        h.GitHub.SetupSequence(g => g.GetAsync(It.IsAny<Ulid>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrReading(mergeable: false))   // conflict → the resolver runs
            .ReturnsAsync(PrReading(mergeable: true))    // resolved and pushed; merge still refused for another reason
            .ReturnsAsync(PrReading(mergeable: false));  // the base moved again: a new conflict

        await h.Processor.MergeUnmergedPullRequestsAsync(CancellationToken.None);
        h.Processor.UnmarkRunningForTests(TaskId);
        h.GitHub.Setup(g => g.MergeAsync(It.IsAny<Ulid>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<GitHubPullRequest>.Failure("Your repository has uncommitted changes on 'main'."));
        await h.Processor.MergeUnmergedPullRequestsAsync(CancellationToken.None);
        h.MergeAlwaysFails("Merge conflict between 'orkka/STO-2' and 'main'");
        await h.Processor.MergeUnmergedPullRequestsAsync(CancellationToken.None);

        h.TasksApi.Verify(a => a.SetTaskMergeFailureAsync(TaskId, It.Is<string>(s => s.Contains("automatic resolution")),
            It.IsAny<MergeFailureKind>(), It.IsAny<CancellationToken>()), Times.Exactly(2),
            "the second conflict gets its own resolver run");
        h.TasksApi.Verify(a => a.RequeueForBaseSyncAsync(It.IsAny<Ulid>(), It.IsAny<int>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private static Result<GitHubPullRequestDetail> PrReading(bool? mergeable)
        => Result<GitHubPullRequestDetail>.Success(new GitHubPullRequestDetail(
            1, 1, "orkka-local://pull/1", "open", "STO-2", null, mergeable, false,
            0, 0, 0, "head-sha", "main", Branch));

    [Fact]
    public void TheClassifier_OnlyMatchesTheRunnersMissingHeadWording()
    {
        MergeBlockedReasons.IsMissingHeadBranch(MissingHead).Should().BeTrue();
        MergeBlockedReasons.IsMissingHeadBranch("Branch 'main' is not in your repository.").Should().BeFalse();
        MergeBlockedReasons.IsMissingHeadBranch("Your repository has uncommitted changes on 'main'.").Should().BeFalse();
        MergeBlockedReasons.IsMissingHeadBranch(null).Should().BeFalse();
    }

    private sealed class Harness
    {
        public readonly Mock<ITasksApi> TasksApi = new(MockBehavior.Loose);
        public readonly Mock<IProjectsApi> ProjectsApi = new(MockBehavior.Loose);
        public readonly Mock<IPullRequestService> GitHub = new(MockBehavior.Loose);
        public readonly Mock<IGitWorkspaceManager> Git = new(MockBehavior.Loose);
        public readonly TaskQueueProcessor Processor;

        public Harness(
            Lane currentLane = Lane.Done, bool? mergeable = null,
            TaskStatus status = TaskStatus.Completed, bool hasMergeFailure = false,
            MergeFailureKind? mergeFailureKind = null)
        {
            ListAs(currentLane, status, hasMergeFailure, mergeFailureKind);
            TasksApi.Setup(a => a.GetGroupMergeStatesAsync(It.IsAny<IReadOnlyCollection<Ulid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            TasksApi.Setup(a => a.GetTaskSnapshotAsync(TaskId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Snapshot());

            // A bring-your-own folder: the repo's "URL" is a path on the user's PC.
            ProjectsApi.Setup(p => p.GetRepoAsync(RepoId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RepoSummary(
                    RepoId, ProjectId, OrgId, "storefront-api", @"C:\Code\storefront\storefront-api", "main", "#fff",
                    WorkspaceStatus.Ready));

            GitHub.Setup(g => g.GetAsync(It.IsAny<Ulid>(), 1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result<GitHubPullRequestDetail>.Success(new GitHubPullRequestDetail(
                    1, 1, "orkka-local://pull/1", "open", "STO-2", null, mergeable, false,
                    0, 0, 0, "head-sha", "main", Branch)));

            var route = new ExecutionRoute(
                Mock.Of<IAiAgentInvoker>(), Git.Object, Mock.Of<IProviderReadiness>(),
                Repo: Mock.Of<IGitRepoReader>());
            var router = new Mock<IExecutionRouter>();
            router.Setup(r => r.Resolve(It.IsAny<TaskSnapshot>())).Returns(route);

            var services = new ServiceCollection();
            services.AddSingleton(TasksApi.Object);
            services.AddSingleton(ProjectsApi.Object);
            services.AddSingleton(GitHub.Object);
            services.AddSingleton(Mock.Of<ITaskNotifier>());
            services.AddSingleton(Mock.Of<ITaskOrchestrator>());
            var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

            Processor = new TaskQueueProcessor(
                scopeFactory, Mock.Of<ITaskOrchestrator>(), Mock.Of<IDocumentationSyncService>(),
                router.Object,
                Mock.Of<ITaskNotifier>(), Mock.Of<IAgentAuditLog>(),
                Mock.Of<Orkka.Modules.Runners.Contracts.IRunnerClaimAffinity>(),
                new WorkerDrain(), new ConfigurationBuilder().Build(),
                Options.Create(new DocumentationSyncOptions()),
                NullLogger<TaskQueueProcessor>.Instance);
        }

        /// <summary>What the next sweep reads about the harness task.</summary>
        public void ListAs(
            Lane currentLane = Lane.Done, TaskStatus status = TaskStatus.Completed,
            bool hasMergeFailure = false, MergeFailureKind? mergeFailureKind = null)
            => TasksApi.Setup(a => a.ListTasksAwaitingMergeAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([new MergeCandidateTask(TaskId, OrgId, ProjectId, RepoId, "orkka-local://pull/1",
                    CurrentLane: currentLane, Status: status, HasMergeFailure: hasMergeFailure,
                    MergeFailureKind: mergeFailureKind)]);

        /// <summary>The UI half of a Gated cross-repo pair whose provider is the harness task:
        /// a second local-folder repository, PR #2 mergeable, the group ready with the API
        /// half first in the merge order. Returns the UI task's id.</summary>
        public Ulid AddGatedUiHalf()
        {
            var uiTask = Ulid.NewUlid();
            var uiRepo = Ulid.NewUlid();
            var group = Ulid.NewUlid();
            TasksApi.Setup(a => a.ListTasksAwaitingMergeAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new MergeCandidateTask(TaskId, OrgId, ProjectId, RepoId, "orkka-local://pull/1", GroupId: group),
                    new MergeCandidateTask(uiTask, OrgId, ProjectId, uiRepo, "orkka-local://pull/2", GroupId: group),
                ]);
            TasksApi.Setup(a => a.GetGroupMergeStatesAsync(It.IsAny<IReadOnlyCollection<Ulid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([new TaskGroupMergeState(group, TaskGroupMergePolicy.Gated, true, [TaskId, uiTask], null)]);
            ProjectsApi.Setup(p => p.GetRepoAsync(uiRepo, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RepoSummary(
                    uiRepo, ProjectId, OrgId, "storefront-ui", @"C:\Code\storefront\storefront-ui", "main", "#fff",
                    WorkspaceStatus.Ready));
            GitHub.Setup(g => g.GetAsync(It.IsAny<Ulid>(), 2, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result<GitHubPullRequestDetail>.Success(new GitHubPullRequestDetail(
                    2, 2, "orkka-local://pull/2", "open", "STO-21", null, true, false,
                    0, 0, 0, "ui-head", "main", "orkka/STO-21")));
            return uiTask;
        }

        /// <summary>A second finished task in the SAME repository, its PR mergeable.</summary>
        public void AddMergeableSibling(Ulid siblingId)
        {
            TasksApi.Setup(a => a.ListTasksAwaitingMergeAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new MergeCandidateTask(TaskId, OrgId, ProjectId, RepoId, "orkka-local://pull/1"),
                    new MergeCandidateTask(siblingId, OrgId, ProjectId, RepoId, "orkka-local://pull/2"),
                ]);
            GitHub.Setup(g => g.GetAsync(It.IsAny<Ulid>(), 2, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result<GitHubPullRequestDetail>.Success(new GitHubPullRequestDetail(
                    2, 2, "orkka-local://pull/2", "open", "STO-3", null, true, false,
                    0, 0, 0, "head-sha-2", "main", "orkka/STO-3")));
        }

        public void MergeAlwaysFails(string error) =>
            GitHub.Setup(g => g.MergeAsync(It.IsAny<Ulid>(), 1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result<GitHubPullRequest>.Failure(error));

        public void MergeFailsThenSucceeds(string firstError) =>
            GitHub.SetupSequence(g => g.MergeAsync(It.IsAny<Ulid>(), 1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result<GitHubPullRequest>.Failure(firstError))
                .ReturnsAsync(Result<GitHubPullRequest>.Success(new GitHubPullRequest(1, 1, "url", "closed", "merge-sha")));

        public void PushSucceeds() =>
            Git.Setup(g => g.EnsurePushedAsync(OrgId, RepoId, TaskId, Branch, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Success());

        public void PushFails(string error) =>
            Git.Setup(g => g.EnsurePushedAsync(OrgId, RepoId, TaskId, Branch, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Failure(error));

        private static TaskSnapshot Snapshot() => new(
            Id: TaskId, OrgId: OrgId, ProjectId: ProjectId, RepoId: RepoId,
            Number: 2, Title: "STO-2", Description: "",
            Priority: Priority.Medium, Type: TaskType.Feature,
            CurrentLane: Lane.Done, Status: TaskStatus.Completed, IsArchived: false,
            RequesterLocale: null, TotalTokensUsed: 0, HasAgentWorking: false,
            BranchName: Branch, PrUrl: "orkka-local://pull/1", Tags: null,
            ContextFiles: null, ContextDocumentIds: null,
            SkipPlanning: false, SkipTesting: false, SkipCodeReview: false, IsMerged: false,
            ReviewCycleCount: 0, QaCycleCount: 0, QuestionSourceLane: null,
            AssignedAgentId: null, CurrentAgentId: null, SourceMessageId: null,
            StartedAt: null, CompletedAt: null);
    }
}
