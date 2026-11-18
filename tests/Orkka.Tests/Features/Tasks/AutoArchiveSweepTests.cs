namespace Orkka.Tests.Features.Tasks;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
/// The sweep's decision: an old Done task is archived only when ITS project asked for it.
/// The candidate query is deliberately blind to that (it spans every org), so this gate is
/// the only thing standing between the setting being off and a project's board being cleared.
/// </summary>
public sealed class AutoArchiveSweepTests : IDisposable
{
    private const int AfterDays = 14;
    private const int BatchSize = 100;

    private readonly TasksDbContext _db;
    private readonly Mock<IProjectsApi> _projects = new();
    private readonly Mock<ITaskNotifier> _notifier = new();
    private readonly Dictionary<Ulid, ProjectSummary> _summaries = [];
    private readonly ServiceProvider _provider;
    private readonly TaskAutoArchiveProcessor _processor;

    public AutoArchiveSweepTests()
    {
        var options = new DbContextOptionsBuilder<TasksDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new TasksDbContext(options, currentUser: null, eventBus: null);

        var tasksApi = new TasksApi(
            _db,
            Mock.Of<ITaskNumberService>(),
            _projects.Object,
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
                Mock.Of<IServiceScopeFactory>(),
                NullLogger<TaskSubmissionService>.Instance),
            NullLogger<TasksApi>.Instance);

        _provider = new ServiceCollection()
            .AddScoped<ITasksApi>(_ => tasksApi)
            .AddScoped(_ => _projects.Object)
            .BuildServiceProvider();

        _processor = new TaskAutoArchiveProcessor(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _notifier.Object,
            new ConfigurationBuilder().Build(),
            NullLogger<TaskAutoArchiveProcessor>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _provider.Dispose();
    }

    /// <summary>An old Done task, plus the project row the sweep will ask about.</summary>
    private async Task<TaskEntity> SeedAsync(bool autoArchive, Ulid? projectId = null)
    {
        var id = projectId ?? Ulid.NewUlid();
        var task = new TaskEntity
        {
            Id = Ulid.NewUlid(),
            OrgId = Ulid.NewUlid(),
            ProjectId = id,
            Number = 1,
            Title = "A task",
            CurrentLane = Lane.Done,
            Status = TaskStatus.Completed,
            CompletedAt = DateTimeOffset.UtcNow.AddDays(-20),
        };
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        _summaries[id] = new ProjectSummary(
            id, task.OrgId, "P", "p", "P", new ProjectSettingsDto(1, false, AutoArchiveDoneTasks: autoArchive));
        return task;
    }

    /// <summary>Answers like the real batch read: ids it does not know are simply absent.
    /// <paramref name="hidden"/> drops a seeded project from the answer, standing in for one
    /// deleted between the listing and the read.</summary>
    private void ArrangeProjects(params Ulid[] hidden)
        => _projects
            .Setup(p => p.GetProjectSummariesAsync(
                It.IsAny<IReadOnlyCollection<Ulid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<Ulid> ids, CancellationToken _) =>
                ids.Where(id => _summaries.ContainsKey(id) && !hidden.Contains(id))
                   .ToDictionary(id => id, id => _summaries[id]));

    private Task SweepAsync() => _processor.SweepAsync(AfterDays, BatchSize, default);

    private async Task<bool> IsArchivedAsync(Ulid taskId) =>
        (await _db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId)).IsArchived;

    [Fact]
    public async Task A_task_in_an_opted_in_project_is_archived()
    {
        var task = await SeedAsync(autoArchive: true);
        ArrangeProjects();

        await SweepAsync();

        (await IsArchivedAsync(task.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task A_task_in_a_project_that_did_not_opt_in_is_left_alone()
    {
        var task = await SeedAsync(autoArchive: false);
        ArrangeProjects();

        await SweepAsync();

        (await IsArchivedAsync(task.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task The_setting_is_read_per_project_not_per_sweep()
    {
        // One opted-in project must not drag its neighbours' boards along with it.
        var opted = await SeedAsync(autoArchive: true);
        var untouched = await SeedAsync(autoArchive: false);
        ArrangeProjects();

        await SweepAsync();

        (await IsArchivedAsync(opted.Id)).Should().BeTrue();
        (await IsArchivedAsync(untouched.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task A_task_whose_project_is_gone_is_left_alone()
    {
        // A deleted project is absent from the batch read; defaulting to "archive" there would
        // let a lookup failure quietly clear boards.
        var task = await SeedAsync(autoArchive: true);
        ArrangeProjects(hidden: task.ProjectId);

        await SweepAsync();

        (await IsArchivedAsync(task.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task An_archived_task_notifies_its_board()
    {
        // Board cards live over SignalR; without this the card sits there until a reload.
        var task = await SeedAsync(autoArchive: true);
        ArrangeProjects();

        await SweepAsync();

        _notifier.Verify(n => n.SendTaskUpdatedAsync(
            task.ProjectId,
            It.Is<TaskUpdatedEvent>(e => e.TaskId == task.Id && e.IsArchived == true)),
            Times.Once);
    }

    [Fact]
    public async Task A_sweep_with_no_candidates_asks_nothing_of_the_projects_module()
    {
        // The idle path is the common one — it must not cost a project read per tick.
        ArrangeProjects();

        await SweepAsync();

        _projects.Verify(p => p.GetProjectSummariesAsync(
            It.IsAny<IReadOnlyCollection<Ulid>>(), It.IsAny<CancellationToken>()), Times.Never);
        _notifier.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_failed_notification_does_not_abandon_the_rest_of_the_batch()
    {
        var first = await SeedAsync(autoArchive: true);
        var second = await SeedAsync(autoArchive: true);
        ArrangeProjects();
        _notifier
            .Setup(n => n.SendTaskUpdatedAsync(first.ProjectId, It.IsAny<TaskUpdatedEvent>()))
            .ThrowsAsync(new InvalidOperationException("hub down"));

        await SweepAsync();

        // Both rows are archived regardless — the write already committed.
        (await IsArchivedAsync(first.Id)).Should().BeTrue();
        (await IsArchivedAsync(second.Id)).Should().BeTrue();
        _notifier.Verify(n => n.SendTaskUpdatedAsync(
            second.ProjectId, It.IsAny<TaskUpdatedEvent>()), Times.Once);
    }
}
