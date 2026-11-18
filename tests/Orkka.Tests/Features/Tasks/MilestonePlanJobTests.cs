namespace Orkka.Tests.Features.Tasks;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Features.Milestones;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.Modules.Tasks.Infrastructure.Services;
using Orkka.SharedKernel.Ai;

/// <summary>
/// The queued half of milestone planning.
///
/// The defect these pin down was not a timeout — nothing in the chain had one short enough to
/// matter. A user watched a spinner for 3m23s, navigated away, and the browser tore down the
/// fetch; the generation was bound to <c>HttpContext.RequestAborted</c>, so minutes of paid
/// model work died with it and came back as a 500. Everything here is about the run outliving
/// whoever asked for it.
/// </summary>
public sealed class MilestonePlanJobTests : IDisposable
{
    private readonly TasksDbContext _db;
    private readonly Mock<ITaskDecomposer> _decomposer = new();
    private readonly Mock<ITaskNotifier> _notifier = new();
    private readonly ServiceProvider _provider;
    private readonly MilestonePlanRunner _runner;

    private static readonly Ulid OrgId = Ulid.NewUlid();
    private static readonly Ulid ProjectId = Ulid.NewUlid();
    private const string UserId = "user_plan";

    public MilestonePlanJobTests()
    {
        var options = new DbContextOptionsBuilder<TasksDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new TasksDbContext(options, currentUser: null, eventBus: null);

        // The runner opens a short-lived scope per progress report so its writes never share a
        // DbContext with the generation in flight. In-memory that scope hands back this same
        // context, which is fine — nothing here exercises the two concurrently.
        _provider = new ServiceCollection()
            .AddScoped(_ => _db)
            .BuildServiceProvider();

        _runner = new MilestonePlanRunner(
            _db,
            _decomposer.Object,
            _notifier.Object,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MilestonePlanRunner>.Instance);
    }

    private async Task<MilestonePlanJob> QueuedJobAsync(Ulid? milestoneId = null)
    {
        var job = new MilestonePlanJob
        {
            Id = Ulid.NewUlid(),
            OrgId = OrgId,
            ProjectId = ProjectId,
            MilestoneId = milestoneId,
            RepoId = Ulid.NewUlid(),
            Goal = "Add checkout",
            RequesterUserId = UserId,
            // Claimed already — the processor flips this before handing the row over.
            Status = MilestonePlanStatus.Running,
            CurrentStep = MilestonePlanSteps.ReadingProject,
            StartedAt = DateTimeOffset.UtcNow,
        };

        _db.MilestonePlanJobs.Add(job);
        await _db.SaveChangesAsync();
        return job;
    }

    private static DecompositionProposal SampleProposal() =>
        new("Checkout", "Users can pay",
            [
                new DecomposedTask("Cart API", "Build the cart endpoints", "High", "Feature"),
                new DecomposedTask("Cart UI", "Wire the cart screen", "Medium", "Feature",
                    DependsOn: [0], Repo: "web"),
            ]);

    [Fact]
    public async Task Completed_plan_is_persisted_so_a_refreshed_client_gets_it()
    {
        var job = await QueuedJobAsync();
        _decomposer
            .Setup(d => d.ProposeAsync(It.IsAny<DecompositionRequest>(),
                It.IsAny<IProgress<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleProposal());

        await _runner.RunAsync(job.Id, CancellationToken.None);

        var saved = await _db.MilestonePlanJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
        saved.Status.Should().Be(MilestonePlanStatus.Completed);
        saved.CompletedAt.Should().NotBeNull();
        // No step on a finished run — a progress label next to a finished plan reads as stuck.
        saved.CurrentStep.Should().BeNull();
        saved.ResultJson.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Persisted_plan_round_trips_through_the_stored_json()
    {
        var job = await QueuedJobAsync();
        _decomposer
            .Setup(d => d.ProposeAsync(It.IsAny<DecompositionRequest>(),
                It.IsAny<IProgress<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleProposal());

        await _runner.RunAsync(job.Id, CancellationToken.None);

        var saved = await _db.MilestonePlanJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
        var plan = MilestonePlanSerialization.Deserialize(saved.ResultJson);

        plan.Should().NotBeNull();
        plan!.MilestoneTitle.Should().Be("Checkout");
        plan.Outcome.Should().Be("Users can pay");
        plan.RepoId.Should().Be(job.RepoId);
        plan.Tasks.Should().HaveCount(2);
        // Keys are positions as planned, and DependsOn references those keys — the property the
        // accept endpoint relies on to survive the user pruning the list.
        plan.Tasks[0].Key.Should().Be(0);
        plan.Tasks[1].Key.Should().Be(1);
        plan.Tasks[1].DependsOn.Should().Equal(0);
        plan.Tasks[1].Repo.Should().Be("web");
    }

    [Fact]
    public async Task A_finished_plan_is_reported_as_completed_to_watchers()
    {
        var job = await QueuedJobAsync();
        _decomposer
            .Setup(d => d.ProposeAsync(It.IsAny<DecompositionRequest>(),
                It.IsAny<IProgress<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleProposal());

        await _runner.RunAsync(job.Id, CancellationToken.None);

        _notifier.Verify(n => n.SendMilestonePlanChangedAsync(
            ProjectId,
            It.Is<MilestonePlanChangedEvent>(e =>
                e.JobId == job.Id
                && e.Status == nameof(MilestonePlanStatus.Completed)
                && e.Step == null
                && e.FailureCode == null)),
            Times.Once);
    }

    [Fact]
    public async Task An_offline_runner_fails_with_its_own_code_and_an_actionable_message()
    {
        var job = await QueuedJobAsync();
        _decomposer
            .Setup(d => d.ProposeAsync(It.IsAny<DecompositionRequest>(),
                It.IsAny<IProgress<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(RunnerUtilityUnavailableException.For("plan this milestone"));

        await _runner.RunAsync(job.Id, CancellationToken.None);

        var saved = await _db.MilestonePlanJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
        saved.Status.Should().Be(MilestonePlanStatus.Failed);
        saved.FailureCode.Should().Be(MilestonePlanFailureCodes.RunnerOffline);
        // The message as-is — not "Planning failed: …", which reads as the goal's fault.
        saved.ErrorMessage.Should().Be(
            "Your runner is offline or unavailable — run `./orkka start` to plan this milestone.");
    }

    [Fact]
    public async Task An_unusable_reply_asks_the_user_to_retry_rather_than_blaming_the_goal()
    {
        var job = await QueuedJobAsync();
        _decomposer
            .Setup(d => d.ProposeAsync(It.IsAny<DecompositionRequest>(),
                It.IsAny<IProgress<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ModelOutputException("The plan was cut off.", "{\"tasks\":["));

        await _runner.RunAsync(job.Id, CancellationToken.None);

        var saved = await _db.MilestonePlanJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
        saved.Status.Should().Be(MilestonePlanStatus.Failed);
        saved.FailureCode.Should().Be(MilestonePlanFailureCodes.Unusable);
        saved.ErrorMessage.Should().Contain("Try again");
        // The raw model output is for the log, never for the user.
        saved.ErrorMessage.Should().NotContain("{\"tasks\"");
    }

    [Fact]
    public async Task An_unexpected_failure_reports_the_root_cause_not_the_wrapper()
    {
        var job = await QueuedJobAsync();
        _decomposer
            .Setup(d => d.ProposeAsync(It.IsAny<DecompositionRequest>(),
                It.IsAny<IProgress<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(
                "outer wrapper", new HttpRequestException("connection refused")));

        await _runner.RunAsync(job.Id, CancellationToken.None);

        var saved = await _db.MilestonePlanJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
        saved.Status.Should().Be(MilestonePlanStatus.Failed);
        saved.FailureCode.Should().Be(MilestonePlanFailureCodes.Error);
        saved.ErrorMessage.Should().Contain("connection refused");
    }

    [Fact]
    public async Task A_very_long_cause_is_truncated_before_it_reaches_the_user()
    {
        var job = await QueuedJobAsync();
        _decomposer
            .Setup(d => d.ProposeAsync(It.IsAny<DecompositionRequest>(),
                It.IsAny<IProgress<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(new string('x', 5000)));

        await _runner.RunAsync(job.Id, CancellationToken.None);

        var saved = await _db.MilestonePlanJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
        saved.ErrorMessage!.Length.Should().BeLessThan(400);
    }

    /// <summary>
    /// The distinction the old code could not make. A cancelled generation used to mean "the
    /// user left" and surfaced as a failure; now the only thing that cancels a run is the host
    /// draining, and that must leave the row for another replica rather than telling the user
    /// their plan failed because a pod was recycled.
    /// </summary>
    [Fact]
    public async Task Shutdown_leaves_the_job_running_for_the_stale_sweep_to_requeue()
    {
        var job = await QueuedJobAsync();
        using var shuttingDown = new CancellationTokenSource();
        await shuttingDown.CancelAsync();

        _decomposer
            .Setup(d => d.ProposeAsync(It.IsAny<DecompositionRequest>(),
                It.IsAny<IProgress<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var act = () => _runner.RunAsync(job.Id, shuttingDown.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        var saved = await _db.MilestonePlanJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
        saved.Status.Should().Be(MilestonePlanStatus.Running);
        saved.FailureCode.Should().BeNull();
        _notifier.Verify(n => n.SendMilestonePlanChangedAsync(
            It.IsAny<Ulid>(), It.IsAny<MilestonePlanChangedEvent>()), Times.Never);
    }

    /// <summary>
    /// The requester's own details reach the decomposer unchanged. If they did not, the call
    /// would route to the platform key instead of their runner and the spend would be
    /// attributed to nobody.
    /// </summary>
    [Fact]
    public async Task The_run_carries_the_requesters_identity_and_the_stored_goal()
    {
        var milestoneId = Ulid.NewUlid();
        var job = await QueuedJobAsync(milestoneId);
        DecompositionRequest? seen = null;

        _decomposer
            .Setup(d => d.ProposeAsync(It.IsAny<DecompositionRequest>(),
                It.IsAny<IProgress<string>>(), It.IsAny<CancellationToken>()))
            .Callback<DecompositionRequest, IProgress<string>?, CancellationToken>(
                (r, _, _) => seen = r)
            .ReturnsAsync(SampleProposal());

        await _runner.RunAsync(job.Id, CancellationToken.None);

        seen.Should().NotBeNull();
        seen!.OrgId.Should().Be(OrgId);
        seen.ProjectId.Should().Be(ProjectId);
        seen.MilestoneId.Should().Be(milestoneId);
        seen.RepoId.Should().Be(job.RepoId);
        // The prose the endpoint derived at enqueue time, not something re-derived later — a
        // milestone renamed in between must not change what was asked for.
        seen.Goal.Should().Be("Add checkout");
        seen.RequesterUserId.Should().Be(UserId);
    }

    [Fact]
    public async Task A_job_deleted_before_it_runs_is_a_no_op()
    {
        var act = () => _runner.RunAsync(Ulid.NewUlid(), CancellationToken.None);

        await act.Should().NotThrowAsync();
        _decomposer.Verify(d => d.ProposeAsync(It.IsAny<DecompositionRequest>(),
            It.IsAny<IProgress<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    public void Dispose()
    {
        _db.Dispose();
        _provider.Dispose();
    }
}
