namespace Orkka.Tests.Features.Tasks;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.Modules.Tasks.Infrastructure.Services;
using Orkka.Modules.Tasks.PublicApi;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// The entry condition for the post-completion documentation pass. The sweep is a pull, not a
/// push — nine code paths put a task in Done and none of them signal documentation — so this
/// query IS the trigger, and everything it must not pick up matters as much as what it must.
/// </summary>
public sealed class DocSyncQueryTests : IDisposable
{
    private readonly TasksDbContext _db;
    private readonly TasksApi _api;

    private static readonly DateTimeOffset Now = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LookbackCutoff = Now.AddHours(-48);
    private static readonly DateTimeOffset StaleCutoff = Now.AddMinutes(-30);
    private const int MaxAttempts = 3;

    public DocSyncQueryTests()
    {
        var options = new DbContextOptionsBuilder<TasksDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new TasksDbContext(options, currentUser: null, eventBus: null);
        _api = new TasksApi(
            _db,
            Mock.Of<ITaskNumberService>(),
            Mock.Of<IProjectsApi>(),
            Mock.Of<ITaskNotifier>(),
            Mock.Of<Orkka.SharedKernel.Events.IEventBus>(),
            new TaskSubmissionService(
                _db,
                Mock.Of<ITaskNotifier>(),
                new MilestoneProgressUpdater(
                    _db,
                    Mock.Of<ITaskNotifier>(),
                    Mock.Of<Orkka.SharedKernel.Events.IEventBus>(),
                    NullLogger<MilestoneProgressUpdater>.Instance),
                Mock.Of<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
                NullLogger<TaskSubmissionService>.Instance),
            NullLogger<TasksApi>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private async Task<TaskEntity> SeedAsync(
        Lane lane = Lane.Done,
        TaskStatus status = TaskStatus.Completed,
        Ulid? repoId = null,
        bool isMerged = true,
        DocSyncStatus docSync = DocSyncStatus.None,
        int attempts = 0,
        DateTimeOffset? completedAt = null,
        bool isArchived = false,
        DateTimeOffset? resetRequestedAt = null)
    {
        var task = new TaskEntity
        {
            Id = Ulid.NewUlid(),
            OrgId = Ulid.NewUlid(),
            ProjectId = Ulid.NewUlid(),
            RepoId = repoId,
            Number = 1,
            Title = "A task",
            CurrentLane = lane,
            Status = status,
            IsMerged = isMerged,
            IsArchived = isArchived,
            ResetRequestedAt = resetRequestedAt,
            DocSyncStatus = docSync,
            DocSyncAttempts = attempts,
            CompletedAt = completedAt ?? Now.AddHours(-1),
            CreatedByUserId = "user_creator",
        };
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return task;
    }

    /// <summary>The abandoned-claim tests move the CUTOFF rather than the row's UpdatedAt: the
    /// DbContext stamps UpdatedAt on every save, so a hand-set timestamp never survives. The
    /// predicate under test is the same comparison either way.</summary>
    private Task<IReadOnlyList<DocSyncCandidateTask>> ListAsync(DateTimeOffset? staleBefore = null) =>
        _api.ListTasksAwaitingDocSyncAsync(
            LookbackCutoff, MaxAttempts, staleBefore ?? StaleCutoff);

    [Fact]
    public async Task A_settled_repoless_task_is_a_candidate()
    {
        var task = await SeedAsync(repoId: null, isMerged: false);

        (await ListAsync()).Should().ContainSingle().Which.Id.Should().Be(task.Id);
    }

    [Fact]
    public async Task A_merged_repo_task_is_a_candidate()
    {
        var task = await SeedAsync(repoId: Ulid.NewUlid(), isMerged: true);

        (await ListAsync()).Should().ContainSingle().Which.Id.Should().Be(task.Id);
    }

    [Fact]
    public async Task An_unmerged_repo_task_is_not_a_candidate()
    {
        // The whole point of waiting: a PR closed without merging must never leave the
        // knowledge base describing code that was never shipped.
        await SeedAsync(repoId: Ulid.NewUlid(), isMerged: false);

        (await ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task An_archived_completed_task_is_still_a_candidate()
    {
        // Dragging a task to Done archives it outright, and tidying the board is not a
        // statement about whether the work deserves documenting.
        var task = await SeedAsync(isArchived: true, repoId: null);

        (await ListAsync()).Should().ContainSingle().Which.Id.Should().Be(task.Id);
    }

    [Fact]
    public async Task A_task_pending_reset_is_not_a_candidate()
    {
        await SeedAsync(resetRequestedAt: Now, repoId: null);

        (await ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_task_that_is_not_done_is_not_a_candidate()
    {
        await SeedAsync(lane: Lane.CodeReview, status: TaskStatus.WaitingCodeReview, repoId: null);

        (await ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_completed_pass_is_not_revisited()
    {
        await SeedAsync(docSync: DocSyncStatus.Completed, repoId: null);

        (await ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_skipped_pass_is_not_revisited()
    {
        // Skipped is the expected outcome for most tasks — re-running it every minute would
        // make the cheap path the expensive one.
        await SeedAsync(docSync: DocSyncStatus.Skipped, repoId: null);

        (await ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_pass_under_the_attempt_cap_is_retried()
    {
        var task = await SeedAsync(
            docSync: DocSyncStatus.Failed, attempts: MaxAttempts - 1, repoId: null);

        (await ListAsync()).Should().ContainSingle().Which.Id.Should().Be(task.Id);
    }

    [Fact]
    public async Task A_failed_pass_at_the_attempt_cap_is_left_alone()
    {
        await SeedAsync(docSync: DocSyncStatus.Failed, attempts: MaxAttempts, repoId: null);

        (await ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_live_running_pass_is_not_stolen()
    {
        await SeedAsync(docSync: DocSyncStatus.Running, repoId: null);

        // Cutoff well in the past: the just-touched row is not yet abandoned.
        (await ListAsync(staleBefore: DateTimeOffset.UtcNow.AddHours(-1))).Should().BeEmpty();
    }

    [Fact]
    public async Task An_abandoned_running_pass_is_reclaimed()
    {
        // Nothing else ever moves a row out of Running, so without this a worker that died
        // holding the claim would hide the task from documentation permanently.
        var task = await SeedAsync(docSync: DocSyncStatus.Running, repoId: null);

        // Cutoff in the future: the row now reads as untouched for longer than the window.
        (await ListAsync(staleBefore: DateTimeOffset.UtcNow.AddHours(1)))
            .Should().ContainSingle().Which.Id.Should().Be(task.Id);
    }

    [Fact]
    public async Task Work_older_than_the_lookback_window_is_left_alone()
    {
        // This is what stops a project that just enabled documentation from firing a model
        // call for every task it has ever finished.
        await SeedAsync(completedAt: Now.AddDays(-30), repoId: null);

        (await ListAsync()).Should().BeEmpty();
    }

    // TryClaimDocSyncAsync and CompleteDocSyncAsync are single-statement ExecuteUpdateAsync
    // calls, which the InMemory provider cannot translate. Their guard predicates mirror the
    // listing query covered above; the writes themselves are exercised against the real database.
}
