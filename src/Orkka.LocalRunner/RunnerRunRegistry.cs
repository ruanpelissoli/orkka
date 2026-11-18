namespace Orkka.LocalRunner;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Orkka.LocalRunner.Contracts;

/// <summary>
/// The runner's book of in-flight streamed runs (agent runs, chat runs, terminal sessions):
/// what <c>CancelRun</c> / <c>CancelTaskRuns</c> act on, what <c>Hello.ActiveRunIds</c>
/// reports, and what <c>CleanupWorktree</c> waits on before deleting a tree an agent may
/// still be reading.
///
/// A run is "active" from registration until the enumerable that produces its chunks has
/// been fully consumed or disposed — NOT until the hub send returns. SignalR's streamed
/// <c>SendAsync</c> returns as soon as the invocation is on the wire and pumps the items
/// in the background, so anything keyed on the send's completion forgets the run a few
/// milliseconds after it starts; wrap the stream with <see cref="Track"/> instead and the
/// registry learns of the real end from the enumerator's own teardown (completion,
/// cancellation, or the connection dropping and SignalR disposing the upload).
/// </summary>
public sealed class RunnerRunRegistry
{
    public sealed class ActiveRun
    {
        internal ActiveRun(string runId, string? taskId)
        {
            RunId = runId;
            TaskId = taskId;
        }

        public string RunId { get; }

        /// <summary>The task the run belongs to, when it has one (agent runs). Chat runs and
        /// terminal sessions have none and are only addressable by run id.</summary>
        public string? TaskId { get; }

        public CancellationTokenSource Cts { get; } = new();

        internal TaskCompletionSource CompletedSource { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when the run's chunk stream has ended and its producer has
        /// been torn down — for an agent run, after the CLI process has exited.</summary>
        public Task Completed => CompletedSource.Task;
    }

    private readonly ConcurrentDictionary<string, ActiveRun> _runs = new();

    public IReadOnlyList<string> ActiveRunIds => _runs.Keys.ToArray();

    public int Count => _runs.Count;

    /// <summary>Registers a run. False when the id is already active (duplicate start).</summary>
    public bool TryStart(string runId, string? taskId, out ActiveRun run)
    {
        run = new ActiveRun(runId, taskId);
        if (_runs.TryAdd(runId, run))
            return true;
        run.Cts.Dispose();
        return false;
    }

    /// <summary>Cancels one run by id. Unknown or already-finished ids are a no-op — a
    /// host replica re-announcing a stale id, or racing the run's own completion, must
    /// never fault.</summary>
    public bool Cancel(string runId)
    {
        if (!_runs.TryGetValue(runId, out var run))
            return false;
        try { run.Cts.Cancel(); }
        catch (ObjectDisposedException) { return false; }
        return true;
    }

    public IReadOnlyList<ActiveRun> RunsForTask(string taskId) =>
        _runs.Values.Where(r => r.TaskId == taskId).ToArray();

    /// <summary>
    /// Cancels every active run of a task and waits — up to <paramref name="waitFor"/> — for
    /// their producers to finish tearing down (the agent processes to exit). Returns the
    /// number of runs cancelled; zero when the task has nothing in flight here. A wait that
    /// times out still returns the count: the caller proceeds, and the lock-tolerant worktree
    /// delete is the next line of defence.
    /// </summary>
    public async Task<int> CancelTaskRunsAsync(string taskId, TimeSpan waitFor)
    {
        var runs = RunsForTask(taskId);
        if (runs.Count == 0)
            return 0;

        foreach (var run in runs)
            Cancel(run.RunId);

        await WaitAllAsync(runs, waitFor);
        return runs.Count;
    }

    /// <summary>
    /// Cancels every active run and waits — up to <paramref name="waitFor"/> — for their
    /// producers to tear down (the runner is stopping). Returns how many runs were in flight.
    /// </summary>
    public async Task<int> CancelAllAsync(TimeSpan waitFor)
    {
        var runs = _runs.Values.ToArray();
        if (runs.Length == 0)
            return 0;

        foreach (var run in runs)
            Cancel(run.RunId);

        await WaitAllAsync(runs, waitFor);
        return runs.Length;
    }

    /// <summary>True when every run finished within the window.</summary>
    public static async Task<bool> WaitAllAsync(IReadOnlyList<ActiveRun> runs, TimeSpan waitFor)
    {
        if (runs.Count == 0)
            return true;
        var all = Task.WhenAll(runs.Select(r => r.Completed));
        try
        {
            await all.WaitAsync(waitFor);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Marks a run finished: releases waiters and drops it from the active set.
    /// Idempotent.</summary>
    public void Complete(string runId)
    {
        if (!_runs.TryRemove(runId, out var run))
            return;
        run.CompletedSource.TrySetResult();
        run.Cts.Dispose();
    }

    /// <summary>
    /// Wraps a run's chunk stream so the registry observes its real end. The wrapper is what
    /// gets handed to the hub send; <see cref="Complete"/> fires from its teardown, which
    /// SignalR triggers on completion, on cancellation, and when the connection goes away.
    /// </summary>
    public async IAsyncEnumerable<RunnerChunk> Track(
        ActiveRun run,
        IAsyncEnumerable<RunnerChunk> inner,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        try
        {
            await foreach (var chunk in inner.WithCancellation(ct))
                yield return chunk;
        }
        finally
        {
            Complete(run.RunId);
        }
    }
}
