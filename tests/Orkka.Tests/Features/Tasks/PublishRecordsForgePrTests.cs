namespace Orkka.Tests.Features.Tasks;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.Modules.Tasks.Infrastructure.Services;
using Orkka.Modules.Tasks.PublicApi;
using Orkka.SharedKernel.Utilities;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// The developer's Merge click on a remote repository publishes the work as a forge pull
/// request. The task must carry that pull request from the same save that marks it merged —
/// "merged with no forge PR" is how a folder landing reads, and the tracker write-back moved the
/// ticket straight to its "Merged" status (Done) while the PR had only just been opened.
/// </summary>
public sealed class PublishRecordsForgePrTests : IDisposable
{
    private const string ForgeUrl = "https://github.com/acme/api/pull/7";

    private readonly TasksDbContext _db;
    private readonly Mock<IProjectsApi> _projects = new();
    private readonly Mock<IPullRequestService> _pullRequests = new();
    private readonly Ulid _orgId = Ulid.NewUlid();
    private readonly Ulid _repoId = Ulid.NewUlid();

    public PublishRecordsForgePrTests()
    {
        var options = new DbContextOptionsBuilder<TasksDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new TasksDbContext(options, currentUser: null, eventBus: null);
        _projects.Setup(p => p.GetRepoAsync(_repoId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepoSummary(_repoId, Ulid.NewUlid(), _orgId, "api", "https://github.com/acme/api.git",
                "main", "#000000", WorkspaceStatus.Ready));
    }

    public void Dispose() => _db.Dispose();

    private BoardOperations Board() => new(
        _db, _projects.Object, _pullRequests.Object, Mock.Of<ITaskNotifier>(),
        Mock.Of<Orkka.SharedKernel.Events.IEventBus>(),
        new TaskSubmissionService(_db, Mock.Of<ITaskNotifier>(),
            new MilestoneProgressUpdater(_db, Mock.Of<ITaskNotifier>(), Mock.Of<Orkka.SharedKernel.Events.IEventBus>(),
                NullLogger<MilestoneProgressUpdater>.Instance),
            Mock.Of<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            NullLogger<TaskSubmissionService>.Instance),
        Mock.Of<Orkka.Modules.Agents.Contracts.IProviderReadiness>(),
        Mock.Of<ITaskNumberService>(),
        NullLogger<BoardOperations>.Instance);

    private TasksApi Api() => new(
        _db, Mock.Of<ITaskNumberService>(), _projects.Object, Mock.Of<ITaskNotifier>(),
        Mock.Of<Orkka.SharedKernel.Events.IEventBus>(),
        new TaskSubmissionService(_db, Mock.Of<ITaskNotifier>(),
            new MilestoneProgressUpdater(_db, Mock.Of<ITaskNotifier>(), Mock.Of<Orkka.SharedKernel.Events.IEventBus>(),
                NullLogger<MilestoneProgressUpdater>.Instance),
            Mock.Of<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            NullLogger<TaskSubmissionService>.Instance),
        NullLogger<TasksApi>.Instance);

    private async Task<TaskEntity> SeedReviewedTaskAsync()
    {
        var task = new TaskEntity
        {
            Id = Ulid.NewUlid(),
            OrgId = _orgId,
            ProjectId = Ulid.NewUlid(),
            RepoId = _repoId,
            Number = 1,
            Title = "[SHOP-11] Add add(a, b)",
            CurrentLane = Lane.CodeReview,
            Status = TaskStatus.WaitingCodeReview,
            PrUrl = "orkka-local://pull/1",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        };
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();
        return task;
    }

    [Fact]
    public async Task A_publish_click_records_the_forge_pull_request_so_the_ticket_reads_PR_opened_not_merged()
    {
        var task = await SeedReviewedTaskAsync();
        _pullRequests.Setup(p => p.MergeAsync(_repoId, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<GitHubPullRequest>.Success(
                new GitHubPullRequest(1, 1, "orkka-local://pull/1", "closed", "abc123", ForgeUrl)));

        var outcome = await Board().MergePullRequestAsync(_orgId, task.Id, CancellationToken.None);

        outcome.Ok.Should().BeTrue();
        var state = (await Api().GetTrackerTaskStatesAsync([task.Id])).Single();
        state.PublishedPrUrl.Should().Be(ForgeUrl);
        state.LandedInFolder.Should().BeFalse("a published remote is not a folder landing");
    }

    [Fact]
    public async Task A_folder_landing_still_reads_as_landed()
    {
        var task = await SeedReviewedTaskAsync();
        _pullRequests.Setup(p => p.MergeAsync(_repoId, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<GitHubPullRequest>.Success(
                new GitHubPullRequest(1, 1, "orkka-local://pull/1", "closed", "abc123")));

        (await Board().MergePullRequestAsync(_orgId, task.Id, CancellationToken.None)).Ok.Should().BeTrue();

        var state = (await Api().GetTrackerTaskStatesAsync([task.Id])).Single();
        state.PublishedPrUrl.Should().BeNull();
        state.LandedInFolder.Should().BeTrue();
    }
}
