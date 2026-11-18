namespace Orkka.Modules.Execution.Orchestration;

using System.Threading.Channels;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Utilities;
using Microsoft.Extensions.Logging;

/// <summary>What a live capture needs to know about the run it mirrors.</summary>
public sealed record LiveDiffContext(
    Ulid OrgId, Ulid RepoId, Ulid TaskId, string BaseBranch, string BranchName,
    Lane Lane, Ulid? ExecutionId,
    // A Story member's starting commit on the shared branch: the live diff is its own change.
    string? ReviewBaseSha = null);

/// <summary>
/// Keeps a running task's <em>live</em> code-change snapshot current while its agent works.
///
/// The Code View shows a file tree from the agent's tool calls the instant they stream, but a
/// tool call is what the agent <em>asked</em> for, not what happened: a Bash command that
/// scaffolds a migration, an install that rewrites a lockfile, a `sed -i` — none of those name
/// their files, and even an Edit's exact result depends on the file. So after every mutating
/// tool call the orchestrator asks the machine holding the worktree for the actual working-tree
/// diff, stores it as the task's one live snapshot and pushes a WorkspaceChanged event. That
/// makes the tree exact, covers generated files, and lets a tab opened mid-run paint at once.
///
/// Timing: a tool_use block streams <em>before</em> the tool runs, so a capture fired on it
/// would see the tree from a moment ago. The agent's next chunk (any text, thinking or tool
/// call) means the previous tool has finished — that is the reliable "now diff" signal — and
/// the debounce catches the common Edit/Write case where the tool is done within a second
/// anyway. Captures are throttled so a burst of edits costs one diff every few seconds, and a
/// final flush at run end lands the last edits regardless.
///
/// One instance per agent run; single background loop; every failure is logged and swallowed
/// — a live mirror must never fail the run it mirrors. An old runner without the working-tree
/// op disables the capture for the rest of the run after the first refusal.
/// </summary>
internal sealed class LiveWorkspaceCapture : IAsyncDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan FlushBudget = TimeSpan.FromSeconds(20);

    private static readonly HashSet<string> MutatingTools = new(StringComparer.Ordinal)
    {
        "Edit", "Write", "MultiEdit", "NotebookEdit", "Bash",
    };

    private readonly LiveDiffContext _context;
    private readonly IGitWorkspaceManager _git;
    private readonly StoreLiveSnapshot _store;
    private readonly ITaskNotifier _notifier;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts;
    private readonly Channel<Signal> _signals = Channel.CreateUnbounded<Signal>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Task _loop;

    // Written by the stream thread, read by the loop: "something may have changed since the
    // last capture". Cleared by the loop when it takes a capture.
    private int _dirty;
    private volatile bool _disabled;
    private long _captures;

    private enum Signal { Change, Progress, Flush }

    /// <summary>Persists one capture (the orchestrator supplies a scoped ITasksApi); null when
    /// nothing changed since the previous capture.</summary>
    public delegate Task<WorkspaceChangedEvent?> StoreLiveSnapshot(
        LiveDiffContext context, string diff, bool isTruncated, CancellationToken ct);

    public LiveWorkspaceCapture(
        LiveDiffContext context,
        IGitWorkspaceManager git,
        StoreLiveSnapshot store,
        ITaskNotifier notifier,
        ILogger logger,
        CancellationToken runCt)
    {
        _context = context;
        _git = git;
        _store = store;
        _notifier = notifier;
        _logger = logger;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(runCt);
        _loop = Task.Run(() => LoopAsync(_cts.Token), CancellationToken.None);
    }

    /// <summary>Number of captures that produced a new snapshot — for tests and logs.</summary>
    public long Captures => Interlocked.Read(ref _captures);

    /// <summary>A ToolUse chunk streamed. Mutating tools mark the tree dirty.</summary>
    public void OnToolUse(string content)
    {
        if (_disabled) return;
        var colon = content.IndexOf(':');
        var tool = colon > 0 ? content[..colon].Trim() : content.Trim();
        if (!MutatingTools.Contains(tool)) return;
        Interlocked.Exchange(ref _dirty, 1);
        _signals.Writer.TryWrite(Signal.Change);
    }

    /// <summary>Any other chunk streamed — the agent moved on, so its last tool has run.</summary>
    public void OnAgentProgress()
    {
        if (_disabled || Volatile.Read(ref _dirty) == 0) return;
        _signals.Writer.TryWrite(Signal.Progress);
    }

    /// <summary>
    /// Takes a final capture if anything is pending, bounded so a hung git never delays the
    /// run's completion, then stops the loop.
    /// </summary>
    public async Task FlushAsync()
    {
        if (!_disabled && Volatile.Read(ref _dirty) == 1)
        {
            _signals.Writer.TryWrite(Signal.Flush);
            // Wait for the loop to drain: it clears _dirty when it captures.
            var deadline = DateTime.UtcNow + FlushBudget;
            while (Volatile.Read(ref _dirty) == 1 && DateTime.UtcNow < deadline && !_loop.IsCompleted)
                await Task.Delay(100, CancellationToken.None);
        }
        await DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _signals.Writer.TryComplete();
        try
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            _cts.Cancel();
        }
        catch (OperationCanceledException) { }
        _cts.Dispose();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var reader = _signals.Reader;
        var lastCapture = DateTime.MinValue;
        try
        {
            while (await reader.WaitToReadAsync(ct))
            {
                var flush = Drain(reader);

                if (!flush)
                {
                    // Debounce: let a burst of edits settle. Extended by further signals.
                    while (true)
                    {
                        try { await Task.Delay(Debounce, ct); }
                        catch (OperationCanceledException) { return; }
                        if (!reader.TryPeek(out _)) break;
                        if (Drain(reader)) { flush = true; break; }
                    }

                    var since = DateTime.UtcNow - lastCapture;
                    if (since < MinInterval)
                    {
                        try { await Task.Delay(MinInterval - since, ct); }
                        catch (OperationCanceledException) { return; }
                        if (Drain(reader)) flush = true;
                    }
                }

                if (_disabled || Volatile.Read(ref _dirty) == 0)
                    continue;

                await CaptureAsync(ct);
                lastCapture = DateTime.UtcNow;
                if (flush) Interlocked.Exchange(ref _dirty, 0);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Live workspace capture loop for task {TaskId} stopped unexpectedly", _context.TaskId);
        }
    }

    /// <summary>Drops every queued signal; true when a Flush was among them.</summary>
    private static bool Drain(ChannelReader<Signal> reader)
    {
        var flush = false;
        while (reader.TryRead(out var s))
            if (s == Signal.Flush) flush = true;
        return flush;
    }

    private async Task CaptureAsync(CancellationToken ct)
    {
        // Clear before diffing: an edit that lands during the diff re-dirties and gets the
        // next capture, instead of being lost between "we diffed" and "we cleared".
        Interlocked.Exchange(ref _dirty, 0);

        Result<string> diff;
        try
        {
            diff = await _git.GetWorkingTreeDiffAsync(
                _context.OrgId, _context.RepoId, _context.TaskId, _context.BaseBranch,
                maxChars: CodeChangeSnapshotRules.MaxDiffChars, ct: ct, baseSha: _context.ReviewBaseSha);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Live diff for task {TaskId} threw — skipping this capture", _context.TaskId);
            return;
        }

        if (diff.IsFailure)
        {
            if (diff.Error?.Contains("does not support", StringComparison.OrdinalIgnoreCase) == true)
            {
                _disabled = true;
                _logger.LogInformation(
                    "Live diff unavailable for task {TaskId} ({Error}) — live snapshots off for this run",
                    _context.TaskId, diff.Error);
            }
            else
            {
                _logger.LogDebug("Live diff for task {TaskId} failed: {Error}", _context.TaskId, diff.Error);
            }
            return;
        }

        var text = diff.Value ?? "";
        var truncated = text.EndsWith("... (diff truncated)", StringComparison.Ordinal);

        try
        {
            var evt = await _store(_context, text, truncated, ct);
            if (evt is null) return;

            Interlocked.Increment(ref _captures);
            await _notifier.SendWorkspaceChangedAsync(_context.TaskId, evt);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Storing the live snapshot for task {TaskId} failed", _context.TaskId);
        }
    }
}
