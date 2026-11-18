namespace Orkka.Modules.Execution.Orchestration;

using System.Collections.Concurrent;

/// <summary>
/// The worker's drain state: "this process is going away — finish what you hold, start
/// nothing new." Deploys used to cut every in-flight agent run (the run re-ran from scratch
/// on the replacement pod, the user saw a restarted phase); a drain lets the old pod keep its
/// runs — and the runner websockets they stream over — alive until they complete.
///
/// Two halves. <see cref="Begin"/> flips the state and fires <see cref="Token"/>: the queue
/// processor stops claiming, resuming and picking up sessions, and long-lived loops (an
/// interactive session idling between turns) use the token to step out at a point where
/// nothing is lost. <see cref="Track"/> registers each piece of in-flight work, so whoever
/// is holding the shutdown (the host lifetime) can <see cref="WaitForIdleAsync"/> before
/// letting the normal stop proceed. Registered as a singleton; the hosted Worker's
/// <c>DrainingHostLifetime</c> drives it, Orkka Local never begins one (its container stop
/// keeps today's cancel-and-re-run behaviour).
/// </summary>
public sealed class WorkerDrain
{
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<long, string> _inFlight = new();
    private long _nextId;
    private TaskCompletionSource _idle = NewIdleSource();

    /// <summary>Fires when the drain begins. Never cancelled otherwise.</summary>
    public CancellationToken Token => _cts.Token;

    public bool IsDraining => _cts.IsCancellationRequested;

    public int InFlight => _inFlight.Count;

    /// <summary>What is still running, for logs ("2 task run(s), 1 chat turn").</summary>
    public IReadOnlyCollection<string> InFlightDescriptions => _inFlight.Values.ToArray();

    public void Begin()
    {
        if (!_cts.IsCancellationRequested)
            _cts.Cancel();
    }

    /// <summary>
    /// Registers a unit of in-flight work; dispose when it finishes. Registration is allowed
    /// during a drain (a chat turn that arrived a moment before the signal is still work
    /// worth finishing) — the drain simply waits for it too, within its deadline.
    /// </summary>
    public IDisposable Track(string description)
    {
        var id = Interlocked.Increment(ref _nextId);
        _inFlight[id] = description;
        return new Registration(this, id);
    }

    /// <summary>
    /// Completes when nothing is in flight, or when <paramref name="timeout"/> elapses.
    /// Returns true when idle was reached. Work registered after idle is reached is not
    /// waited for — the caller should have begun the drain first, so nothing new starts.
    /// </summary>
    public async Task<bool> WaitForIdleAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            // Capture the edge source BEFORE reading the count: a release that lands between
            // the two completes the captured source, so the wait below cannot miss it.
            var idle = Volatile.Read(ref _idle).Task;
            if (_inFlight.IsEmpty)
                return true;

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return false;

            try
            {
                await idle.WaitAsync(remaining, ct);
            }
            catch (TimeoutException)
            {
                return false;
            }
        }
    }

    private void Release(long id)
    {
        if (!_inFlight.TryRemove(id, out _))
            return;
        if (_inFlight.IsEmpty)
        {
            // Wake every waiter, then arm a fresh source for the next non-empty → empty edge.
            var previous = Interlocked.Exchange(ref _idle, NewIdleSource());
            previous.TrySetResult();
        }
    }

    private static TaskCompletionSource NewIdleSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Registration(WorkerDrain owner, long id) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Release(id);
        }
    }
}
