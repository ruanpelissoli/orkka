namespace Orkka.LocalRunner.Apps;

using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Orkka.LocalRunner.Contracts;
using Orkka.LocalRunner.Terminal;

/// <summary>
/// Owns the processes the Run button starts: the user's own app, running on the user's own
/// machine, in their real working copy.
///
/// Three things separate these from terminal sessions, which is why they are not the same
/// manager:
/// <list type="bullet">
/// <item>They outlive the browser. A dev server must survive a page reload, a navigation,
/// the hub reconnecting and the host restarting — it dies when the user stops it, when a
/// newer start replaces it, or when the runner itself exits.</item>
/// <item>They are keyed by (project, target), not by session id, so starting the web app
/// twice replaces the first process instead of leaving an orphan holding port 3000.</item>
/// <item>They own ports, so starting one has to reason about who already holds them.</item>
/// </list>
///
/// Each target runs under a PTY rather than a plain redirected process: dev tooling prints
/// colour, draws progress, and reads single keypresses (Expo's `r`, Vite's `h`), all of
/// which a pipe silently breaks.
///
/// <b>The manager always drains its own PTYs</b> and streams are mere subscribers. Reading
/// only while a stream is attached looks simpler and is wrong twice over: a full pseudo-
/// terminal buffer BLOCKS the child process, so a disconnected browser would freeze the dev
/// server; and tying the read loop to the stream makes "the connection dropped" and "the
/// process exited" the same event, which would kill the app on every host restart.
///
/// <b>Before a target spawns, the manager prepares its folder</b> — the difference between a
/// play button that works on a fresh clone and one that prints "Cannot find module" or
/// "DATABASE_URL is not set" and blames the user: a missing `.env` is written from the
/// repo's `.env.example` (local defaults, not secrets), a declared install command runs when
/// dependencies are evidently absent, and Docker-backed targets are refused up front with a
/// plain "Docker is not running" when the daemon is down. Every step is announced in the
/// target's own log so nothing happens behind the user's back.
/// </summary>
public sealed class AppRunManager(ILogger<AppRunManager> logger) : IDisposable
{
    /// <summary>Generous enough for a monorepo's full stack, low enough that a runaway
    /// caller cannot fork-bomb the machine.</summary>
    public const int MaxSessions = 12;

    /// <summary>Replay handed to a stream when it attaches. Enough to see why a dev server
    /// died while nothing was watching; small enough that a chatty build's output cannot
    /// grow the runner's memory without bound.</summary>
    private const int ScrollbackBytes = 256 * 1024;

    /// <summary>Staggers spawns so a portless dependency has a moment to get going before its
    /// dependents boot. Dependencies that declare a port get a real readiness wait instead —
    /// see <see cref="DependencyReadyTimeout"/>.</summary>
    private static readonly TimeSpan DependencyStagger = TimeSpan.FromMilliseconds(600);

    /// <summary>
    /// How long a dependent waits for a dependency's declared port to accept connections
    /// before starting anyway. A Postgres container or an API that runs migrations on boot
    /// takes seconds, not milliseconds, and an app that boots first crashes on "connection
    /// refused" — the exact failure the user then reports as "Run does not work". Bounded,
    /// and bounded again in total across a stack, because one dependency that never binds
    /// must not hold the whole start call past the host's operation timeout.
    /// </summary>
    internal static readonly TimeSpan DependencyReadyTimeout = TimeSpan.FromSeconds(45);
    internal static readonly TimeSpan TotalReadyBudget = TimeSpan.FromSeconds(90);

    /// <summary>A stop command that has not returned by then is abandoned and the process
    /// tree killed as usual — the user pressed Stop and expects things to stop.</summary>
    private static readonly TimeSpan StopCommandTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, AppRunSession> _sessions = new();

    public int ActiveCount => _sessions.Count;

    /// <summary>
    /// Starts every target, in dependency order. Returns as soon as the processes are
    /// spawned — the caller streams each target's output separately, and a per-target
    /// failure never blocks the rest of the stack from coming up.
    ///
    /// <paramref name="resolveRepoPath"/> maps a repo token to the user's working copy; it
    /// stays a parameter so this type never learns the repo cache layout.
    /// </summary>
    public async Task<StartAppRunResult> StartAsync(
        StartAppRunRequest request,
        Func<string, string, Task<(string? Path, string? Error)>> resolveRepoPath,
        Action<string> onSessionStarted)
    {
        var ordered = OrderByDependencies(request.Targets);
        var results = new List<AppRunTargetStart>(ordered.Count);
        var readyBudget = Stopwatch.StartNew();
        var dependedOn = ordered
            .SelectMany(t => t.DependsOn)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < ordered.Count; i++)
        {
            var target = ordered[i];
            var result = await StartOneAsync(request, target, resolveRepoPath);
            results.Add(result);
            if (!result.Started)
                continue;

            onSessionStarted(target.RunId);
            // Only a dependency holds the loop; an unrelated target that happens to start
            // earlier costs its successors nothing.
            if (i < ordered.Count - 1 && dependedOn.Contains(target.Key))
                await WaitForDependencyAsync(target, readyBudget);
        }

        // Answer in the order asked, not the order started — the UI lists targets as the
        // user selected them.
        var byRunId = results.ToDictionary(r => r.RunId);
        return new StartAppRunResult(
            request.Targets.Select(t => byRunId[t.RunId]).ToArray());
    }

    /// <summary>Holds the start loop until a just-started dependency is usable: its port
    /// listening when it declared one, a short stagger otherwise.</summary>
    private async Task WaitForDependencyAsync(AppRunTargetSpec dependency, Stopwatch readyBudget)
    {
        if (dependency.Port is not { } port)
        {
            await Task.Delay(DependencyStagger);
            return;
        }

        var remaining = TotalReadyBudget - readyBudget.Elapsed;
        if (remaining <= TimeSpan.Zero)
            return;
        var timeout = remaining < DependencyReadyTimeout ? remaining : DependencyReadyTimeout;

        var ready = await PortInspector.WaitForPortAsync(port, timeout, CancellationToken.None);
        if (!ready)
        {
            logger.LogInformation(
                "Dependency '{Label}' ({Key}) did not open port {Port} within {Timeout}s — starting its dependents anyway",
                dependency.Label, dependency.Key, port, (int)timeout.TotalSeconds);
        }
    }

    private async Task<AppRunTargetStart> StartOneAsync(
        StartAppRunRequest request,
        AppRunTargetSpec target,
        Func<string, string, Task<(string? Path, string? Error)>> resolveRepoPath)
    {
        AppRunTargetStart Fail(string error, AppRunPortConflict? conflict = null)
            => new(target.RunId, target.Key, false, null, null, null, conflict, error);

        if (!RunnerTokens.TryParseUserRepo(target.RepoToken, out var orgId, out var repoId))
            return Fail("Invalid repository token.");

        var (repoPath, repoError) = await resolveRepoPath(orgId, repoId);
        if (repoPath is null)
            return Fail(repoError ?? "Could not find that repository on this machine.");

        var cwd = ResolveWorkingDirectory(repoPath, target.WorkingDirectory);
        if (cwd is null)
            return Fail($"The folder '{target.WorkingDirectory}' does not exist in this repository.");

        // Replacing our own previous process for this target is the whole reason Run is safe
        // to press twice — do it before the port check, or we would report a conflict
        // against ourselves.
        await StopTargetAsync(request.ProjectKey, target.Key);

        // Infra targets skip the conflict check: `docker compose up <svc>` against a container
        // that is already up simply attaches to it (a runner restart leaves the containers
        // running on purpose), and the port's owner is Docker's own proxy — the one process
        // "free the port" must never kill.
        if (target.Port is { } port && !NeedsDocker(target)
            && PortInspector.FindConflict(port) is { } conflict)
        {
            var owner = conflict.ProcessName is { } name ? $" ({name}, pid {conflict.Pid})" : "";
            if (!request.ForceFreePorts)
                return Fail(
                    $"Port {port} is already in use{owner} — stop it, or start again with \"free the port\".",
                    conflict);
            if (!PortInspector.TryFreePort(port))
                return Fail(
                    $"Port {port} is held by a process this runner could not stop{owner}. " +
                    "Stop it yourself and try again.",
                    conflict);
            logger.LogInformation(
                "Freed port {Port} (was pid {Pid}) for target {Target}", port, conflict.Pid, target.Key);
        }

        if (_sessions.Count >= MaxSessions)
            return Fail($"Too many apps are already running ({MaxSessions} max) — stop one first.");

        // Docker-backed targets fail fast and plainly. `docker compose up` against a stopped
        // daemon prints a pipe error that means nothing to most people; this is the one
        // failure that gets a sentence the user can act on before any process starts.
        if (NeedsDocker(target) && !DockerProbe.IsDaemonRunning())
            return Fail(
                $"'{target.Label}' needs Docker, but the Docker daemon is not running — start Docker Desktop and run again.");

        var prepared = AppRunPreparer.Prepare(cwd, target);
        var commandLine = prepared.InstallCommand is { } install
            ? $"{install} && {target.Command}"
            : target.Command;

        var shell = ShellResolver.ResolveForCommand(commandLine);
        var options = new PtyOptions(
            shell.Path,
            shell.Args,
            cwd,
            BuildEnvironment(),
            Math.Clamp(request.Cols, 2, 500),
            Math.Clamp(request.Rows, 2, 300));

        try
        {
            var pty = Pty.Spawn(options);
            var session = new AppRunSession(
                target.RunId, request.ProjectKey, target.Key, target.Label, target.Port,
                cwd, target.StopCommand, pty, ScrollbackBytes, logger);
            if (!_sessions.TryAdd(target.RunId, session))
            {
                session.Dispose();
                return Fail("A run with this id is already active.");
            }
            // Announced BEFORE the drain starts, so the notes sit above the process's own
            // first line in every log pane, including ones that attach later.
            foreach (var note in prepared.Notes)
                session.Announce(note);
            session.StartDraining();
            logger.LogInformation(
                "Started '{Label}' ({Key}): {Command} — in {Cwd}",
                target.Label, target.Key, commandLine, cwd);
            return new AppRunTargetStart(
                target.RunId, target.Key, true, cwd, shell.Name, null, null, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Target {Key} failed to start", target.Key);
            return Fail($"Could not start '{target.Label}': {ex.Message}");
        }
    }

    internal static bool NeedsDocker(AppRunTargetSpec target)
        => string.Equals(target.Kind, "infra", StringComparison.OrdinalIgnoreCase)
            || target.Command.TrimStart().StartsWith("docker ", StringComparison.OrdinalIgnoreCase)
            || target.Command.TrimStart().StartsWith("docker-compose ", StringComparison.OrdinalIgnoreCase);

    /// <summary>Stops one run, or every run of a project when RunId is null. Idempotent —
    /// stopping something already gone is the normal case after a crash.</summary>
    public async Task<int> StopAsync(StopAppRunRequest request)
    {
        var doomed = _sessions.Values
            .Where(s => s.ProjectKey == request.ProjectKey
                && (request.RunId is null || s.RunId == request.RunId))
            .ToList();
        foreach (var session in doomed)
            await CloseAsync(session.RunId);
        return doomed.Count;
    }

    public AppRunStatusResult Status() => new(_sessions.Values
        .Select(s => new AppRunSessionInfo(
            s.RunId, s.ProjectKey, s.Key, s.Label, s.Port, !s.Exited, s.StartedAtUnixMs, s.Ready))
        .ToArray());

    public bool IsActive(string runId) => _sessions.ContainsKey(runId);

    public async Task WriteAsync(AppRunInputRequest request)
    {
        if (!_sessions.TryGetValue(request.RunId, out var session))
            return;
        try
        {
            await session.Pty.WriteAsync(
                Convert.FromBase64String(request.DataBase64), CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Racing process exit is routine, not an error.
            logger.LogDebug(ex, "App run {RunId} input dropped", request.RunId);
        }
    }

    public void Resize(ResizeAppRunRequest request)
    {
        if (_sessions.TryGetValue(request.RunId, out var session))
            session.Pty.Resize(Math.Clamp(request.Cols, 2, 500), Math.Clamp(request.Rows, 2, 300));
    }

    /// <summary>Kills a run's process tree and forgets it, running its stop command first
    /// when it has one. Idempotent. This — not a closed stream — is the only thing that ends
    /// an app run.</summary>
    public async Task CloseAsync(string runId)
    {
        if (!_sessions.TryRemove(runId, out var session))
            return;
        logger.LogInformation("Stopping '{Label}' ({Key})", session.Label, session.Key);
        if (session.StopCommand is { Length: > 0 } stopCommand && !session.Exited)
            await RunStopCommandAsync(session, stopCommand);
        session.Dispose();
    }

    /// <summary>
    /// A `docker compose up` target's containers keep running after the compose process is
    /// killed — the PTY dies, the database does not — so the target says how to stop what it
    /// started. Bounded and best-effort: the tree kill follows either way.
    /// </summary>
    private async Task RunStopCommandAsync(AppRunSession session, string stopCommand)
    {
        session.Announce($"Stopping: {stopCommand}");
        try
        {
            var shell = ShellResolver.ResolveForCommand(stopCommand);
            var startInfo = new ProcessStartInfo
            {
                FileName = shell.Path,
                WorkingDirectory = session.Cwd,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in shell.Args)
                startInfo.ArgumentList.Add(arg);
            using var process = Process.Start(startInfo);
            if (process is null)
                return;
            using var timeout = new CancellationTokenSource(StopCommandTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation(
                    "Stop command for '{Label}' did not finish within {Seconds}s — killing the process tree",
                    session.Label, (int)StopCommandTimeout.TotalSeconds);
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Stop command for '{Label}' failed", session.Label);
        }
    }

    /// <summary>
    /// Streams one run's output: the scrollback so far, then everything new, as base64 "Pty"
    /// chunks — the same shape terminal sessions use, so the host consumes both through one
    /// path. A "Ready" chunk marks the moment the target's port started accepting
    /// connections. Ends with "Exit" + "Done" when the process finishes. Detaching (the
    /// browser went away, the hub reconnected) leaves the process alone; a later attach picks
    /// the stream back up from the scrollback.
    /// </summary>
    public async IAsyncEnumerable<RunnerChunk> PumpOutput(
        string runId, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!_sessions.TryGetValue(runId, out var session))
        {
            yield return new RunnerChunk("Error", "That run is no longer active.");
            yield return new RunnerChunk("Done", "");
            yield break;
        }

        var (replay, ready, live) = session.Subscribe();
        try
        {
            if (replay.Length > 0)
                yield return new RunnerChunk("Pty", Convert.ToBase64String(replay));
            if (ready)
                yield return new RunnerChunk("Ready", "");

            await foreach (var chunk in live.Reader.ReadAllAsync(ct))
                yield return chunk;
        }
        finally
        {
            session.Unsubscribe(live);
        }
    }

    /// <summary>
    /// Dependency order, falling back to declared order for anything cyclic or dangling. A
    /// cycle in a hand-edited config must not deadlock the Run button — starting in the
    /// wrong order costs a few seconds of reconnect noise, starting not at all costs the
    /// feature.
    /// </summary>
    internal static List<AppRunTargetSpec> OrderByDependencies(AppRunTargetSpec[] targets)
    {
        var byKey = new Dictionary<string, AppRunTargetSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
            byKey.TryAdd(target.Key, target);

        var ordered = new List<AppRunTargetSpec>(targets.Length);
        // Marking on entry (not on completion) is what makes a cycle terminate: the second
        // visit sees the key already marked and returns instead of recursing forever.
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(AppRunTargetSpec target)
        {
            if (!visited.Add(target.Key))
                return;
            foreach (var dependency in target.DependsOn)
            {
                if (byKey.TryGetValue(dependency, out var next))
                    Visit(next);
            }
            ordered.Add(target);
        }

        foreach (var target in targets)
            Visit(target);
        return ordered;
    }

    /// <summary>Resolves a target's folder inside the repo, refusing anything that escapes
    /// it. The path comes from a config the host generated, but "the host said so" has never
    /// been a reason to spawn a process outside the repo the user pointed at.</summary>
    internal static string? ResolveWorkingDirectory(string repoPath, string? relative)
    {
        var root = Path.GetFullPath(repoPath);
        if (string.IsNullOrWhiteSpace(relative) || relative.Trim() is "." or "./")
            return root;

        var combined = Path.GetFullPath(Path.Combine(root, relative.Trim()));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!combined.StartsWith(root, comparison))
            return null;
        return Directory.Exists(combined) ? combined : null;
    }

    /// <summary>Stops whatever currently owns this (project, target) pair.</summary>
    private async Task StopTargetAsync(string projectKey, string key)
    {
        foreach (var session in _sessions.Values.Where(s =>
            s.ProjectKey == projectKey && string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase)))
        {
            logger.LogInformation(
                "Replacing the running '{Label}' ({Key}) with a fresh start", session.Label, key);
            await CloseAsync(session.RunId);
        }
    }

    /// <summary>The full runner environment (already startup-scrubbed of secrets) plus
    /// terminal identity vars — this is the user's own app on their own machine, and the
    /// .env files it needs are already sitting in the working copy where they run it by
    /// hand.</summary>
    private static Dictionary<string, string> BuildEnvironment()
    {
        var env = new Dictionary<string, string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key?.ToString() is { Length: > 0 } key && entry.Value?.ToString() is { } value)
                env[key] = value;
        }
        env["TERM"] = "xterm-256color";
        env["COLORTERM"] = "truecolor";
        // Dev servers that detect CI go non-interactive and often exit after one build —
        // exactly the wrong mode for a Run button.
        env.Remove("CI");
        if (!OperatingSystem.IsWindows() && !env.ContainsKey("LANG"))
            env["LANG"] = "en_US.UTF-8";
        return env;
    }

    /// <summary>The runner going down must not leave dev servers behind — nothing else on
    /// the machine knows they belong to Orkka. Stop commands are skipped here on purpose:
    /// process exit is not the moment to wait thirty seconds on `docker compose stop`, and a
    /// database container left running is harmless and restartable.</summary>
    public void Dispose()
    {
        foreach (var runId in _sessions.Keys.ToList())
        {
            if (_sessions.TryRemove(runId, out var session))
                session.Dispose();
        }
    }
}

/// <summary>
/// One running app: its pseudo-terminal, the drain loop that keeps it unblocked, a bounded
/// scrollback for whoever attaches next, the live subscribers, and — for a target with a
/// port — a readiness watch that flips <see cref="Ready"/> when the port starts listening.
/// </summary>
internal sealed class AppRunSession(
    string runId,
    string projectKey,
    string key,
    string label,
    int? port,
    string cwd,
    string? stopCommand,
    IPtySession pty,
    int scrollbackBytes,
    ILogger logger) : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<byte[]> _scrollback = new();
    private readonly List<Channel<RunnerChunk>> _subscribers = [];
    private int _scrollbackSize;
    private RunnerChunk[]? _terminal;
    private bool _ready = port is null;

    public string RunId => runId;
    public string ProjectKey => projectKey;
    public string Key => key;
    public string Label => label;
    public int? Port => port;
    public string Cwd => cwd;
    public string? StopCommand => stopCommand;
    public IPtySession Pty => pty;
    public long StartedAtUnixMs { get; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public bool Exited => _terminal is not null;
    public bool Ready { get { lock (_gate) return _ready; } }

    private readonly CancellationTokenSource _stopping = new();

    public void StartDraining()
    {
        _ = Task.Run(DrainAsync, CancellationToken.None);
        if (port is { } p)
            _ = Task.Run(() => WatchReadinessAsync(p), CancellationToken.None);
    }

    /// <summary>
    /// A line from the runner itself, rendered in the target's log as if the process printed
    /// it — dimmed, so it reads as narration rather than app output. This is how "Created .env
    /// from .env.example" and "Installing dependencies…" reach the user.
    /// </summary>
    public void Announce(string text)
    {
        var line = $"{Dim}[orkka] {text}{Reset}\r\n";
        Publish(Encoding.UTF8.GetBytes(line));
    }

    /// <summary>Scrollback snapshot, whether the port is already ready, and a live feed. The
    /// live channel drops its oldest chunk when a slow consumer falls behind: losing a screen
    /// of build output beats stalling the drain loop, which would stall the app itself.</summary>
    public (byte[] Replay, bool Ready, Channel<RunnerChunk> Live) Subscribe()
    {
        var live = Channel.CreateBounded<RunnerChunk>(
            new BoundedChannelOptions(512)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            });
        lock (_gate)
        {
            var replay = _scrollback.SelectMany(b => b).ToArray();
            if (_terminal is { } terminal)
            {
                // Already dead: hand over the last words and close, so an attaching client
                // sees the exit code instead of waiting on a channel nothing will write to.
                foreach (var chunk in terminal)
                    live.Writer.TryWrite(chunk);
                live.Writer.TryComplete();
            }
            else
            {
                _subscribers.Add(live);
            }
            return (replay, _ready, live);
        }
    }

    public void Unsubscribe(Channel<RunnerChunk> live)
    {
        lock (_gate)
            _subscribers.Remove(live);
        live.Writer.TryComplete();
    }

    private async Task DrainAsync()
    {
        var buffer = new byte[16 * 1024];
        var exitCode = -1;
        try
        {
            while (true)
            {
                // The token races the read: on Windows a read already pending when the
                // pseudo-terminal is disposed can stay blocked indefinitely (see
                // IPtySession), which would strand this loop and its process handle.
                int read;
                try
                {
                    read = await pty.ReadOutputAsync(buffer, CancellationToken.None)
                        .WaitAsync(_stopping.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                if (read == 0)
                    break;

                var slice = buffer.AsSpan(0, read).ToArray();
                Publish(slice);
            }

            using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                exitCode = await pty.WaitForExitAsync(exitTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                // killed hard enough that the reaper never reported — surface the -1
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "App run {RunId} output drain failed", runId);
        }
        finally
        {
            if (exitCode != 0)
                ExplainExecutionPolicyRefusal();
            Complete(exitCode);
        }
    }

    /// <summary>A run PowerShell's execution policy refused gets one line saying so: Orkka never
    /// bypasses the policy, so the developer needs to know it was the policy, not the app.</summary>
    private void ExplainExecutionPolicyRefusal()
    {
        string text;
        lock (_gate)
            text = Encoding.UTF8.GetString(_scrollback.SelectMany(b => b).ToArray());
        if (Terminal.ShellResolver.ExecutionPolicyHint(text) is { } hint)
            Announce(hint);
    }

    /// <summary>Polls the declared port until it listens, the process exits, or the session
    /// stops. Cheap (a listener-table read every half second) and it ends itself.</summary>
    private async Task WatchReadinessAsync(int p)
    {
        try
        {
            while (!_stopping.IsCancellationRequested && !Exited)
            {
                if (PortInspector.IsPortInUse(p))
                {
                    MarkReady();
                    return;
                }
                await Task.Delay(500, _stopping.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // stopped — nothing to report
        }
    }

    private void MarkReady()
    {
        lock (_gate)
        {
            if (_ready || _terminal is not null)
                return;
            _ready = true;
            var chunk = new RunnerChunk("Ready", "");
            foreach (var subscriber in _subscribers)
                subscriber.Writer.TryWrite(chunk);
        }
    }

    private void Publish(byte[] data)
    {
        var chunk = new RunnerChunk("Pty", Convert.ToBase64String(data));
        lock (_gate)
        {
            _scrollback.Enqueue(data);
            _scrollbackSize += data.Length;
            while (_scrollbackSize > scrollbackBytes && _scrollback.Count > 1)
                _scrollbackSize -= _scrollback.Dequeue().Length;

            foreach (var subscriber in _subscribers)
                subscriber.Writer.TryWrite(chunk);
        }
    }

    /// <summary>Records the process's last words once, so both current subscribers and
    /// anyone attaching afterwards see the same ending.</summary>
    private void Complete(int exitCode)
    {
        lock (_gate)
        {
            if (_terminal is not null)
                return;
            _terminal =
            [
                new RunnerChunk("Exit", exitCode.ToString()),
                new RunnerChunk("Done", ""),
            ];
            foreach (var subscriber in _subscribers)
            {
                foreach (var chunk in _terminal)
                    subscriber.Writer.TryWrite(chunk);
                subscriber.Writer.TryComplete();
            }
            _subscribers.Clear();
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        pty.Dispose();
        Complete(-1);
        // The CTS is deliberately NOT disposed: the drain loop may not have observed the
        // cancellation yet, and disposing underneath it turns an orderly stop into an
        // ObjectDisposedException logged as a drain failure on every single stop. A cancelled
        // CTS with no timer holds nothing worth reclaiming early.
    }

    // Built from the code point: an inline escape is invisible in a diff.
    private static readonly string Dim = $"{(char)27}[2m";
    private static readonly string Reset = $"{(char)27}[0m";
}
