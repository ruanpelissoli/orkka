namespace Orkka.LocalRunner;

using System.Collections.Concurrent;

/// <summary>
/// How many agent runs this machine works on at once, across every workspace. Two jobs each
/// sending a few tasks must not start six agents on one laptop: a run that finds every slot
/// taken waits for one before its agent starts. Configured with
/// <c>ORKKA_MAX_CONCURRENT_RUNS</c> (environment or ~/.orkka/runner.env); default 2.
/// </summary>
public sealed class MachineRunSlots
{
    public const int DefaultCapacity = 2;
    public const int MaxCapacity = 32;
    public const string SettingName = "ORKKA_MAX_CONCURRENT_RUNS";

    private readonly SemaphoreSlim _gate;

    public MachineRunSlots(int capacity)
    {
        Capacity = Math.Clamp(capacity, 1, MaxCapacity);
        _gate = new SemaphoreSlim(Capacity, Capacity);
    }

    public int Capacity { get; }

    public int Available => _gate.CurrentCount;

    /// <summary>A positive whole number up to <see cref="MaxCapacity"/>; anything else is
    /// the default.</summary>
    public static int ParseCapacity(string? value)
        => int.TryParse(value?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
            && n is >= 1 and <= MaxCapacity
            ? n
            : DefaultCapacity;

    /// <summary>Waits for a slot; disposing the result gives it back (once).</summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        return new Slot(_gate);
    }

    private sealed class Slot(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();
        }
    }
}

/// <summary>
/// Which session's host link a run belongs to. The agent invokers — and the approval gate
/// they call — are machine-wide singletons, but an interactive run's approval question must go
/// to the Orkka the run came from: the run registers its session's link under its task id for
/// as long as it runs. With a single session there is nothing to choose.
/// </summary>
public sealed class RunnerHostRouter
{
    private readonly ConcurrentDictionary<string, RunnerHostLink> _byTask = new(StringComparer.Ordinal);
    private readonly List<RunnerHostLink> _sessions = [];
    private readonly Lock _gate = new();

    public void AddSession(RunnerHostLink link)
    {
        lock (_gate)
        {
            if (!_sessions.Contains(link))
                _sessions.Add(link);
        }
    }

    /// <summary>Routes <paramref name="taskId"/> to <paramref name="link"/> until disposed.</summary>
    public IDisposable Route(string? taskId, RunnerHostLink link)
    {
        if (string.IsNullOrEmpty(taskId))
            return new Registration(null, null, this);
        _byTask[taskId] = link;
        return new Registration(taskId, link, this);
    }

    public RunnerHostLink? Resolve(string? taskId)
    {
        if (!string.IsNullOrEmpty(taskId) && _byTask.TryGetValue(taskId, out var link))
            return link;
        lock (_gate)
            return _sessions.Count == 1 ? _sessions[0] : null;
    }

    private sealed class Registration(string? taskId, RunnerHostLink? link, RunnerHostRouter router) : IDisposable
    {
        public void Dispose()
        {
            if (taskId is not null && link is not null)
                router._byTask.TryRemove(new KeyValuePair<string, RunnerHostLink>(taskId, link));
        }
    }
}
