namespace Orkka.IntegrationTests;

using Orkka.IntegrationTests.Fixtures;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// Closing a PR without merging is not shipping. It used to be recorded as a merge, which moved
/// the task to Done, counted it toward its milestone, and scored the forecast as work that
/// completed — while a plain "don't mark it merged" would have left the merge poller re-hitting
/// GitHub forever, since candidates are selected on (Done AND !IsMerged).
/// </summary>
[Collection("Integration")]
public sealed class PrClosedWithoutMergeTests(OrkkaApiFactory factory)
{
    private static int _nextNumber = 30_000;

    private static async Task<TaskEntity> SeedAwaitingMergeAsync(TasksDbContext db)
    {
        var task = new TaskEntity
        {
            Id = Ulid.NewUlid(),
            OrgId = TestCurrentUser.TestOrgId,
            ProjectId = Ulid.NewUlid(),
            RepoId = Ulid.NewUlid(),
            Number = Interlocked.Increment(ref _nextNumber),
            Title = "Awaiting merge",
            CurrentLane = Lane.Done,
            Status = TaskStatus.Completed,
            PrUrl = "https://github.com/o/r/pull/7",
            CompletedAt = DateTimeOffset.UtcNow,
        };
        db.Tasks.Add(task);
        await db.SaveChangesAsync();
        return task;
    }

    [Fact]
    public async Task ClosingWithoutMerging_DoesNotRecordItAsMerged()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var tasksApi = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        var task = await SeedAwaitingMergeAsync(db);
        await tasksApi.MarkPrClosedAsync(task.Id);

        db.ChangeTracker.Clear();
        var reloaded = await db.Tasks.AsNoTracking().FirstAsync(t => t.Id == task.Id);

        reloaded.IsMerged.Should().BeFalse("closed usually means rejected, not shipped");
        reloaded.PrClosedAt.Should().NotBeNull();
        reloaded.MergeFailureReason.Should().Contain("closed without merging",
            because: "the user needs to know why the task stopped moving");
    }

    [Fact]
    public async Task ClosedTask_IsNoLongerAMergeCandidate()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var tasksApi = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        var task = await SeedAwaitingMergeAsync(db);

        (await tasksApi.ListTasksAwaitingMergeAsync())
            .Should().Contain(c => c.Id == task.Id, "it is Done with an open PR");

        await tasksApi.MarkPrClosedAsync(task.Id);

        // Without the exclusion the poller would re-query GitHub every cycle, forever, for an
        // answer that cannot change.
        (await tasksApi.ListTasksAwaitingMergeAsync())
            .Should().NotContain(c => c.Id == task.Id);
    }

    [Fact]
    public async Task AttachingANewPr_ResumesWatching()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var tasksApi = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        var task = await SeedAwaitingMergeAsync(db);
        await tasksApi.MarkPrClosedAsync(task.Id);

        await tasksApi.SetTaskPrUrlAsync(task.Id, "https://github.com/o/r/pull/8");

        db.ChangeTracker.Clear();
        var reloaded = await db.Tasks.AsNoTracking().FirstAsync(t => t.Id == task.Id);
        reloaded.PrClosedAt.Should().BeNull("a fresh PR is a fresh decision");

        (await tasksApi.ListTasksAwaitingMergeAsync()).Should().Contain(c => c.Id == task.Id);
    }

    [Fact]
    public async Task MarkingClosedTwice_KeepsTheFirstStamp()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var tasksApi = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        var task = await SeedAwaitingMergeAsync(db);
        await tasksApi.MarkPrClosedAsync(task.Id);

        db.ChangeTracker.Clear();
        var first = (await db.Tasks.AsNoTracking().FirstAsync(t => t.Id == task.Id)).PrClosedAt;

        await tasksApi.MarkPrClosedAsync(task.Id);

        db.ChangeTracker.Clear();
        var second = (await db.Tasks.AsNoTracking().FirstAsync(t => t.Id == task.Id)).PrClosedAt;

        second.Should().Be(first, "the sweep is idempotent — a repeat must not move the stamp");
    }

    [Fact]
    public async Task AMergedTask_IsNeverDowngradedToClosed()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var tasksApi = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        var task = await SeedAwaitingMergeAsync(db);
        await tasksApi.MarkTaskMergedAsync(task.Id, "abc123");

        await tasksApi.MarkPrClosedAsync(task.Id);

        db.ChangeTracker.Clear();
        var reloaded = await db.Tasks.AsNoTracking().FirstAsync(t => t.Id == task.Id);

        reloaded.IsMerged.Should().BeTrue();
        reloaded.PrClosedAt.Should().BeNull("merged wins — GitHub reports a merged PR as closed too");
    }
}
