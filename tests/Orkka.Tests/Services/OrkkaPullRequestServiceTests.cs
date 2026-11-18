namespace Orkka.Tests.Services;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.Modules.SourceControl.Infrastructure;
using Orkka.Modules.SourceControl.Infrastructure.Services;
using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Utilities;

/// <summary>
/// Orkka's own pull requests, in a hosted process: a task's branch gets a pull request, the
/// review lane can read its diff, and "merge" either runs git in the developer's folder or —
/// for a remote repository — publishes the work as a pull request their runner opens on the
/// forge with their own CLI.
/// </summary>
public sealed class OrkkaPullRequestServiceTests : IDisposable
{
    private readonly SourceControlDbContext _db;
    private readonly FakeLocalRepoGitOps _gitOps = new();
    private readonly OrkkaPullRequestService _service;

    private static readonly Ulid OrgId = Ulid.NewUlid();
    private static readonly Ulid RepoId = Ulid.NewUlid();
    private static readonly Ulid RemoteRepoId = Ulid.NewUlid();
    private static readonly Ulid TaskId = Ulid.NewUlid();
    private const string Head = "orkka/task-1";
    private const string Base = "main";


    public OrkkaPullRequestServiceTests()
    {
        _db = new SourceControlDbContext(
            new DbContextOptionsBuilder<SourceControlDbContext>()
                .UseInMemoryDatabase($"localpr-{Ulid.NewUlid()}")
                .Options);

        var projects = new Mock<IProjectsApi>();
        projects.Setup(p => p.GetRepoAsync(RepoId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepoSummary(
                RepoId, Ulid.NewUlid(), OrgId, "accounting-app",
                @"C:\Users\him\accounting-app", Base, "#fff",
                WorkspaceStatus.Ready));
        projects.Setup(p => p.GetRepoAsync(RemoteRepoId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepoSummary(
                RemoteRepoId, Ulid.NewUlid(), OrgId, "storefront",
                "git@github-work:acme/storefront.git", Base, "#fff",
                WorkspaceStatus.Ready, ForgeKind: ForgeKind.GitHub, CliAccount: "dana-acme"));

        var tasks = new Mock<ITasksApi>();
        tasks.Setup(t => t.FindTaskIdByBranchAsync(
                Head, It.IsAny<IReadOnlyCollection<Ulid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TaskId);

        _service = new OrkkaPullRequestService(
            _db, projects.Object,
            new ServiceCollection().AddSingleton(tasks.Object).BuildServiceProvider(), _gitOps,
            Mock.Of<ILogger<OrkkaPullRequestService>>());
    }

    public void Dispose() => _db.Dispose();

    private Task<Result<GitHubPullRequest>> CreateAsync() =>
        _service.CreateAsync(
            RepoId, Head, Base, "Add invoice export", "body", default);

    [Fact]
    public async Task Create_MintsAPullRequestForTheBranch()
    {
        var result = await CreateAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value!.Number.Should().Be(1);

        var row = await _db.LocalPullRequests.IgnoreQueryFilters().SingleAsync();
        row.OrgId.Should().Be(OrgId, "the org comes from the repository, never from the caller");
        row.TaskId.Should().Be(TaskId);
        row.HeadBranch.Should().Be(Head);
    }

    [Fact]
    public async Task Create_IsIdempotent_SoPhaseReEntryDoesNotMintASecond()
    {
        var first = await CreateAsync();
        var second = await CreateAsync();

        second.Value!.Number.Should().Be(first.Value!.Number);
        (await _db.LocalPullRequests.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Merge_RunsGitInTheUsersRepositoryAndClosesThePullRequest()
    {
        var pr = await CreateAsync();

        var merged = await _service.MergeAsync(RepoId, pr.Value!.Number);

        merged.IsSuccess.Should().BeTrue();
        _gitOps.Merges.Should().ContainSingle();
        _gitOps.Merges[0].Should().Be((OrgId, RepoId, Head, Base));

        var row = await _db.LocalPullRequests.IgnoreQueryFilters().SingleAsync();
        row.Merged.Should().BeTrue();
        row.MergedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Merge_WhenTheMachineRefuses_LeavesThePullRequestOpen()
    {
        // The runner's preconditions (uncommitted changes, wrong branch) surface here as the
        // user-facing reason. Recording a merge that did not happen would strand the task.
        _gitOps.MergeError = "Your repository has uncommitted changes.";
        var pr = await CreateAsync();

        var merged = await _service.MergeAsync(RepoId, pr.Value!.Number);

        merged.IsFailure.Should().BeTrue();
        merged.Error.Should().Contain("uncommitted changes");
        (await _db.LocalPullRequests.IgnoreQueryFilters().SingleAsync()).Merged.Should().BeFalse();
    }

    [Fact]
    public async Task Merge_IsIdempotent()
    {
        var pr = await CreateAsync();
        await _service.MergeAsync(RepoId, pr.Value!.Number);
        var again = await _service.MergeAsync(RepoId, pr.Value!.Number);

        again.IsSuccess.Should().BeTrue();
        _gitOps.Merges.Should().ContainSingle("a merged pull request must not merge twice");
    }

    [Fact]
    public async Task Merge_OfARemoteRepository_PublishesItToTheForge_AndNeverMerges()
    {
        var pr = await _service.CreateAsync(
            RemoteRepoId, Head, Base, "Add invoice export", "body", default);

        var published = await _service.MergeAsync(RemoteRepoId, pr.Value!.Number);

        published.IsSuccess.Should().BeTrue(published.Error);
        published.Value!.ForgeUrl.Should().Be(_gitOps.PublishedUrl);
        published.Value.HtmlUrl.Should().StartWith("orkka-local://", "the number in it is Orkka's own");
        _gitOps.Merges.Should().BeEmpty("Orkka never merges into a developer's remote repository");
        _gitOps.Published.Should().ContainSingle().Which.Should().Be((RemoteRepoId, Head, Base, ForgeKind.GitHub));

        var row = await _db.LocalPullRequests.IgnoreQueryFilters().SingleAsync();
        row.Merged.Should().BeTrue("the work has left Orkka");
        row.ForgeUrl.Should().Be(_gitOps.PublishedUrl);
        row.ForgeState.Should().Be("open");
    }

    [Fact]
    public async Task Publish_WhenTheCliRefuses_LeavesThePullRequestOpen()
    {
        _gitOps.PublishError = "The GitHub CLI is not signed in to github.com — run `gh auth login` on this machine.";
        var pr = await _service.CreateAsync(
            RemoteRepoId, Head, Base, "t", "b", default);

        var published = await _service.MergeAsync(RemoteRepoId, pr.Value!.Number);

        published.IsFailure.Should().BeTrue();
        published.Error.Should().Contain("gh auth login");
        (await _db.LocalPullRequests.IgnoreQueryFilters().SingleAsync()).Merged.Should().BeFalse();
    }

    [Fact]
    public async Task Detail_ReadsTheDiffFromTheUsersMachine()
    {
        _gitOps.Diff = """
            diff --git a/app.js b/app.js
            --- a/app.js
            +++ b/app.js
            @@ -1 +1,2 @@
             const a = 1;
            +const b = 2;
            """;
        var pr = await CreateAsync();

        var detail = await _service.GetAsync(RepoId, pr.Value!.Number);

        detail.IsSuccess.Should().BeTrue();
        detail.Value!.ChangedFiles.Should().Be(1);
        detail.Value.Additions.Should().Be(1);
        detail.Value.State.Should().Be("open");
    }

    [Fact]
    public async Task Detail_WhenTheMachineIsAsleep_RendersAnEmptyDiffRatherThanFailing()
    {
        _gitOps.DiffError = "Runner offline.";
        _gitOps.MergesCleanly = null;
        var pr = await CreateAsync();

        var detail = await _service.GetAsync(RepoId, pr.Value!.Number);

        detail.IsSuccess.Should().BeTrue();
        detail.Value!.ChangedFiles.Should().Be(0);
        detail.Value.Mergeable.Should().BeNull(
            "unknown is neither conflicted nor clean: a clean reading cleared a real conflict "
            + "notice while the runner was offline (prod, 2026-09-17)");
    }

    [Fact]
    public async Task Detail_ReportsAConflictedBranchAsUnmergeable()
    {
        _gitOps.MergesCleanly = false;
        var pr = await CreateAsync();

        var detail = await _service.GetAsync(RepoId, pr.Value!.Number);

        detail.Value!.Mergeable.Should().BeFalse();
    }

    [Fact]
    public async Task Review_IsRecordedAndReadBack()
    {
        var pr = await CreateAsync();

        await _service.CreateReviewAsync(
            RepoId, pr.Value!.Number, "REQUEST_CHANGES", "One fix first",
            [new GitHubNewReviewComment("app.js", 2, "This drops the tax line.")]);

        var reviews = await _service.GetReviewsAsync(RepoId, pr.Value.Number);
        var comments = await _service.GetCommentsAsync(RepoId, pr.Value.Number);

        reviews.Value.Should().ContainSingle().Which.State.Should().Be("CHANGES_REQUESTED");
        comments.Value.Should().ContainSingle().Which.Path.Should().Be("app.js");
    }

    [Fact]
    public async Task PullRequestsFromAnotherRepository_AreNotVisible()
    {
        // Numbers are unique per org, and the repo id is the only tenant evidence some call
        // sites carry — a lookup must not cross it.
        await CreateAsync();

        var found = await _service.GetAsync(Ulid.NewUlid(), 1);

        found.IsFailure.Should().BeTrue();
    }

    /// <summary>Stands in for the user's machine.</summary>
    private sealed class FakeLocalRepoGitOps : ILocalRepoGitOps
    {
        public List<(Ulid OrgId, Ulid RepoId, string Head, string Base)> Merges { get; } = [];
        public string? MergeError { get; set; }
        public string Diff { get; set; } = "";
        public string? DiffError { get; set; }
        public bool? MergesCleanly { get; set; } = true;

        public Task<Result> SquashMergeAsync(
            Ulid orgId, Ulid repoId, string? runnerUserId,
            string headBranch, string baseBranch, string commitMessage, CancellationToken ct = default)
        {
            if (MergeError is not null)
                return Task.FromResult(Result.Failure(MergeError));

            Merges.Add((orgId, repoId, headBranch, baseBranch));
            return Task.FromResult(Result.Success());
        }

        public Task<Result<string>> GetDiffAsync(
            Ulid orgId, Ulid repoId, Ulid taskId, string? runnerUserId,
            string baseBranch, string headBranch, CancellationToken ct = default)
            => Task.FromResult(DiffError is not null
                ? Result<string>.Failure(DiffError)
                : Result<string>.Success(Diff));

        public Task<Result<string>> GetWorkingTreeDiffAsync(
            Ulid orgId, Ulid repoId, Ulid taskId, string? runnerUserId,
            string baseBranch, CancellationToken ct = default)
            => GetDiffAsync(orgId, repoId, taskId, runnerUserId, baseBranch, "", ct);

        public Task<bool?> WouldMergeCleanlyAsync(
            Ulid orgId, Ulid repoId, string? runnerUserId,
            string headBranch, string baseBranch, CancellationToken ct = default)
            => Task.FromResult(MergesCleanly);

        public List<(Ulid RepoId, string Head, string Base, ForgeKind Forge)> Published { get; } = [];
        public string PublishedUrl { get; set; } = "https://github.com/acme/app/pull/1";
        public string? PublishError { get; set; }
        public string? ForgeState { get; set; } = "open";

        public Task<Result<string>> OpenPullRequestAsync(
            Ulid orgId, Ulid repoId, string? runnerUserId,
            string headBranch, string baseBranch, string title, string body,
            ForgeKind forge, string? cliAccount, CancellationToken ct = default)
        {
            if (PublishError is not null)
                return Task.FromResult(Result<string>.Failure(PublishError));

            Published.Add((repoId, headBranch, baseBranch, forge));
            return Task.FromResult(Result<string>.Success(PublishedUrl));
        }

        public Task<string?> GetPullRequestStateAsync(
            Ulid orgId, Ulid repoId, string? runnerUserId,
            string pullRequestUrl, ForgeKind forge, string? cliAccount, CancellationToken ct = default)
            => Task.FromResult(ForgeState);
    }
}
