namespace Orkka.Tests.Services;

using System.Runtime.CompilerServices;
using Orkka.LocalRunner;
using Orkka.LocalRunner.Contracts;
using FluentAssertions;

/// <summary>
/// The runner's book of in-flight runs: what CancelRun / CancelTaskRuns act on and what
/// CleanupWorktree waits for. A run must stay active for the life of its chunk stream and
/// be addressable by task, and cancelling something the runner does not know about must
/// be a no-op — a host replica re-announces stale ids on every reconnect.
/// </summary>
public sealed class RunnerRunRegistryTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public void Cancel_UnknownOrFinishedRun_IsNoOp()
    {
        var registry = new RunnerRunRegistry();

        registry.Cancel("never-started").Should().BeFalse();

        registry.TryStart("run-1", "task-1", out _).Should().BeTrue();
        registry.Complete("run-1");
        registry.Cancel("run-1").Should().BeFalse("a finished run has nothing left to cancel");
        registry.ActiveRunIds.Should().BeEmpty();
    }

    [Fact]
    public void TryStart_DuplicateId_IsRejected()
    {
        var registry = new RunnerRunRegistry();

        registry.TryStart("run-1", null, out _).Should().BeTrue();
        registry.TryStart("run-1", null, out _).Should().BeFalse();
        registry.Count.Should().Be(1);
    }

    [Fact]
    public async Task Track_KeepsRunActiveUntilTheProducerIsFullyConsumed()
    {
        var registry = new RunnerRunRegistry();
        registry.TryStart("run-1", "task-1", out var run);

        var seen = 0;
        await foreach (var _ in registry.Track(run, Chunks(3)))
        {
            seen++;
            registry.ActiveRunIds.Should().Contain("run-1", "the run is live while chunks still flow");
            run.Completed.IsCompleted.Should().BeFalse();
        }

        seen.Should().Be(3);
        registry.ActiveRunIds.Should().BeEmpty();
        run.Completed.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task Track_WhenTheConsumerDisposesEarly_CompletesTheRun()
    {
        // This is what SignalR does to the upload stream when the connection drops or the
        // send's token is cancelled — the registry must learn of the end from that teardown.
        var registry = new RunnerRunRegistry();
        registry.TryStart("run-1", "task-1", out var run);

        var enumerator = registry.Track(run, Chunks(100)).GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).Should().BeTrue();
        registry.ActiveRunIds.Should().Contain("run-1");

        await enumerator.DisposeAsync();

        registry.ActiveRunIds.Should().BeEmpty();
        run.Completed.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task CancelTaskRunsAsync_CancelsOnlyThatTasksRuns_AndWaitsForTheirTeardown()
    {
        var registry = new RunnerRunRegistry();
        var aTornDown = 0;

        // Two agent runs of task A, one chat run (no task), one run of task B — all blocked
        // until cancelled, each recording its own teardown.
        var a1 = StartBlocked(registry, "a-1", "task-A", () => Interlocked.Increment(ref aTornDown));
        var a2 = StartBlocked(registry, "a-2", "task-A", () => Interlocked.Increment(ref aTornDown));
        var chat = StartBlocked(registry, "chat-1", null, () => { });
        var b = StartBlocked(registry, "b-1", "task-B", () => { });

        var cancelled = await registry.CancelTaskRunsAsync("task-A", Wait);

        cancelled.Should().Be(2);
        aTornDown.Should().Be(2, "CancelTaskRuns returns only after the cancelled producers have torn down");
        a1.Completed.IsCompleted.Should().BeTrue();
        a2.Completed.IsCompleted.Should().BeTrue();
        chat.Completed.IsCompleted.Should().BeFalse();
        b.Completed.IsCompleted.Should().BeFalse();
        registry.ActiveRunIds.Should().BeEquivalentTo(["chat-1", "b-1"]);

        // Clean up the survivors.
        registry.Cancel("chat-1");
        registry.Cancel("b-1");
        await Task.WhenAll(chat.Completed, b.Completed).WaitAsync(Wait);
    }

    [Fact]
    public async Task CancelAllAsync_CancelsEveryRun_AndWaitsForTheirTeardown()
    {
        // The runner is stopping: agent runs, chat runs, everything goes — and only after the
        // producers tore down (the agent processes exited) does the stop carry on.
        var registry = new RunnerRunRegistry();
        var tornDown = 0;
        var a = StartBlocked(registry, "a-1", "task-A", () => Interlocked.Increment(ref tornDown));
        var chat = StartBlocked(registry, "chat-1", null, () => Interlocked.Increment(ref tornDown));
        var b = StartBlocked(registry, "b-1", "task-B", () => Interlocked.Increment(ref tornDown));

        var cancelled = await registry.CancelAllAsync(Wait);

        cancelled.Should().Be(3);
        tornDown.Should().Be(3);
        new[] { a, chat, b }.Should().OnlyContain(r => r.Completed.IsCompleted);
        registry.ActiveRunIds.Should().BeEmpty();
        (await registry.CancelAllAsync(Wait)).Should().Be(0, "nothing is left in flight");
    }

    [Fact]
    public async Task CancelTaskRunsAsync_NothingInFlight_ReturnsZero()
    {
        var registry = new RunnerRunRegistry();
        (await registry.CancelTaskRunsAsync("task-x", Wait)).Should().Be(0);
    }

    [Fact]
    public async Task CancelTaskRunsAsync_ProducerIgnoresCancellation_ReturnsAfterTheWaitWindow()
    {
        // A wedged agent process must not wedge the caller: the count still comes back and
        // the (lock-tolerant) worktree delete is the next line of defence.
        var registry = new RunnerRunRegistry();
        registry.TryStart("stuck", "task-A", out var run);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pump = Task.Run(async () =>
        {
            await foreach (var _ in registry.Track(run, IgnoresCancellation(release.Task))) { }
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var cancelled = await registry.CancelTaskRunsAsync("task-A", TimeSpan.FromMilliseconds(200));

        cancelled.Should().Be(1);
        sw.Elapsed.Should().BeLessThan(Wait);
        registry.ActiveRunIds.Should().Contain("stuck", "the registry does not lie about a run that is still there");

        // Release it.
        release.SetResult();
        await pump.WaitAsync(Wait);
        registry.ActiveRunIds.Should().BeEmpty();
    }

    private static RunnerRunRegistry.ActiveRun StartBlocked(
        RunnerRunRegistry registry, string runId, string? taskId, Action onTeardown)
    {
        registry.TryStart(runId, taskId, out var run).Should().BeTrue();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in registry.Track(run, BlockUntilCancelled(run.Cts.Token, onTeardown)))
                    started.TrySetResult();
            }
            catch (OperationCanceledException) { }
            finally { started.TrySetResult(); }
        });
        // Let the producer reach its blocking await so the teardown is a real observation.
        started.Task.Wait(Wait).Should().BeTrue();
        return run;
    }

    private static async IAsyncEnumerable<RunnerChunk> Chunks(int count)
    {
        for (var i = 0; i < count; i++)
        {
            await Task.Yield();
            yield return new RunnerChunk("Text", $"chunk {i}");
        }
    }

    private static async IAsyncEnumerable<RunnerChunk> BlockUntilCancelled(
        CancellationToken ct, Action onTeardown, [EnumeratorCancellation] CancellationToken _ = default)
    {
        try
        {
            yield return new RunnerChunk("Text", "started");
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        finally
        {
            onTeardown();
        }
    }

    private static async IAsyncEnumerable<RunnerChunk> IgnoresCancellation(Task release)
    {
        yield return new RunnerChunk("Text", "started");
        await release;
    }
}
