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
/// The entry condition for the Done-lane auto-archive sweep, and the write it feeds.
///
/// Archiving is not a cosmetic flag here: the board, the milestone counts, the merge poller and
/// the PR sweep all skip archived tasks. So what this query must NOT return matters more than
/// what it returns — an over-eager candidate is unfinished work quietly removed from the
/// pipeline, two weeks after anybody last looked at it.
/// </summary>
public sealed class AutoArchiveQueryTests : IDisposable
{
    private readonly TasksDbContext _db;
    private readonly TasksApi _api;

    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Cutoff = Now.AddDays(-14);
    private const int BatchSize = 100;

    public AutoArchiveQueryTests()
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
        bool isMerged = false,
        DateTimeOffset? prClosedAt = null,
        DateTimeOffset? completedAt = null,
        bool isArchived = false,
        DateTimeOffset? resetRequestedAt = null,
        int number = 1)
    {
        var task = new TaskEntity
        {
            Id = Ulid.NewUlid(),
            OrgId = Ulid.NewUlid(),
            ProjectId = Ulid.NewUlid(),
            RepoId = repoId,
            Number = number,
            Title = "A task",
            CurrentLane = lane,
            Status = status,
            IsMerged = isMerged,
            PrClosedAt = prClosedAt,
            IsArchived = isArchived,
            ResetRequestedAt = resetRequestedAt,
            CompletedAt = completedAt ?? Now.AddDays(-20),
        };
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return task;
    }

    private Task<IReadOnlyList<AutoArchiveCandidateTask>> ListAsync(int limit = BatchSize) =>
        _api.ListTasksAwaitingAutoArchiveAsync(Cutoff, limit);

    // What the sweep may take ------------------------------------------------

    [Fact]
    public async Task A_repoless_task_done_longer_than_the_window_is_a_candidate()
    {
        var task = await SeedAsync(repoId: null);

        (await ListAsync()).Should().ContainSingle().Which.Id.Should().Be(task.Id);
    }

    [Fact]
    public async Task A_merged_repo_task_is_a_candidate()
    {
        var task = await SeedAsync(repoId: Ulid.NewUlid(), isMerged: true);

        (await ListAsync()).Should().ContainSingle().Which.Id.Should().Be(task.Id);
    }

    [Fact]
    public async Task A_task_whose_pr_was_closed_without_merging_is_a_candidate()
    {
        // Terminal the other way: the merge poller has already given up on a closed PR, so
        // archiving strands nothing.
        var task = await SeedAsync(
            repoId: Ulid.NewUlid(), isMerged: false, prClosedAt: Now.AddDays(-19));

        (await ListAsync()).Should().ContainSingle().Which.Id.Should().Be(task.Id);
    }

    // What it must leave alone -----------------------------------------------

    [Fact]
    public async Task An_unmerged_repo_task_is_not_a_candidate()
    {
        // The load-bearing exclusion: ListTasksAwaitingMergeAsync skips archived tasks, so
        // archiving this one would stop its PR from ever landing.
        await SeedAsync(repoId: Ulid.NewUlid(), isMerged: false);

        (await ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Work_still_inside_the_window_is_not_a_candidate()
    {
        await SeedAsync(completedAt: Now.AddDays(-13));

        (await ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task An_already_archived_task_is_not_a_candidate()
    {
        await SeedAsync(isArchived: true);

        (await ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_task_outside_the_done_lane_is_not_a_candidate()
    {
        await SeedAsync(lane: Lane.CodeReview, status: TaskStatus.WaitingCodeReview);

        (await ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_done_task_that_is_not_completed_is_not_a_candidate()
    {
        await SeedAsync(status: TaskStatus.Blocked);

        (await ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_task_pending_reset_is_not_a_candidate()
    {
        // The user is discarding this task's work; the reset sweep must win the race.
        await SeedAsync(resetRequestedAt: Now);

        (await ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_task_without_a_completion_timestamp_is_not_a_candidate()
    {
        // Nothing to measure two weeks from. Reopening a task clears CompletedAt, so this is
        // reachable in practice, not merely defensive.
        var task = await SeedAsync();
        var tracked = await _db.Tasks.SingleAsync(t => t.Id == task.Id);
        tracked.CompletedAt = null;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        (await ListAsync()).Should().BeEmpty();
    }

    // Batching ---------------------------------------------------------------

    [Fact]
    public async Task The_batch_is_capped_and_drains_oldest_first()
    {
        // A project enabling the setting after years of work must not rewrite every row at
        // once; the oldest-first ordering is what makes successive ticks make progress.
        var oldest = await SeedAsync(completedAt: Now.AddDays(-90), number: 1);
        await SeedAsync(completedAt: Now.AddDays(-60), number: 2);
        await SeedAsync(completedAt: Now.AddDays(-30), number: 3);

        var page = await ListAsync(limit: 2);

        page.Should().HaveCount(2);
        page[0].Id.Should().Be(oldest.Id);
        page[0].CompletedAt.Should().Be(Now.AddDays(-90));
    }

    // The write --------------------------------------------------------------

    [Fact]
    public async Task Archiving_sets_the_same_fields_as_the_archive_endpoint()
    {
        var task = await SeedAsync();

        var archived = await _api.ArchiveTasksAsync([task.Id]);

        archived.Should().ContainSingle().Which.Id.Should().Be(task.Id);
        var stored = await _db.Tasks.AsNoTracking().SingleAsync(t => t.Id == task.Id);
        stored.IsArchived.Should().BeTrue();
        stored.ArchivedAt.Should().NotBeNull();
        stored.Status.Should().Be(TaskStatus.Archived);
        // The lane is deliberately untouched: unarchiving restores the status from it.
        stored.CurrentLane.Should().Be(Lane.Done);
    }

    [Fact]
    public async Task Archiving_re_checks_eligibility_and_skips_what_moved()
    {
        // What makes two replicas sweeping at once — or a task reopened between the listing
        // and the write — a no-op rather than an archive of live work.
        var reopened = await SeedAsync(lane: Lane.Developing, status: TaskStatus.Active);
        var stillDone = await SeedAsync();

        var archived = await _api.ArchiveTasksAsync([reopened.Id, stillDone.Id]);

        archived.Should().ContainSingle().Which.Id.Should().Be(stillDone.Id);
        (await _db.Tasks.AsNoTracking().SingleAsync(t => t.Id == reopened.Id))
            .IsArchived.Should().BeFalse();
    }

    [Fact]
    public async Task Archiving_nothing_is_a_no_op()
    {
        (await _api.ArchiveTasksAsync([])).Should().BeEmpty();
    }
}
