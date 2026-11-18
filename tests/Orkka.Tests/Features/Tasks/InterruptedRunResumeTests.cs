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
/// A Developing run that dies mid-flight — transport drop, runner disconnect, worker
/// restart — used to take its CLI session down with it: the session id was written only by
/// the completion path, and the resume lookup only considered completed executions. Phase
/// re-entry (which is the crash-recovery model) therefore always started cold, so the agent
/// re-read the plan, re-explored the repo and redid work that was already sitting in its
/// worktree. These cover the session surviving the failure and being labelled as interrupted.
/// </summary>
public sealed class InterruptedRunResumeTests : IDisposable
{
    private readonly TasksDbContext _db;
    private readonly TasksApi _api;

    public InterruptedRunResumeTests()
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

    private async Task<TaskEntity> SeedTaskAsync()
    {
        var task = new TaskEntity
        {
            Id = Ulid.NewUlid(),
            OrgId = Ulid.NewUlid(),
            ProjectId = Ulid.NewUlid(),
            Number = 1,
            Title = "Task under test",
            CurrentLane = Lane.Developing,
            Status = TaskStatus.Active,
            HasAgentWorking = true,
        };
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();
        return task;
    }

    private async Task<Ulid> SeedRunningExecutionAsync(Ulid taskId, Lane lane = Lane.Developing)
    {
        var execution = new TaskExecution
        {
            Id = Ulid.NewUlid(),
            OrgId = Ulid.NewUlid(),
            TaskId = taskId,
            Lane = lane,
            Status = ExecutionStatus.Running,
            StartedAt = DateTimeOffset.UtcNow,
        };
        _db.TaskExecutions.Add(execution);
        await _db.SaveChangesAsync();
        return execution.Id;
    }

    [Fact]
    public async Task AFailedRun_KeepsItsSession_SoTheNextPassCanResumeIt()
    {
        var task = await SeedTaskAsync();
        var executionId = await SeedRunningExecutionAsync(task.Id);

        await _api.FailExecutionAsync(
            task.Id, executionId, "Connection lost mid-response",
            promptTokens: 120, completionTokens: 40, sessionId: "sess-abc");

        var stored = await _db.TaskExecutions.SingleAsync(e => e.Id == executionId);
        stored.SessionId.Should().Be("sess-abc");

        var resume = await _api.GetLatestExecutionSessionAsync(task.Id, Lane.Developing);
        resume.Should().NotBeNull();
        resume!.SessionId.Should().Be("sess-abc");
        resume.Interrupted.Should().BeTrue("the run was cut off rather than finishing");
    }

    [Fact]
    public async Task ARunParkedForALostRunner_KeepsItsSessionToo()
    {
        var task = await SeedTaskAsync();
        var executionId = await SeedRunningExecutionAsync(task.Id);

        await _api.ParkExecutionWaitingForRunnerAsync(
            task.Id, executionId, "Your runner disconnected mid-run.",
            promptTokens: 10, completionTokens: 5, sessionId: "sess-runner");

        var reloaded = await _db.Tasks.SingleAsync(t => t.Id == task.Id);
        reloaded.Status.Should().Be(TaskStatus.WaitingForRunner);

        var resume = await _api.GetLatestExecutionSessionAsync(task.Id, Lane.Developing);
        resume!.SessionId.Should().Be("sess-runner");
        resume.Interrupted.Should().BeTrue();
    }

    [Fact]
    public async Task ACompletedRun_IsNotFlaggedAsInterrupted()
    {
        var task = await SeedTaskAsync();
        var executionId = await SeedRunningExecutionAsync(task.Id);

        await _api.CompleteExecutionAsync(
            task.Id, executionId, promptTokens: 100, completionTokens: 50,
            agentLog: "done", sessionId: "sess-done");

        var resume = await _api.GetLatestExecutionSessionAsync(task.Id, Lane.Developing);
        resume!.SessionId.Should().Be("sess-done");
        resume.Interrupted.Should().BeFalse();
    }

    [Fact]
    public async Task TheMostRecentSessionWins_EvenWhenTheFailureCameLast()
    {
        var task = await SeedTaskAsync();

        var first = await SeedRunningExecutionAsync(task.Id);
        await _api.CompleteExecutionAsync(task.Id, first, 10, 10, sessionId: "sess-old");

        var second = await SeedRunningExecutionAsync(task.Id);
        await _api.FailExecutionAsync(task.Id, second, "socket hang up", sessionId: "sess-new");

        var resume = await _api.GetLatestExecutionSessionAsync(task.Id, Lane.Developing);
        resume!.SessionId.Should().Be("sess-new");
        resume.Interrupted.Should().BeTrue();
    }

    [Fact]
    public async Task ARunningExecution_IsNeverOfferedForResume()
    {
        var task = await SeedTaskAsync();
        var execution = new TaskExecution
        {
            Id = Ulid.NewUlid(),
            OrgId = Ulid.NewUlid(),
            TaskId = task.Id,
            Lane = Lane.Developing,
            Status = ExecutionStatus.Running,
            SessionId = "sess-live",
            StartedAt = DateTimeOffset.UtcNow,
        };
        _db.TaskExecutions.Add(execution);
        await _db.SaveChangesAsync();

        var resume = await _api.GetLatestExecutionSessionAsync(task.Id, Lane.Developing);
        resume.Should().BeNull("that session belongs to a run that is still going");
    }

    [Fact]
    public async Task AFailureWithNoSession_OffersNothingToResume()
    {
        var task = await SeedTaskAsync();
        var executionId = await SeedRunningExecutionAsync(task.Id);

        await _api.FailExecutionAsync(task.Id, executionId, "the CLI exited unexpectedly");

        var resume = await _api.GetLatestExecutionSessionAsync(task.Id, Lane.Developing);
        resume.Should().BeNull();
    }

    [Fact]
    public async Task SessionsDoNotLeakAcrossLanes()
    {
        var task = await SeedTaskAsync();
        var qaExecution = await SeedRunningExecutionAsync(task.Id, Lane.Testing);

        await _api.FailExecutionAsync(task.Id, qaExecution, "fetch failed", sessionId: "sess-qa");

        var resume = await _api.GetLatestExecutionSessionAsync(task.Id, Lane.Developing);
        resume.Should().BeNull();
    }
}
