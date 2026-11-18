namespace Orkka.Tests.Features.Tasks;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Features.Milestones;
using Orkka.Modules.Tasks.Infrastructure;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// Archiving is a view decision, not a statement about whether the work happened. These counts
/// feed the roadmap's progress bars and the milestone status derivation, so they count archived
/// tasks — otherwise the two-week auto-archive would empty every milestone it touched and the
/// roadmap would report shipped work as never started.
/// </summary>
public sealed class MilestoneArchivedCountsTests : IDisposable
{
    private readonly TasksDbContext _db;
    private readonly Ulid _orgId = Ulid.NewUlid();
    private readonly Ulid _projectId = Ulid.NewUlid();
    private readonly Ulid _milestoneId = Ulid.NewUlid();

    public MilestoneArchivedCountsTests()
    {
        var options = new DbContextOptionsBuilder<TasksDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new TasksDbContext(options, currentUser: null, eventBus: null);
    }

    public void Dispose() => _db.Dispose();

    private async Task SeedAsync(Lane lane, TaskStatus status, bool isArchived, Ulid? milestoneId = null)
    {
        _db.Tasks.Add(new TaskEntity
        {
            Id = Ulid.NewUlid(),
            OrgId = _orgId,
            ProjectId = _projectId,
            MilestoneId = milestoneId ?? _milestoneId,
            Number = 1,
            Title = "A task",
            CurrentLane = lane,
            Status = status,
            IsArchived = isArchived,
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private async Task<MilestoneCountsResponse> CountsAsync()
    {
        var counts = await MilestoneQueries.GetCountsByMilestoneAsync(
            _db, _orgId, _projectId, CancellationToken.None);
        return counts.For(_milestoneId);
    }

    [Fact]
    public async Task An_archived_done_task_still_counts_as_done()
    {
        await SeedAsync(Lane.Done, TaskStatus.Archived, isArchived: true);

        var counts = await CountsAsync();

        counts.Total.Should().Be(1);
        counts.Done.Should().Be(1);
    }

    [Fact]
    public async Task A_fully_archived_milestone_still_reads_as_complete()
    {
        // The regression this guards: every task auto-archived after two weeks used to leave
        // the milestone counting 0 of 0, which derives Planned — a shipped milestone reporting
        // itself as never started.
        await SeedAsync(Lane.Done, TaskStatus.Archived, isArchived: true);
        await SeedAsync(Lane.Done, TaskStatus.Archived, isArchived: true);

        var counts = await CountsAsync();

        counts.Total.Should().Be(2);
        counts.Done.Should().Be(counts.Total);
    }

    [Fact]
    public async Task Archived_and_live_tasks_are_counted_together()
    {
        await SeedAsync(Lane.Done, TaskStatus.Archived, isArchived: true);
        await SeedAsync(Lane.Developing, TaskStatus.Active, isArchived: false);

        var counts = await CountsAsync();

        counts.Total.Should().Be(2);
        counts.Done.Should().Be(1);
        counts.InFlight.Should().Be(1);
    }

    [Fact]
    public async Task Tasks_of_another_milestone_are_not_counted()
    {
        await SeedAsync(Lane.Done, TaskStatus.Archived, isArchived: true, milestoneId: Ulid.NewUlid());

        (await CountsAsync()).Total.Should().Be(0);
    }
}
