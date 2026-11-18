namespace Orkka.Tests.Worker;

using Orkka.Modules.Execution.Orchestration;
using FluentAssertions;

/// <summary>
/// The drain primitive a deploy leans on: in-flight work is counted, "idle" is observed
/// precisely (no missed edge between a release and a wait), and the wait is bounded.
/// </summary>
public sealed class WorkerDrainTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public void Begin_FiresTheTokenOnce_AndIsIdempotent()
    {
        var drain = new WorkerDrain();
        drain.IsDraining.Should().BeFalse();
        drain.Token.IsCancellationRequested.Should().BeFalse();

        drain.Begin();
        drain.Begin();

        drain.IsDraining.Should().BeTrue();
        drain.Token.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task WaitForIdle_NothingInFlight_ReturnsAtOnce()
    {
        var drain = new WorkerDrain();
        (await drain.WaitForIdleAsync(Wait)).Should().BeTrue();
    }

    [Fact]
    public async Task WaitForIdle_ReturnsWhenTheLastRegistrationIsDisposed()
    {
        var drain = new WorkerDrain();
        var a = drain.Track("task A");
        var b = drain.Track("chat turn");
        drain.InFlight.Should().Be(2);
        drain.InFlightDescriptions.Should().BeEquivalentTo(["task A", "chat turn"]);

        var wait = drain.WaitForIdleAsync(Wait);
        a.Dispose();
        await Task.Delay(50);
        wait.IsCompleted.Should().BeFalse("one item is still running");

        b.Dispose();
        (await wait).Should().BeTrue();
        drain.InFlight.Should().Be(0);
    }

    [Fact]
    public async Task WaitForIdle_TimesOut_WhenWorkOutlivesTheWindow()
    {
        var drain = new WorkerDrain();
        using var _ = drain.Track("stuck run");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        (await drain.WaitForIdleAsync(TimeSpan.FromMilliseconds(200))).Should().BeFalse();
        sw.Elapsed.Should().BeLessThan(Wait);
        drain.InFlight.Should().Be(1);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var drain = new WorkerDrain();
        var reg = drain.Track("x");
        reg.Dispose();
        reg.Dispose();
        drain.InFlight.Should().Be(0);
    }

    [Fact]
    public async Task WaitForIdle_DoesNotMissAReleaseRacingTheWait()
    {
        // Many rounds of: register, start waiting, release immediately. A missed edge would
        // show up as a wait that only returns at its timeout.
        var drain = new WorkerDrain();
        for (var round = 0; round < 200; round++)
        {
            var reg = drain.Track($"round {round}");
            var wait = drain.WaitForIdleAsync(Wait);
            reg.Dispose();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            (await wait).Should().BeTrue();
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), $"round {round} must not wait for the timeout");
        }
    }

    [Fact]
    public async Task Track_AfterBegin_IsStillWaitedFor()
    {
        // A chat turn that arrived a moment before the signal is work worth finishing.
        var drain = new WorkerDrain();
        drain.Begin();
        var late = drain.Track("late chat turn");

        var wait = drain.WaitForIdleAsync(Wait);
        await Task.Delay(50);
        wait.IsCompleted.Should().BeFalse();

        late.Dispose();
        (await wait).Should().BeTrue();
    }
}
