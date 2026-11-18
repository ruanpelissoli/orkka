namespace Orkka.Tests.Features.Tasks;

using FluentAssertions;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Features.Milestones;
using Orkka.Modules.Tasks.Infrastructure.Services;

/// <summary>
/// Which replica takes a queued plan, and what the client is told while it runs.
/// </summary>
public sealed class MilestonePlanClaimTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(20);

    [Fact]
    public void The_replica_holding_the_runner_socket_takes_it_immediately()
    {
        MilestonePlanClaim.ShouldClaimHere(
            canExecuteHere: true, age: TimeSpan.Zero, Grace)
            .Should().BeTrue();
    }

    /// <summary>
    /// Planning runs only on the requester's runner (no platform-key fallback), so a replica
    /// without the socket would fail a plan the socket's holder could serve.
    /// </summary>
    [Fact]
    public void A_replica_without_the_socket_defers_while_the_grace_window_is_open()
    {
        MilestonePlanClaim.ShouldClaimHere(
            canExecuteHere: false,
            age: TimeSpan.FromSeconds(5), Grace)
            .Should().BeFalse();
    }

    /// <summary>
    /// The runner is offline everywhere, so no replica holds the socket. Deferring forever
    /// would leave the job pending until the stale sweep gave up on it — a spinner that never
    /// resolves, which is the exact failure this whole change exists to end.
    /// </summary>
    [Fact]
    public void Once_the_grace_window_closes_any_replica_takes_it()
    {
        MilestonePlanClaim.ShouldClaimHere(
            canExecuteHere: false,
            age: TimeSpan.FromSeconds(30), Grace)
            .Should().BeTrue();
    }

    [Fact]
    public void A_queued_job_reports_itself_as_queued()
    {
        var job = new MilestonePlanJob
        {
            Id = Ulid.NewUlid(),
            ProjectId = Ulid.NewUlid(),
            Goal = "g",
            RequesterUserId = "u",
            Status = MilestonePlanStatus.Pending,
        };

        var evt = MilestonePlanNotifications.From(job);

        evt.Status.Should().Be(nameof(MilestonePlanStatus.Pending));
        evt.Step.Should().Be(MilestonePlanSteps.Queued);
    }

    /// <summary>A finished run carries no step: leaving the last one set would keep a progress
    /// label on screen next to a plan that is already there to read.</summary>
    [Fact]
    public void A_finished_job_reports_no_step()
    {
        var job = new MilestonePlanJob
        {
            Id = Ulid.NewUlid(),
            ProjectId = Ulid.NewUlid(),
            Goal = "g",
            RequesterUserId = "u",
            Status = MilestonePlanStatus.Completed,
            CurrentStep = MilestonePlanSteps.Ordering,
        };

        MilestonePlanNotifications.From(job).Step.Should().BeNull();
    }

    [Fact]
    public void A_failed_job_carries_the_code_the_client_branches_on()
    {
        var job = new MilestonePlanJob
        {
            Id = Ulid.NewUlid(),
            ProjectId = Ulid.NewUlid(),
            Goal = "g",
            RequesterUserId = "u",
            Status = MilestonePlanStatus.Failed,
            FailureCode = MilestonePlanFailureCodes.RunnerOffline,
            ErrorMessage = "Your runner is offline.",
        };

        var evt = MilestonePlanNotifications.From(job);

        evt.Status.Should().Be(nameof(MilestonePlanStatus.Failed));
        evt.FailureCode.Should().Be(MilestonePlanFailureCodes.RunnerOffline);
        evt.ErrorMessage.Should().Be("Your runner is offline.");
    }

    /// <summary>A plan written by one version and read by another must not take the page down;
    /// an unreadable plan is one the user re-runs.</summary>
    [Fact]
    public void An_unreadable_stored_plan_reads_back_as_nothing_rather_than_throwing()
    {
        MilestonePlanSerialization.Deserialize("not json at all").Should().BeNull();
        MilestonePlanSerialization.Deserialize(null).Should().BeNull();
        MilestonePlanSerialization.Deserialize("   ").Should().BeNull();
    }
}
