namespace Orkka.Tests.Features.CodeChanges;

using FluentAssertions;
using Moq;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.Modules.Tasks.Features.CodeChanges;
using Orkka.SharedKernel.Utilities;

/// <summary>
/// A Story member's finished "Code changes" diff is read from its review base, so the second
/// sub-task does not show the first one's work again.
/// </summary>
public sealed class SnapshotDiffStoryBaseTests
{
    private readonly Ulid _org = Ulid.NewUlid();
    private readonly Ulid _repo = Ulid.NewUlid();
    private readonly Ulid _task = Ulid.NewUlid();

    [Fact]
    public async Task A_story_members_finished_diff_starts_at_its_review_base()
    {
        var git = new Mock<ILocalRepoGitOps>();
        git.Setup(g => g.GetDiffAsync(_org, _repo, _task, "local_user", "main", "feature/SHOP-20", "6a68241", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<string>.Success("only neg()"));

        var diff = await GetCodeChangeSnapshotDiffEndpoint.ReadDiffAsync(git.Object, _org, _repo, _task, "local_user",
            isLive: false, "main", "feature/SHOP-20", reviewBaseSha: "6a68241", CancellationToken.None);

        diff.Value.Should().Be("only neg()");
    }

    [Fact]
    public async Task Any_other_tasks_finished_diff_is_the_branch_against_its_base()
    {
        var git = new Mock<ILocalRepoGitOps>();
        git.Setup(g => g.GetDiffAsync(_org, _repo, _task, "local_user", "main", "feature/SHOP-11", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<string>.Success("whole branch"));

        var diff = await GetCodeChangeSnapshotDiffEndpoint.ReadDiffAsync(git.Object, _org, _repo, _task, "local_user",
            isLive: false, "main", "feature/SHOP-11", reviewBaseSha: null, CancellationToken.None);

        diff.Value.Should().Be("whole branch");
    }
}
