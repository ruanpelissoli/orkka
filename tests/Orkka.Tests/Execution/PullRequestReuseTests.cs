namespace Orkka.Tests.Execution;

using Orkka.Modules.Execution.Orchestration;
using Orkka.Modules.SourceControl.Contracts;
using FluentAssertions;

/// <summary>
/// A task re-run after its PR merged (or closed) must get a fresh PR for the new commits —
/// the old one can never move again (Approve/Merge hide on a merged PR), which is exactly
/// what stranded FAN-9 on the review page. Only an open PR is reusable.
/// </summary>
public sealed class PullRequestReuseTests
{
    private static GitHubPullRequestDetail Pr(string state, bool merged = false) =>
        new(1, 2, "https://github.com/o/r/pull/2", state, "t", null, true, false, 1, 1, 1,
            "abc", "main", "orkka/FAN-9", Merged: merged);

    [Fact]
    public void OpenPullRequest_IsReused()
        => TaskOrchestrator.PullRequestIsDone(Pr("open")).Should().BeFalse();

    [Fact]
    public void MergedPullRequest_NeedsAFreshOne()
    {
        // GitHub reports a merged PR as state "closed" + merged=true; "merged" from other
        // forges/local PRs must be treated the same way.
        TaskOrchestrator.PullRequestIsDone(Pr("closed", merged: true)).Should().BeTrue();
        TaskOrchestrator.PullRequestIsDone(Pr("merged")).Should().BeTrue();
    }

    [Fact]
    public void ClosedWithoutMerging_NeedsAFreshOne()
        => TaskOrchestrator.PullRequestIsDone(Pr("closed")).Should().BeTrue();

    [Fact]
    public void StateComparison_IsCaseInsensitive()
        => TaskOrchestrator.PullRequestIsDone(Pr("OPEN")).Should().BeFalse();
}
