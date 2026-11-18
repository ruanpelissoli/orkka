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
/// Submission opens the dependency-analysis gate, and only the analysis (or the split) closes
/// it. A Story member gets neither, so the gate must close at once — it stayed open, and the
/// queue held every member of every Story (the first included) for the whole 5-minute window.
/// </summary>
public sealed class StoryMemberSubmissionGateTests : IDisposable
{
    private readonly TasksDbContext _db;
    private readonly TaskSubmissionService _submissions;
    private readonly TasksApi _api;
    private readonly Ulid _orgId = Ulid.NewUlid();
    private readonly Ulid _projectId = Ulid.NewUlid();

    public StoryMemberSubmissionGateTests()
    {
        var options = new DbContextOptionsBuilder<TasksDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new TasksDbContext(options, currentUser: null, eventBus: null);
        _submissions = new TaskSubmissionService(_db, Mock.Of<ITaskNotifier>(),
            new MilestoneProgressUpdater(_db, Mock.Of<ITaskNotifier>(), Mock.Of<Orkka.SharedKernel.Events.IEventBus>(),
                NullLogger<MilestoneProgressUpdater>.Instance),
            Mock.Of<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            NullLogger<TaskSubmissionService>.Instance);
        _api = new TasksApi(_db, Mock.Of<ITaskNumberService>(), Mock.Of<IProjectsApi>(), Mock.Of<ITaskNotifier>(),
            Mock.Of<Orkka.SharedKernel.Events.IEventBus>(), _submissions, NullLogger<TasksApi>.Instance);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_submitted_story_member_is_not_held_by_the_dependency_analysis_gate()
    {
        var story = new TaskGroup
        {
            Id = Ulid.NewUlid(),
            OrgId = _orgId,
            ProjectId = _projectId,
            Title = "SHOP-20",
            Kind = TaskGroupKind.Story,
            BranchName = "feature/SHOP-20-math-helpers",
        };
        _db.TaskGroups.Add(story);
        var member = new TaskEntity
        {
            Id = Ulid.NewUlid(),
            OrgId = _orgId,
            ProjectId = _projectId,
            RepoId = Ulid.NewUlid(),
            GroupId = story.Id,
            Number = 1,
            Title = "[SHOP-21] Add div(a, b)",
            CurrentLane = Lane.Drafting,
            Status = TaskStatus.Draft,
        };
        _db.Tasks.Add(member);
        await _db.SaveChangesAsync();

        await _submissions.SubmitAsync(member, null, CancellationToken.None);

        (await _db.Tasks.AsNoTracking().SingleAsync(t => t.Id == member.Id)).DependencyAnalysisStartedAt.Should().BeNull();
        (await _api.GetTasksWithUnmetDependenciesAsync([member.Id])).Should().BeEmpty(
            "the first sub-task of a Story has nothing to wait for");
    }
}
