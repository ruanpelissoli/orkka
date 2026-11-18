namespace Orkka.Tests.Features.Tasks;

using FluentAssertions;
using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Features.Verification;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.Modules.Tasks.Infrastructure.Services;
using Orkka.Modules.Tasks.PublicApi;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

public sealed class VerificationTests
{
    // --- SubmitVerificationValidator ---

    private readonly SubmitVerificationValidator _validator = new();

    [Theory]
    [InlineData("approve")]
    [InlineData("request_changes")]
    [InlineData("APPROVE")]
    [InlineData("Request_Changes")]
    public void SubmitVerification_ValidDecisions_Pass(string decision)
    {
        var request = new SubmitVerificationRequest(decision, "feedback");
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(x => x.Decision);
    }

    [Theory]
    [InlineData("")]
    [InlineData("comment")]
    [InlineData("reject")]
    public void SubmitVerification_InvalidDecisions_Fail(string decision)
    {
        var request = new SubmitVerificationRequest(decision, "feedback");
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Decision);
    }

    [Fact]
    public void SubmitVerification_RequestChangesWithoutFeedback_Fails()
    {
        var request = new SubmitVerificationRequest("request_changes", null);
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Feedback);
    }

    [Fact]
    public void SubmitVerification_ApproveWithoutFeedback_Passes()
    {
        var request = new SubmitVerificationRequest("approve", null);
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void SubmitVerification_FeedbackTooLong_Fails()
    {
        var request = new SubmitVerificationRequest("approve", new string('a', 10001));
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Feedback);
    }
}

/// <summary>
/// Worker-side lane/status transitions behind the manual verification park —
/// developing-phase routing precedence and the QA-pass park.
/// </summary>
public sealed class VerificationTransitionTests : IDisposable
{
    private readonly TasksDbContext _db;
    private readonly TasksApi _api;

    public VerificationTransitionTests()
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
            // Real instances rather than mocks: both are concrete services, and nothing in
            // these lane-transition tests reaches submission. Constructing them is cheaper
            // than introducing an interface only the tests would use.
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

    private async Task<TaskEntity> SeedTaskAsync(Lane lane, int qaCycleCount = 0)
    {
        var task = new TaskEntity
        {
            Id = Ulid.NewUlid(),
            OrgId = Ulid.NewUlid(),
            ProjectId = Ulid.NewUlid(),
            Number = 1,
            Title = "Task under test",
            CurrentLane = lane,
            Status = TaskStatus.Active,
            HasAgentWorking = true,
            QaCycleCount = qaCycleCount,
        };
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();
        return task;
    }

    [Fact]
    public async Task CompleteDeveloping_RouteToTesting_WinsOverPark()
    {
        var task = await SeedTaskAsync(Lane.Developing);

        var transition = await _api.CompleteDevelopingPhaseAsync(
            task.Id, skipCodeReview: false, routeToTesting: true, parkForUserVerification: true);

        transition!.NewLane.Should().Be(Lane.Testing);
        transition.NewStatus.Should().Be(TaskStatus.InQueue);
    }

    [Fact]
    public async Task CompleteDeveloping_Park_LandsInWaitingUserVerification()
    {
        var task = await SeedTaskAsync(Lane.Developing);

        var transition = await _api.CompleteDevelopingPhaseAsync(
            task.Id, skipCodeReview: false, routeToTesting: false, parkForUserVerification: true);

        transition!.NewLane.Should().Be(Lane.Testing);
        transition.NewStatus.Should().Be(TaskStatus.WaitingUserVerification);
        task.HasAgentWorking.Should().BeFalse();
    }

    [Fact]
    public async Task CompleteDeveloping_ParkWinsOverSkipCodeReview()
    {
        // A SkipCodeReview task still parks for verification; approval then completes it.
        var task = await SeedTaskAsync(Lane.Developing);

        var transition = await _api.CompleteDevelopingPhaseAsync(
            task.Id, skipCodeReview: true, routeToTesting: false, parkForUserVerification: true);

        transition!.NewLane.Should().Be(Lane.Testing);
        transition.NewStatus.Should().Be(TaskStatus.WaitingUserVerification);
        task.CompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task CompleteDeveloping_NoPark_SkipCodeReviewCompletes()
    {
        var task = await SeedTaskAsync(Lane.Developing);

        var transition = await _api.CompleteDevelopingPhaseAsync(
            task.Id, skipCodeReview: true, routeToTesting: false, parkForUserVerification: false);

        transition!.NewLane.Should().Be(Lane.Done);
        transition.NewStatus.Should().Be(TaskStatus.Completed);
        task.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task CompleteDeveloping_Default_RoutesToCodeReview()
    {
        var task = await SeedTaskAsync(Lane.Developing);

        var transition = await _api.CompleteDevelopingPhaseAsync(
            task.Id, skipCodeReview: false, routeToTesting: false, parkForUserVerification: false);

        transition!.NewLane.Should().Be(Lane.CodeReview);
        transition.NewStatus.Should().Be(TaskStatus.InQueue);
    }

    [Fact]
    public async Task QaVerdict_PassWithPark_LandsInWaitingUserVerification()
    {
        var task = await SeedTaskAsync(Lane.Testing);

        var transition = await _api.ApplyQaVerdictAsync(
            task.Id, passed: true, maxQaCycles: 2, parkForUserVerification: true);

        transition!.NewLane.Should().Be(Lane.Testing);
        transition.NewStatus.Should().Be(TaskStatus.WaitingUserVerification);
        transition.EscalatedToHuman.Should().BeFalse();
    }

    [Fact]
    public async Task QaVerdict_PassWithoutPark_RoutesToCodeReview()
    {
        var task = await SeedTaskAsync(Lane.Testing);

        var transition = await _api.ApplyQaVerdictAsync(
            task.Id, passed: true, maxQaCycles: 2, parkForUserVerification: false);

        transition!.NewLane.Should().Be(Lane.CodeReview);
        transition.NewStatus.Should().Be(TaskStatus.InQueue);
    }

    [Fact]
    public async Task QaVerdict_FailWithPark_StillRetriesDev()
    {
        // The park flag only affects the pass branch — QA failures keep their retry loop.
        var task = await SeedTaskAsync(Lane.Testing);

        var transition = await _api.ApplyQaVerdictAsync(
            task.Id, passed: false, maxQaCycles: 2, parkForUserVerification: true);

        transition!.NewLane.Should().Be(Lane.Developing);
        transition.NewStatus.Should().Be(TaskStatus.InQueue);
        transition.QaCycleCount.Should().Be(1);
    }

    [Fact]
    public async Task CheckFailure_UnderCap_RequeuesForDeveloping()
    {
        // First failed lint/typecheck/test run: back to the dev agent with the output.
        var task = await SeedTaskAsync(Lane.Developing);

        var transition = await _api.ApplyCheckFailureAsync(task.Id, maxCheckCycles: 2);

        transition!.FromLane.Should().Be(Lane.Developing);
        transition.NewLane.Should().Be(Lane.Developing);
        transition.NewStatus.Should().Be(TaskStatus.InQueue);
        transition.CheckCycleCount.Should().Be(1);
        transition.EscalatedToHuman.Should().BeFalse();
        task.CheckCycleCount.Should().Be(1);
        task.HasAgentWorking.Should().BeFalse();
    }

    [Fact]
    public async Task CheckFailure_AtCap_ParksForUserVerification()
    {
        // Budget spent: park in the status a chat reply resumes (same shape as no-commit
        // escalation), not WaitingCodeReview/WaitingQaReview which have nothing to point at.
        var task = await SeedTaskAsync(Lane.Developing);
        task.CheckCycleCount = 1;
        await _db.SaveChangesAsync();

        var transition = await _api.ApplyCheckFailureAsync(task.Id, maxCheckCycles: 2);

        transition!.NewLane.Should().Be(Lane.Developing);
        transition.NewStatus.Should().Be(TaskStatus.WaitingUserVerification);
        transition.CheckCycleCount.Should().Be(2);
        transition.EscalatedToHuman.Should().BeTrue();
        task.HasAgentWorking.Should().BeFalse();
    }

    [Fact]
    public async Task CheckFailure_UnknownTask_ReturnsNull()
    {
        var transition = await _api.ApplyCheckFailureAsync(Ulid.NewUlid(), maxCheckCycles: 2);

        transition.Should().BeNull();
    }

    [Fact]
    public async Task SetWaitingUserVerification_SetsLaneAndStatus()
    {
        // Callable from any lane (defensive park for manual-only projects).
        var task = await SeedTaskAsync(Lane.Developing);

        await _api.SetTaskWaitingUserVerificationAsync(task.Id);

        task.CurrentLane.Should().Be(Lane.Testing);
        task.Status.Should().Be(TaskStatus.WaitingUserVerification);
        task.HasAgentWorking.Should().BeFalse();
    }
}
