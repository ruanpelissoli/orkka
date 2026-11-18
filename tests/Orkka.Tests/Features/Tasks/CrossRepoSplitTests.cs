namespace Orkka.Tests.Features.Tasks;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.InMemory.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Orkka.Modules.Usage.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.Modules.Tasks.Infrastructure.Services;
using Orkka.SharedKernel.Ai;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// The split turns one spanning request into one task per repository plus the contract they
/// share. What it must never do is leave a group half-built: a member with no sibling sits
/// behind a merge gate waiting on tasks that will never exist, which looks exactly like a hung
/// pipeline. So every unusable reply has to end with the task running alone and the group closed.
/// </summary>
public sealed class CrossRepoSplitTests : IDisposable
{
    private readonly TasksDbContext _db;
    private readonly Ulid _orgId = Ulid.NewUlid();
    private readonly Ulid _projectId = Ulid.NewUlid();
    private readonly RepoSummary _api;
    private readonly RepoSummary _web;

    private string _modelReply = "";
    private bool _runnerOnline = true;

    public CrossRepoSplitTests()
    {
        var options = new DbContextOptionsBuilder<TasksDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            // The split writes its members and their edges in one transaction, and the in-memory
            // provider throws rather than no-opping on BeginTransaction. Suppressed rather than
            // worked around: these tests are about what the split DECIDES, and that the write is
            // atomic is a database guarantee the integration suite exercises for real.
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new TasksDbContext(options, currentUser: null, eventBus: null);

        _api = Repo("api");
        _web = Repo("web");
    }

    public void Dispose() => _db.Dispose();

    private RepoSummary Repo(string name) => new(
        Ulid.NewUlid(), _projectId, _orgId, name, $"https://example.test/{name}.git",
        "main", "#000000", WorkspaceStatus.Ready);

    private CrossRepoSplitService BuildService()
    {
        // The creator's runner is the only model: there is no platform-key fallback.
        var runner = new Mock<IUserRunnerUtility>();
        runner
            .Setup(r => r.TryCompleteAsync(
                _orgId, It.IsAny<string?>(), It.IsAny<AnthropicRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _runnerOnline ? new AnthropicResponse { Text = _modelReply } : null);

        var projects = new Mock<IProjectsApi>();
        projects
            .Setup(p => p.ListReposAsync(_projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([_api, _web]);

        var numbers = new Mock<ITaskNumberService>();
        var next = 100;
        numbers
            .Setup(n => n.ReserveAsync(_projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++next);

        return new CrossRepoSplitService(
            runner.Object,
            Mock.Of<IUsageRecorder>(),
            _db,
            numbers.Object,
            Mock.Of<ITaskNotifier>(),
            projects.Object,
            Mock.Of<Orkka.Modules.SourceControl.Contracts.IRepoContextBuilder>(),
            // UtilityModel is derived from DefaultModel, so the override is what a test can set.
            Options.Create(new AnthropicOptions { DefaultModel = "test-model" }),
            NullLogger<CrossRepoSplitService>.Instance);
    }

    /// <summary>Seeds a submitted spanning task and its Splitting group, as the create endpoint
    /// and the submission service would leave them.</summary>
    private async Task<(TaskEntity Task, TaskGroup Group)> SeedSpanningTaskAsync(
        IEnumerable<Ulid>? spans = null)
    {
        var group = new TaskGroup
        {
            Id = Ulid.NewUlid(),
            OrgId = _orgId,
            ProjectId = _projectId,
            Title = "Add avatar upload",
            Request = "Users should be able to upload an avatar.",
            SpansRepoIdsJson = SpannedRepoIds.Serialize(spans ?? [_api.Id, _web.Id]),
            Status = TaskGroupStatus.Splitting,
        };
        _db.TaskGroups.Add(group);

        var task = new TaskEntity
        {
            Id = Ulid.NewUlid(),
            OrgId = _orgId,
            ProjectId = _projectId,
            GroupId = group.Id,
            RepoId = _api.Id,
            Number = 7,
            Title = "Add avatar upload",
            Description = "Users should be able to upload an avatar.",
            CurrentLane = Lane.Ready,
            Status = TaskStatus.InQueue,
        };
        _db.Tasks.Add(task);

        await _db.SaveChangesAsync();
        return (task, group);
    }

    private const string GoodReply = """
        {
          "contract": "### Endpoints\nPOST /api/users/{id}/avatar -> 201 {\"url\": string}",
          "parts": [
            { "repo": "api", "title": "Avatar upload endpoint", "description": "Server side.", "dependsOn": [] },
            { "repo": "web", "title": "Avatar upload screen", "description": "Client side.", "dependsOn": [0] }
          ]
        }
        """;

    [Fact]
    public async Task Split_CreatesOneTaskPerRepo_WithTheContractAndTheEdge()
    {
        var (task, group) = await SeedSpanningTaskAsync();
        _modelReply = GoodReply;

        await BuildService().SplitAsync(task.Id);

        var members = await _db.Tasks.Where(t => t.GroupId == group.Id).ToListAsync();
        members.Should().HaveCount(2);
        members.Select(m => m.RepoId).Should().BeEquivalentTo([_api.Id, _web.Id]);

        // The originating task keeps its number and identity — anything already linking to it
        // must keep working — while taking over its own repo's half.
        var primary = members.Single(m => m.Id == task.Id);
        primary.Number.Should().Be(7);
        primary.RepoId.Should().Be(_api.Id);
        primary.Title.Should().Be("Avatar upload endpoint");

        var sibling = members.Single(m => m.Id != task.Id);
        sibling.RepoId.Should().Be(_web.Id);
        // Submitted, not drafted: a sibling left in Drafting would hold the gate forever.
        sibling.Status.Should().Be(TaskStatus.InQueue);
        sibling.CurrentLane.Should().Be(Lane.Ready);

        var reloaded = await _db.TaskGroups.FirstAsync(g => g.Id == group.Id);
        reloaded.Status.Should().Be(TaskGroupStatus.Active);
        reloaded.Contract.Should().Contain("POST /api/users/{id}/avatar");

        var edge = await _db.TaskDependencies.SingleAsync();
        edge.TaskId.Should().Be(sibling.Id);
        edge.DependsOnTaskId.Should().Be(task.Id);
        // Manual, or the dependency analyzer drops it on its next run.
        edge.Source.Should().Be(DependencySource.Manual);
    }

    [Fact]
    public async Task Split_LeavesNoGroupBehind_WhenTheModelRepliesItIsNotNeeded()
    {
        var (task, group) = await SeedSpanningTaskAsync();
        _modelReply = """{"contract": null, "parts": []}""";

        await BuildService().SplitAsync(task.Id);

        (await _db.Tasks.FirstAsync(t => t.Id == task.Id)).GroupId.Should().BeNull();
        (await _db.TaskGroups.FirstAsync(g => g.Id == group.Id)).Status
            .Should().Be(TaskGroupStatus.Cancelled);
        (await _db.Tasks.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Split_LeavesNoGroupBehind_WhenTheReplyIsUnparseable()
    {
        var (task, group) = await SeedSpanningTaskAsync();
        _modelReply = "I'm afraid I can't do that.";

        await BuildService().SplitAsync(task.Id);

        (await _db.Tasks.FirstAsync(t => t.Id == task.Id)).GroupId.Should().BeNull();
        (await _db.TaskGroups.FirstAsync(g => g.Id == group.Id)).Status
            .Should().Be(TaskGroupStatus.Cancelled);

        // The user has to learn why their spanning task became an ordinary one.
        (await _db.TaskMessages.CountAsync(m => m.TaskId == task.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Split_WithTheRunnerOffline_AbandonsHonestly()
    {
        var (task, group) = await SeedSpanningTaskAsync();
        _modelReply = GoodReply;
        _runnerOnline = false;

        await BuildService().SplitAsync(task.Id);

        (await _db.Tasks.CountAsync()).Should().Be(1, "no member may be created without a plan");
        (await _db.Tasks.FirstAsync(t => t.Id == task.Id)).GroupId.Should().BeNull();
        (await _db.TaskGroups.FirstAsync(g => g.Id == group.Id)).Status
            .Should().Be(TaskGroupStatus.Cancelled);

        var note = await _db.TaskMessages.SingleAsync(m => m.TaskId == task.Id);
        note.Content.Should().Contain("./orkka start");
    }

    [Fact]
    public async Task Split_DropsAPartNamingARepoTheProjectDoesNotHave()
    {
        var (task, group) = await SeedSpanningTaskAsync();
        _modelReply = """
            {
              "contract": "### Endpoints\nPOST /x",
              "parts": [
                { "repo": "api", "title": "A", "description": "d", "dependsOn": [] },
                { "repo": "mobile", "title": "B", "description": "d", "dependsOn": [0] }
              ]
            }
            """;

        await BuildService().SplitAsync(task.Id);

        // One usable part is not a split — filing the invented repo's work nowhere and gating
        // the survivor on it would hang the task.
        (await _db.Tasks.FirstAsync(t => t.Id == task.Id)).GroupId.Should().BeNull();
        (await _db.TaskGroups.FirstAsync(g => g.Id == group.Id)).Status
            .Should().Be(TaskGroupStatus.Cancelled);
    }

    [Fact]
    public async Task Split_KeepsTheFirstPart_WhenTheModelNamesARepoTwice()
    {
        var (task, _) = await SeedSpanningTaskAsync();
        _modelReply = """
            {
              "contract": "### Endpoints\nPOST /x",
              "parts": [
                { "repo": "api", "title": "First", "description": "d", "dependsOn": [] },
                { "repo": "api", "title": "Duplicate", "description": "d", "dependsOn": [] },
                { "repo": "web", "title": "Client", "description": "d", "dependsOn": [0] }
              ]
            }
            """;

        await BuildService().SplitAsync(task.Id);

        // Two tasks in one repo would share a worktree and a branch, and the second would
        // silently overwrite the first.
        var members = await _db.Tasks.Where(t => t.GroupId != null).ToListAsync();
        members.Should().HaveCount(2);
        members.Select(m => m.RepoId).Should().OnlyHaveUniqueItems();
        members.Single(m => m.RepoId == _api.Id).Title.Should().Be("First");
    }

    [Fact]
    public async Task Split_IsIdempotent_WhenMembersAlreadyExist()
    {
        var (task, group) = await SeedSpanningTaskAsync();
        _modelReply = GoodReply;
        var service = BuildService();

        await service.SplitAsync(task.Id);
        var afterFirst = await _db.Tasks.CountAsync();

        // A retry after the process died mid-split must not duplicate every sibling.
        await service.SplitAsync(task.Id);

        (await _db.Tasks.CountAsync()).Should().Be(afterFirst);
        (await _db.TaskGroups.FirstAsync(g => g.Id == group.Id)).Status
            .Should().Be(TaskGroupStatus.Active);
    }

    [Fact]
    public async Task Split_AbandonsWhenTheSpannedRepoIsGone()
    {
        // The repo was removed from the project between creation and submission.
        var (task, group) = await SeedSpanningTaskAsync([_api.Id, Ulid.NewUlid()]);
        _modelReply = GoodReply;

        await BuildService().SplitAsync(task.Id);

        (await _db.Tasks.FirstAsync(t => t.Id == task.Id)).GroupId.Should().BeNull();
        (await _db.TaskGroups.FirstAsync(g => g.Id == group.Id)).Status
            .Should().Be(TaskGroupStatus.Cancelled);
    }
}
