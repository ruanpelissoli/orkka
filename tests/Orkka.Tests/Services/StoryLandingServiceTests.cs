namespace Orkka.Tests.Services;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.Modules.SourceControl.Domain;
using Orkka.Modules.SourceControl.Infrastructure;
using Orkka.Modules.SourceControl.Infrastructure.Services;
using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Utilities;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// Landing a Story member through <see cref="IPullRequestService.MergeAsync"/>: the first opens
/// the story's DRAFT pull request, a middle one ticks its checklist, the last checks the shared
/// branch merges, marks the pull request ready and stamps the story ready; a linked folder is
/// squash-merged once, at the end. Also: a member's Orkka pull request is created for the task
/// named (never looked up by the shared branch) and shows only its own change.
/// </summary>
public sealed class StoryLandingServiceTests : IDisposable
{
    private static readonly Ulid OrgId = Ulid.NewUlid();
    private static readonly Ulid ProjectId = Ulid.NewUlid();
    private static readonly Ulid RemoteRepo = Ulid.NewUlid();
    private static readonly Ulid FolderRepo = Ulid.NewUlid();
    private static readonly Ulid WebRepo = Ulid.NewUlid();
    private const string WebDraftUrl = "https://github.com/acme/web/pull/3";
    private static readonly Ulid GroupId = Ulid.NewUlid();
    private const string Branch = "feature/SHOP-10-checkout";
    private const string DraftUrl = "https://github.com/acme/shop/pull/7";

    private readonly SourceControlDbContext _db = new(
        new DbContextOptionsBuilder<SourceControlDbContext>().UseInMemoryDatabase($"story-{Ulid.NewUlid()}").Options);
    private readonly StoryGitOps _git = new();
    private readonly Mock<ITasksApi> _tasks = new(MockBehavior.Loose);
    private readonly Mock<IProjectsApi> _projects = new();
    private readonly OrkkaPullRequestService _service;
    private readonly Ulid _first = Ulid.NewUlid();
    private readonly Ulid _second = Ulid.NewUlid();
    private StoryContext _story;

    public StoryLandingServiceTests()
    {
        var projects = _projects;
        projects.Setup(p => p.GetRepoAsync(RemoteRepo, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepoSummary(RemoteRepo, ProjectId, OrgId, "shop", "git@github.com:acme/shop.git", "main", "#fff",
                WorkspaceStatus.Ready, ForgeKind: ForgeKind.GitHub, CliAccount: "dana"));
        projects.Setup(p => p.GetRepoAsync(WebRepo, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepoSummary(WebRepo, ProjectId, OrgId, "web", "git@github.com:acme/web.git", "develop", "#fff",
                WorkspaceStatus.Ready, ForgeKind: ForgeKind.GitHub));
        projects.Setup(p => p.GetRepoAsync(FolderRepo, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepoSummary(FolderRepo, ProjectId, OrgId, "tools", @"C:\Code\tools", "main", "#fff",
                WorkspaceStatus.Ready));

        _story = Story(RemoteRepo, firstLanded: false, branches: [new StoryBranchState(RemoteRepo, Branch, null, false, null, null)]);
        _tasks.Setup(t => t.GetStoryForTaskAsync(It.IsAny<Ulid>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => _story);
        _tasks.Setup(t => t.GetStoryAsync(GroupId, It.IsAny<CancellationToken>())).ReturnsAsync(() => _story);

        var services = new ServiceCollection().AddSingleton(_tasks.Object).BuildServiceProvider();
        _service = new OrkkaPullRequestService(_db, projects.Object, services, _git, Mock.Of<ILogger<OrkkaPullRequestService>>(),
            new StoryLandingService(_db, projects.Object, services, _git, Mock.Of<ILogger<StoryLandingService>>()));
    }

    public void Dispose() => _db.Dispose();

    private StoryContext Story(Ulid repo, bool firstLanded, IReadOnlyList<StoryBranchState> branches, int pending = 0) => new(
        GroupId, OrgId, ProjectId, "[SHOP-10] Checkout", "desc", "SHOP-10", "https://acme.atlassian.net/browse/SHOP-10",
        Branch, null, false, pending,
        [
            new StoryMemberContext(_first, 11, "[SHOP-11] Cart", 1, repo, firstLanded ? TaskStatus.Completed : TaskStatus.Active,
                firstLanded, false, "aaaaaaa", null, [], "SHOP-11", "https://acme.atlassian.net/browse/SHOP-11", "user_dana"),
            new StoryMemberContext(_second, 12, "[SHOP-12] Pay", 2, repo, TaskStatus.Active, false, false, "bbbbbbb", null, [],
                "SHOP-12", "https://acme.atlassian.net/browse/SHOP-12", "user_dana"),
        ],
        branches);

    private async Task<int> PrForAsync(Ulid repo, Ulid taskId, string baseSha)
        => (await _service.CreateForTaskAsync(repo, taskId, Branch, "main", "t", "b", baseSha)).Value!.Number;

    [Fact]
    public async Task TheFirstMember_OpensTheStoryPullRequestAsADraft()
    {
        var number = await PrForAsync(RemoteRepo, _first, "aaaaaaa");

        var landed = await _service.MergeAsync(RemoteRepo, number);

        landed.IsSuccess.Should().BeTrue(landed.Error);
        landed.Value!.ForgeUrl.Should().Be(DraftUrl);
        _git.Drafts.Should().ContainSingle().Which.Should().Be((RemoteRepo, Branch, "main"));
        _git.DraftBodies.Single().Should().Contain("- [x] [SHOP-11](https://acme.atlassian.net/browse/SHOP-11) Cart")
            .And.Contain("- [ ] [SHOP-12](https://acme.atlassian.net/browse/SHOP-12) Pay")
            .And.Contain("[SHOP-10](https://acme.atlassian.net/browse/SHOP-10)");
        _git.Readied.Should().BeEmpty();
        _git.Squashes.Should().BeEmpty();
        _tasks.Verify(t => t.SetGroupBranchPullRequestAsync(GroupId, RemoteRepo, Branch, DraftUrl, true, It.IsAny<CancellationToken>()), Times.Once);
        _tasks.Verify(t => t.SetStoryReadyAsync(It.IsAny<Ulid>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()), Times.Never);
        (await _db.LocalPullRequests.SingleAsync()).Merged.Should().BeTrue("landed on the shared branch");
    }

    [Fact]
    public async Task AMiddleMember_UpdatesTheChecklist_AndDoesNotReady()
    {
        // A third member still to come keeps the second from being last.
        _story = _story with
        {
            Members = [.. Story(RemoteRepo, true, []).Members,
                new StoryMemberContext(Ulid.NewUlid(), 13, "[SHOP-13] Ship", 3, RemoteRepo, TaskStatus.Draft, false, false, null, null, [])],
            Branches = [new StoryBranchState(RemoteRepo, Branch, DraftUrl, true, null, "open")],
        };
        var number = await PrForAsync(RemoteRepo, _second, "bbbbbbb");

        (await _service.MergeAsync(RemoteRepo, number)).IsSuccess.Should().BeTrue();

        _git.Drafts.Should().BeEmpty("the draft exists");
        _git.Bodies.Should().ContainSingle().Which.Body.Should().Contain("- [x] [SHOP-12]").And.Contain("- [ ] [SHOP-13] Ship");
        _git.Readied.Should().BeEmpty();
    }

    [Fact]
    public async Task TheLastMember_ChecksTheBranch_MarksThePullRequestReady_AndStampsTheStory()
    {
        _story = Story(RemoteRepo, true, [new StoryBranchState(RemoteRepo, Branch, DraftUrl, true, null, "open")]);
        var number = await PrForAsync(RemoteRepo, _second, "bbbbbbb");

        (await _service.MergeAsync(RemoteRepo, number)).IsSuccess.Should().BeTrue();

        _git.MergeChecks.Should().ContainSingle().Which.Should().Be((Branch, "main"));
        _git.Readied.Should().ContainSingle().Which.Should().Be((DraftUrl, false));
        _tasks.Verify(t => t.SetStoryReadyAsync(GroupId, It.IsNotNull<DateTimeOffset?>(), It.IsAny<CancellationToken>()), Times.Once);
        _tasks.Verify(t => t.SettleStoryAsync(It.IsAny<Ulid>(), It.IsAny<CancellationToken>()), Times.Never,
            "the forge pull request still has to merge");
    }

    [Fact]
    public async Task TheLastMember_WithAConflictingBranch_GoesThroughTheResolverFirst()
    {
        _story = Story(RemoteRepo, true, [new StoryBranchState(RemoteRepo, Branch, DraftUrl, true, null, "open")]);
        _git.MergesCleanly = false;
        var number = await PrForAsync(RemoteRepo, _second, "bbbbbbb");

        var landed = await _service.MergeAsync(RemoteRepo, number);

        landed.IsFailure.Should().BeTrue();
        landed.Error.Should().Contain("Merge conflict between", "the merge sweep's conflict path keys on these words");
        _git.Readied.Should().BeEmpty();
        (await _db.LocalPullRequests.SingleAsync()).Merged.Should().BeFalse();
    }

    [Fact]
    public async Task ALaterSubTaskStillPending_KeepsTheStoryFromReadying()
    {
        _story = Story(RemoteRepo, true, [new StoryBranchState(RemoteRepo, Branch, DraftUrl, true, null, "open")], pending: 1);
        var number = await PrForAsync(RemoteRepo, _second, "bbbbbbb");

        (await _service.MergeAsync(RemoteRepo, number)).IsSuccess.Should().BeTrue();

        _git.Readied.Should().BeEmpty();
    }

    [Fact]
    public async Task ALinkedFolder_HasNoDraft_AndIsSquashMergedOnce_AtTheEnd()
    {
        _story = Story(FolderRepo, false, [new StoryBranchState(FolderRepo, Branch, null, false, null, null)]);
        var first = await PrForAsync(FolderRepo, _first, "aaaaaaa");
        (await _service.MergeAsync(FolderRepo, first)).IsSuccess.Should().BeTrue();
        _git.Drafts.Should().BeEmpty();
        _git.Squashes.Should().BeEmpty("the folder gets the story once it is whole");

        _story = Story(FolderRepo, true, [new StoryBranchState(FolderRepo, Branch, null, false, null, null)]);
        var second = await PrForAsync(FolderRepo, _second, "bbbbbbb");
        (await _service.MergeAsync(FolderRepo, second)).IsSuccess.Should().BeTrue();

        _git.Squashes.Should().ContainSingle().Which.Should().Be((Branch, "main"));
        _tasks.Verify(t => t.SetStoryReadyAsync(GroupId, It.IsNotNull<DateTimeOffset?>(), It.IsAny<CancellationToken>()), Times.Once);
        _tasks.Verify(t => t.SettleStoryAsync(GroupId, It.IsAny<CancellationToken>()), Times.Once);
        _tasks.Verify(t => t.SetGroupBranchForgeStateAsync(GroupId, FolderRepo, "merged", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReturnToDraft_UndoesReadyOnEveryOpenPullRequest()
    {
        _story = Story(RemoteRepo, true, [new StoryBranchState(RemoteRepo, Branch, DraftUrl, false, DateTimeOffset.UtcNow, "open")]);

        var undo = await new StoryLandingService(_db, _projects.Object,
            new ServiceCollection().AddSingleton(_tasks.Object).BuildServiceProvider(), _git,
            Mock.Of<ILogger<StoryLandingService>>()).ReturnToDraftAsync(GroupId);

        undo.IsSuccess.Should().BeTrue();
        _git.Readied.Should().ContainSingle().Which.Should().Be((DraftUrl, true));
        _tasks.Verify(t => t.SetStoryReadyAsync(GroupId, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AMembersPullRequest_IsForTheNamedTask_PerTask_AndShowsOnlyItsOwnChange()
    {
        var first = await PrForAsync(RemoteRepo, _first, "aaaaaaa");
        var second = await PrForAsync(RemoteRepo, _second, "bbbbbbb");
        var again = await PrForAsync(RemoteRepo, _second, "bbbbbbb");

        second.Should().NotBe(first, "the next member opens its own pull request on the shared branch");
        again.Should().Be(second);
        var rows = await _db.LocalPullRequests.OrderBy(p => p.Number).ToListAsync();
        rows.Select(r => r.TaskId).Should().Equal(_first, _second);
        rows.Select(r => r.BaseSha).Should().Equal("aaaaaaa", "bbbbbbb");
        _tasks.Verify(t => t.FindTaskIdByBranchAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<Ulid>>(), It.IsAny<CancellationToken>()),
            Times.Never, "a shared branch would resolve to the wrong member");

        await _service.GetFilesAsync(RemoteRepo, second);
        _git.DiffBaseShas.Should().ContainSingle().Which.Should().Be("bbbbbbb");
    }

    // ── A story spanning several repositories ────────────────────────────────

    private StoryContext TwoRepoStory() => _story with
    {
        Members =
        [
            new StoryMemberContext(_first, 11, "[SHOP-11] Cart", 1, WebRepo, TaskStatus.Completed, true, false, "aaaaaaa", null, [],
                "SHOP-11", null, "user_dana"),
            new StoryMemberContext(_second, 12, "[SHOP-12] Pay", 2, RemoteRepo, TaskStatus.Active, false, false, "bbbbbbb", null, [],
                "SHOP-12", null, "user_dana"),
        ],
        Branches =
        [
            new StoryBranchState(WebRepo, Branch, WebDraftUrl, true, null, "open"),
            new StoryBranchState(RemoteRepo, Branch, DraftUrl, true, null, "open"),
        ],
    };

    [Fact]
    public async Task TheLastMember_OfAMultiRepoStory_ChecksEveryRepository_AndReadiesThemAll()
    {
        _story = TwoRepoStory();
        var number = await PrForAsync(RemoteRepo, _second, "bbbbbbb");

        (await _service.MergeAsync(RemoteRepo, number)).IsSuccess.Should().BeTrue();

        _git.CheckedRepos.Should().BeEquivalentTo([(WebRepo, "develop"), (RemoteRepo, "main")],
            "each repository is checked against its OWN default branch, not only the landing one");
        _git.Readied.Should().BeEquivalentTo([(WebDraftUrl, false), (DraftUrl, false)]);
        _tasks.Verify(t => t.SetStoryReadyAsync(GroupId, It.IsNotNull<DateTimeOffset?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AConflictInAnotherRepository_LeavesEveryPullRequestADraft_AndIsAConflictForTheResolver()
    {
        _story = TwoRepoStory();
        _git.CleanByRepo[WebRepo] = false;
        var number = await PrForAsync(RemoteRepo, _second, "bbbbbbb");

        var landed = await _service.MergeAsync(RemoteRepo, number);

        landed.IsFailure.Should().BeTrue();
        StoryLandingErrors.IsStoryConflict(landed.Error).Should().BeTrue("the merge sweep sends it to the resolver");
        landed.Error.Should().Contain("web (develop)").And.NotContain("shop (main)");
        _git.Readied.Should().BeEmpty("nothing is marked ready unless every repository is clean");
        _tasks.Verify(t => t.SetStoryReadyAsync(It.IsAny<Ulid>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()), Times.Never);
        (await _db.LocalPullRequests.SingleAsync()).Merged.Should().BeFalse("it lands again once resolved");
    }

    [Fact]
    public async Task ARepositoryNobodyCouldCheck_KeepsTheStoryADraft_AsAnEnvironmentBlock()
    {
        _story = TwoRepoStory();
        _git.CleanByRepo[WebRepo] = null;
        var number = await PrForAsync(RemoteRepo, _second, "bbbbbbb");

        var landed = await _service.MergeAsync(RemoteRepo, number);

        landed.IsFailure.Should().BeTrue();
        MergeBlockedReasons.IsEnvironmentBlocked(landed.Error).Should().BeTrue("retried every cycle, never readied on a guess");
        _git.Readied.Should().BeEmpty();
    }

    /// <summary>A mixed folder + forge story readied once (folder squashed, forge PR ready) gets a
    /// late sub-task in the folder: when it lands last, the folder is squashed again (it had
    /// never received the late work) and the forge pull request is readied again.</summary>
    [Fact]
    public async Task AMixedStory_WithALateFolderSubTaskAfterReady_SquashesTheFolderAgain_AndReReadiesTheForge()
    {
        var late = Ulid.NewUlid();
        _story = _story with
        {
            Members =
            [
                new StoryMemberContext(_first, 11, "[SHOP-11] Cart", 1, FolderRepo, TaskStatus.Completed, true, false, "aaaaaaa", null, []),
                new StoryMemberContext(_second, 12, "[SHOP-12] Pay", 2, RemoteRepo, TaskStatus.Completed, true, false, "bbbbbbb", null, []),
                new StoryMemberContext(late, 14, "[SHOP-14] Receipt", 3, FolderRepo, TaskStatus.Active, false, false, "ccccccc", null, []),
            ],
            // After the late append: the folder row reopened (state cleared), the forge PR a draft again.
            Branches =
            [
                new StoryBranchState(FolderRepo, Branch, null, false, null, null),
                new StoryBranchState(RemoteRepo, Branch, DraftUrl, true, null, "open"),
            ],
        };
        var number = await PrForAsync(FolderRepo, late, "ccccccc");

        var landed = await _service.MergeAsync(FolderRepo, number);

        landed.IsSuccess.Should().BeTrue(landed.Error);
        _git.CheckedRepos.Select(c => c.Repo).Should().BeEquivalentTo([FolderRepo, RemoteRepo]);
        _git.Squashes.Should().ContainSingle().Which.Should().Be((Branch, "main"));
        _git.Readied.Should().ContainSingle().Which.Should().Be((DraftUrl, false));
        _tasks.Verify(t => t.SettleStoryAsync(It.IsAny<Ulid>(), It.IsAny<CancellationToken>()), Times.Never,
            "the forge pull request still has to merge");
    }

    /// <summary>A folder squashed at the first ready, with no late member of its own, is not
    /// squashed (or checked) a second time when a late forge sub-task re-readies the story.</summary>
    [Fact]
    public async Task AMixedStory_WithALateForgeSubTask_LeavesTheSquashedFolderAlone()
    {
        var late = Ulid.NewUlid();
        _story = _story with
        {
            Members =
            [
                new StoryMemberContext(_first, 11, "[SHOP-11] Cart", 1, FolderRepo, TaskStatus.Completed, true, false, "aaaaaaa", null, []),
                new StoryMemberContext(late, 13, "[SHOP-13] Tax", 2, RemoteRepo, TaskStatus.Active, false, false, "ccccccc", null, []),
            ],
            Branches =
            [
                new StoryBranchState(FolderRepo, Branch, null, false, null, "merged"),
                new StoryBranchState(RemoteRepo, Branch, DraftUrl, true, null, "open"),
            ],
        };
        var number = await PrForAsync(RemoteRepo, late, "ccccccc");

        (await _service.MergeAsync(RemoteRepo, number)).IsSuccess.Should().BeTrue();

        _git.Squashes.Should().BeEmpty();
        _git.CheckedRepos.Select(c => c.Repo).Should().Equal(RemoteRepo);
        _git.Readied.Should().ContainSingle();
    }

    /// <summary>The developer's machine, Story operations included.</summary>
    private sealed class StoryGitOps : ILocalRepoGitOps
    {
        public bool? MergesCleanly { get; set; } = true;
        public Dictionary<Ulid, bool?> CleanByRepo { get; } = [];
        public List<(string Head, string Base)> MergeChecks { get; } = [];
        public List<(Ulid Repo, string Base)> CheckedRepos { get; } = [];
        public List<(string Head, string Base)> Squashes { get; } = [];
        public List<(Ulid Repo, string Head, string Base)> Drafts { get; } = [];
        public List<string> DraftBodies { get; } = [];
        public List<(string Url, bool Undo)> Readied { get; } = [];
        public List<(string Url, string Body)> Bodies { get; } = [];
        public List<string?> DiffBaseShas { get; } = [];

        public Task<Result> SquashMergeAsync(Ulid orgId, Ulid repoId, string? runnerUserId,
            string headBranch, string baseBranch, string commitMessage, CancellationToken ct = default)
        {
            Squashes.Add((headBranch, baseBranch));
            return Task.FromResult(Result.Success());
        }

        public Task<Result<string>> GetDiffAsync(Ulid orgId, Ulid repoId, Ulid taskId, string? runnerUserId,
            string baseBranch, string headBranch, CancellationToken ct = default)
            => GetDiffAsync(orgId, repoId, taskId, runnerUserId, baseBranch, headBranch, null, ct);

        public Task<Result<string>> GetDiffAsync(Ulid orgId, Ulid repoId, Ulid taskId, string? runnerUserId,
            string baseBranch, string headBranch, string? baseSha, CancellationToken ct = default)
        {
            DiffBaseShas.Add(baseSha);
            return Task.FromResult(Result<string>.Success(""));
        }

        public Task<Result<string>> GetWorkingTreeDiffAsync(Ulid orgId, Ulid repoId, Ulid taskId, string? runnerUserId,
            string baseBranch, CancellationToken ct = default) => Task.FromResult(Result<string>.Success(""));

        public Task<bool?> WouldMergeCleanlyAsync(Ulid orgId, Ulid repoId, string? runnerUserId,
            string headBranch, string baseBranch, CancellationToken ct = default)
        {
            MergeChecks.Add((headBranch, baseBranch));
            CheckedRepos.Add((repoId, baseBranch));
            return Task.FromResult(CleanByRepo.TryGetValue(repoId, out var clean) ? clean : MergesCleanly);
        }

        public Task<Result<string>> OpenPullRequestAsync(Ulid orgId, Ulid repoId, string? runnerUserId,
            string headBranch, string baseBranch, string title, string body, ForgeKind forge, string? cliAccount,
            CancellationToken ct = default)
            => throw new InvalidOperationException("A story member never opens a ready pull request directly.");

        public Task<Result<string>> OpenDraftPullRequestAsync(Ulid orgId, Ulid repoId, string? runnerUserId,
            string headBranch, string baseBranch, string title, string body, ForgeKind forge, string? cliAccount,
            CancellationToken ct = default)
        {
            Drafts.Add((repoId, headBranch, baseBranch));
            DraftBodies.Add(body);
            return Task.FromResult(Result<string>.Success(DraftUrl));
        }

        public Task<Result> MarkPullRequestReadyAsync(Ulid orgId, Ulid repoId, string? runnerUserId,
            string pullRequestUrl, bool undo, ForgeKind forge, string? cliAccount, CancellationToken ct = default)
        {
            Readied.Add((pullRequestUrl, undo));
            return Task.FromResult(Result.Success());
        }

        public Task<Result> UpdatePullRequestBodyAsync(Ulid orgId, Ulid repoId, string? runnerUserId,
            string pullRequestUrl, string body, ForgeKind forge, string? cliAccount, CancellationToken ct = default)
        {
            Bodies.Add((pullRequestUrl, body));
            return Task.FromResult(Result.Success());
        }

        public Task<string?> GetPullRequestStateAsync(Ulid orgId, Ulid repoId, string? runnerUserId,
            string pullRequestUrl, ForgeKind forge, string? cliAccount, CancellationToken ct = default)
            => Task.FromResult<string?>("open");
    }
}
