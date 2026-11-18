namespace Orkka.Tests.Execution;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Orkka.Modules.Execution.Orchestration;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Utilities;

/// <summary>
/// The live mirror's contract with the run it shadows: it diffs after mutating tools (not
/// after reads), coalesces bursts, lands the final state on flush, never throws into the
/// stream, and stands down for a runner that cannot diff. Timing is exercised with real
/// (short) delays — the debounce/throttle are the behaviour under test.
/// </summary>
public sealed class LiveWorkspaceCaptureTests
{
    private static readonly LiveDiffContext Context = new(
        Ulid.NewUlid(), Ulid.NewUlid(), Ulid.NewUlid(), "main", "orkka/T-1", Lane.Developing, Ulid.NewUlid());

    private sealed class Harness
    {
        public readonly Mock<IGitWorkspaceManager> Git = new(MockBehavior.Strict);
        public readonly Mock<ITaskNotifier> Notifier = new();
        public readonly List<string> StoredDiffs = [];
        public int DiffCalls;
        public string NextDiff = "diff --git a/x b/x\n+++ b/x\n@@ -0,0 +1 @@\n+x\n";
        public Result<string>? ForcedResult;

        public Harness()
        {
            Git.Setup(g => g.GetWorkingTreeDiffAsync(
                    It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<string>(),
                    It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
                .ReturnsAsync(() =>
                {
                    Interlocked.Increment(ref DiffCalls);
                    return ForcedResult ?? Result<string>.Success(NextDiff);
                });
        }

        public Task<WorkspaceChangedEvent?> Store(LiveDiffContext c, string diff, bool truncated, CancellationToken ct)
        {
            lock (StoredDiffs)
            {
                // Same contract as the real upsert: identical diff → nothing to broadcast.
                if (StoredDiffs.Count > 0 && StoredDiffs[^1] == diff)
                    return Task.FromResult<WorkspaceChangedEvent?>(null);
                StoredDiffs.Add(diff);
                return Task.FromResult<WorkspaceChangedEvent?>(new WorkspaceChangedEvent(
                    c.TaskId, Ulid.NewUlid(), StoredDiffs.Count, c.BranchName, c.BaseBranch,
                    1, 1, 0, truncated, [new WorkspaceChangedFile("x", "added", 1, 0)], DateTimeOffset.UtcNow));
            }
        }

        public LiveWorkspaceCapture Build(CancellationToken ct = default) =>
            new(Context, Git.Object, Store, Notifier.Object, NullLogger.Instance, ct);

        /// <summary>
        /// Waits for the loop to have asked git <paramref name="count"/> times. The debounce is
        /// 1.5 s of wall clock; a fixed sleep with a small margin is exactly what a loaded CI
        /// runner misses, so the tests wait for the event and only then check that nothing
        /// more happened.
        /// </summary>
        public async Task WaitForDiffCallsAsync(int count)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (Volatile.Read(ref DiffCalls) < count && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            // Let anything queued right behind the capture (store, broadcast) land too.
            await Task.Delay(300);
        }
    }

    [Fact]
    public async Task ReadOnlyTools_NeverTriggerADiff()
    {
        var h = new Harness();
        await using (var capture = h.Build())
        {
            capture.OnToolUse("Read: {\"file_path\":\"a.cs\"}");
            capture.OnToolUse("Grep: {\"pattern\":\"foo\"}");
            capture.OnAgentProgress();
            await Task.Delay(2500);
        }

        h.DiffCalls.Should().Be(0);
        h.StoredDiffs.Should().BeEmpty();
    }

    [Fact]
    public async Task ABurstOfEdits_CoalescesIntoOneCapture_AndBroadcasts()
    {
        var h = new Harness();
        await using var capture = h.Build();

        for (var i = 0; i < 5; i++)
            capture.OnToolUse($"Edit: {{\"file_path\":\"f{i}.cs\",\"old_string\":\"a\",\"new_string\":\"b\"}}");
        capture.OnAgentProgress();

        await h.WaitForDiffCallsAsync(1);
        // The burst is coalesced: nothing else may fire once the first capture landed.
        await Task.Delay(1000);

        h.DiffCalls.Should().Be(1);
        h.StoredDiffs.Should().HaveCount(1);
        capture.Captures.Should().Be(1);
        h.Notifier.Verify(n => n.SendWorkspaceChangedAsync(Context.TaskId, It.IsAny<WorkspaceChangedEvent>()), Times.Once);
    }

    [Fact]
    public async Task Flush_LandsPendingEditsWithoutWaitingOutTheDebounce()
    {
        var h = new Harness();
        var capture = h.Build();

        capture.OnToolUse("Write: {\"file_path\":\"new.cs\",\"content\":\"x\"}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await capture.FlushAsync();
        sw.Stop();

        h.DiffCalls.Should().Be(1);
        h.StoredDiffs.Should().HaveCount(1);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task UnchangedDiff_IsNotBroadcastAgain()
    {
        var h = new Harness();
        var capture = h.Build();

        capture.OnToolUse("Edit: {\"file_path\":\"a.cs\"}");
        await h.WaitForDiffCallsAsync(1);
        // Second round: the "diff" the machine reports is byte-identical.
        capture.OnToolUse("Edit: {\"file_path\":\"a.cs\"}");
        await capture.FlushAsync();

        h.DiffCalls.Should().Be(2);
        h.StoredDiffs.Should().HaveCount(1);
        h.Notifier.Verify(n => n.SendWorkspaceChangedAsync(It.IsAny<Ulid>(), It.IsAny<WorkspaceChangedEvent>()), Times.Once);
    }

    [Fact]
    public async Task RunnerWithoutTheOp_DisablesItselfAfterOneRefusal()
    {
        var h = new Harness { ForcedResult = Result<string>.Failure("The connected runner does not support working-tree diffs") };
        var capture = h.Build();

        capture.OnToolUse("Edit: {\"file_path\":\"a.cs\"}");
        await h.WaitForDiffCallsAsync(1);
        capture.OnToolUse("Edit: {\"file_path\":\"b.cs\"}");
        capture.OnToolUse("Bash: {\"command\":\"rm x\"}");
        await capture.FlushAsync();

        h.DiffCalls.Should().Be(1);
        h.StoredDiffs.Should().BeEmpty();
    }

    [Fact]
    public async Task GitFailure_IsSwallowed_AndTheNextEditTriesAgain()
    {
        var h = new Harness { ForcedResult = Result<string>.Failure("index.lock exists") };
        var capture = h.Build();

        capture.OnToolUse("Edit: {\"file_path\":\"a.cs\"}");
        await h.WaitForDiffCallsAsync(1);
        h.DiffCalls.Should().Be(1);
        h.StoredDiffs.Should().BeEmpty();

        h.ForcedResult = null;
        capture.OnToolUse("Edit: {\"file_path\":\"a.cs\"}");
        await capture.FlushAsync();

        h.DiffCalls.Should().Be(2);
        h.StoredDiffs.Should().HaveCount(1);
    }
}
