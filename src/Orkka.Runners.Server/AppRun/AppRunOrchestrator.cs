namespace Orkka.Runners.Server.AppRun;

using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using Orkka.LocalRunner.Contracts;

/// <summary>One target the user asked to start, as the browser sees it.</summary>
/// <param name="PortConflict">True when the start failed because something else holds the
/// port. A flag rather than something the UI infers from the error text, because it decides
/// whether to offer "free the port and run" — a real action, not a nicer message.</param>
/// <param name="Scrollback">Base64 VT bytes of everything printed so far, concatenated, so a
/// tab attaching late renders the log it missed. Null when there is nothing to replay.</param>
/// <param name="Ready">True once the target's port accepts connections (immediately for a
/// portless target). The UI shows "starting…" until then, so the link it offers is one that
/// loads.</param>
/// <param name="PreviewUrl">Where to open the running app — the target URL plus its preview
/// path. Null for targets that serve nothing a browser can open.</param>
public sealed record AppRunSnapshot(
    string RunId,
    string Key,
    string Label,
    string Kind,
    string RepoId,
    int? Port,
    string? Url,
    string Status,
    int? ExitCode,
    string? Error,
    bool PortConflict,
    DateTimeOffset StartedAt,
    string? Scrollback,
    bool Ready = true,
    string? PreviewUrl = null);

public static class AppRunStatuses
{
    public const string Running = "running";
    public const string Exited = "exited";
    public const string Failed = "failed";
}

/// <summary>A stored target resolved into something startable — the browser-facing edge
/// (Local's hub, the hosted API's endpoints) does this, so the orchestrator never touches
/// the database.</summary>
public sealed record PlannedTarget(
    string RunId,
    string Key,
    string Label,
    string Kind,
    string RepoId,
    string Command,
    string? WorkingDirectory,
    int? Port,
    string? Url,
    string[] DependsOn,
    string? InstallCommand = null,
    string? StopCommand = null,
    string? PreviewPath = null,
    bool RestartOnMerge = false);

/// <summary>
/// How app-run events reach the browsers watching. Each edition supplies its own path:
/// Orkka Local broadcasts on its in-process AppRunHub; the hosted Worker forwards through
/// the API's TaskHub (the browsers are connected there, not here). The user id scopes the
/// audience — runs execute on ONE user's machine, and only that user watches them.
/// </summary>
public interface IAppRunEvents
{
    Task OutputAsync(
        Ulid orgId, string userId, Ulid projectId, string runId, string base64,
        CancellationToken ct);

    Task StateAsync(
        Ulid orgId, string userId, Ulid projectId, IReadOnlyList<AppRunSnapshot> snapshots,
        CancellationToken ct);
}

/// <summary>
/// Tracks the apps the Run button started, and forwards their output to whoever is watching.
///
/// A singleton rather than per-connection state, because a dev server is not a property of a
/// browser tab: the user starts it, navigates away, reloads, closes the panel, and expects it
/// to still be serving. The hub connection comes and goes; this does not.
///
/// Must live in the process that holds the runner socket (the chunk channels exist only
/// there): Orkka Local's host, or the hosted Worker replica owning the user's connection.
/// Runs are keyed per (org, user) because on hosted, two members can each run the same
/// project on their own machines.
///
/// It keeps a bounded scrollback per run for the same reason the runner does — a tab that
/// attaches five minutes in should see why the build failed, not an empty pane.
/// </summary>
public sealed class AppRunOrchestrator(
    IRunnerLinkProvider links,
    IAppRunEvents events,
    ILogger<AppRunOrchestrator> logger)
{
    /// <summary>Raw output bytes kept per run. Big enough for a failed build's stack trace,
    /// small enough that a dozen chatty dev servers cannot grow the host without bound.</summary>
    private const int MaxScrollbackBytes = 384 * 1024;

    /// <summary>A non-zero exit this soon after starting is a start that failed — a missing
    /// module, a port the app could not bind, a bad env — not an app that ran and stopped.
    /// The UI shows those two very differently: a red dot with the reason, or a grey one.</summary>
    internal static readonly TimeSpan EarlyExitWindow = TimeSpan.FromSeconds(15);

    private sealed class RunState
    {
        public required string RunId { get; init; }
        public required Ulid OrgId { get; init; }
        public required string UserId { get; init; }
        public required Ulid ProjectId { get; init; }
        public required string Key { get; init; }
        public required string Label { get; init; }
        public required string Kind { get; init; }
        public required string RepoId { get; init; }
        public int? Port { get; init; }
        public string? Url { get; init; }
        public string? PreviewUrl { get; init; }
        public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>The spec this run was started from — what a restart re-sends. Null for
        /// adopted runs, which this host never started and cannot faithfully restart.</summary>
        public PlannedTarget? Plan { get; init; }
        public bool ForceFreePorts { get; init; }

        public string Status { get; set; } = AppRunStatuses.Running;
        public int? ExitCode { get; set; }
        public string? Error { get; set; }
        public bool Ready { get; set; }

        /// <summary>Null for adopted runs: the output is going to a channel this host no
        /// longer owns, so claiming an empty scrollback would be a lie the UI would render
        /// as "nothing has happened".</summary>
        public ScrollbackBuffer? Scrollback { get; init; } = new(MaxScrollbackBytes);

        public CancellationTokenSource? Pump { get; set; }
    }

    private readonly ConcurrentDictionary<string, RunState> _runs = new();

    /// <summary>The runner-side session key: sessions on the user's machine are grouped per
    /// project. Stable wire format — Orkka Local runners already store sessions under it.</summary>
    public static string ProjectKeyOf(Ulid projectId) => $"apprun:{projectId}";

    /// <summary>The URL a browser opens for a target: its base URL plus the preview path.
    /// Null when the target has no URL — a worker, a database — so the UI offers no link.</summary>
    public static string? PreviewUrlOf(string? url, string? previewPath)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        var path = string.IsNullOrWhiteSpace(previewPath) ? "/" : previewPath.Trim();
        if (!path.StartsWith('/'))
            path = "/" + path;
        return url.TrimEnd('/') + (path == "/" ? "" : path);
    }

    /// <summary>Everything this host knows to be running for this user's copy of the
    /// project, newest last.</summary>
    public IReadOnlyList<AppRunSnapshot> Snapshot(Ulid orgId, string userId, Ulid projectId) =>
    [
        .. _runs.Values
            .Where(r => r.OrgId == orgId && r.UserId == userId && r.ProjectId == projectId)
            .OrderBy(r => r.StartedAt)
            .Select(ToSnapshot)
    ];

    /// <summary>The (org, user) pairs with runs of this project — on hosted, every member
    /// running it on their own machine. What a merge-triggered restart fans out over.</summary>
    public IReadOnlyList<(Ulid OrgId, string UserId)> AudiencesOf(Ulid projectId) =>
    [
        .. _runs.Values
            .Where(r => r.ProjectId == projectId)
            .Select(r => (r.OrgId, r.UserId))
            .Distinct()
    ];

    /// <summary>
    /// Starts the given targets on the user's machine. The caller has already resolved the
    /// project's stored config into specs — this type deals only with the runner and with
    /// keeping the browser informed.
    /// </summary>
    public async Task<IReadOnlyList<AppRunSnapshot>> StartAsync(
        Ulid orgId,
        string userId,
        Ulid projectId,
        IReadOnlyList<PlannedTarget> planned,
        bool forceFreePorts,
        CancellationToken ct)
    {
        var runner = links.GetLink(orgId, userId);
        var specs = planned.Select(p => new AppRunTargetSpec(
            p.RunId,
            p.Key,
            p.Label,
            RunnerTokens.UserRepo(orgId.ToString(), p.RepoId),
            p.WorkingDirectory,
            p.Command,
            p.Port,
            p.DependsOn,
            p.InstallCommand,
            p.StopCommand,
            p.Kind)).ToArray();

        // Channels must exist BEFORE the runner starts streaming, or the first chunks — the
        // ones that say why a command failed — race into the void.
        foreach (var spec in specs)
            runner.OpenRunChannel(spec.RunId);

        StartAppRunResult result;
        try
        {
            result = await runner.InvokeAsync<StartAppRunResult>(
                RunnerProtocol.StartAppRun,
                [new StartAppRunRequest(ProjectKeyOf(projectId), specs, forceFreePorts)],
                ct,
                // Starting a stack staggers its spawns and waits on dependency ports; the
                // default op budget is generous but this is one of the few ops whose
                // duration scales with the request.
                opTimeout: TimeSpan.FromMinutes(3));
        }
        catch
        {
            foreach (var spec in specs)
                runner.CloseRunChannel(spec.RunId);
            throw;
        }

        var snapshots = new List<AppRunSnapshot>(planned.Count);
        foreach (var target in result.Targets)
        {
            var plan = planned.First(p => p.RunId == target.RunId);
            if (!target.Started)
            {
                runner.CloseRunChannel(target.RunId);
                // Kept out of _runs on purpose: there is no process, so there is nothing to
                // stop, re-attach to, or replace. The failure travels back in this response.
                snapshots.Add(new AppRunSnapshot(
                    target.RunId, plan.Key, plan.Label, plan.Kind, plan.RepoId, plan.Port,
                    plan.Url, AppRunStatuses.Failed, null,
                    target.Error ?? "Could not start.", target.Conflict is not null,
                    DateTimeOffset.UtcNow, ConflictHint(target.Conflict), Ready: false,
                    PreviewUrl: null));
                continue;
            }

            // A restarted target reuses its key: drop the previous run's state, since the
            // runner has already killed the process behind it.
            foreach (var stale in _runs.Values.Where(r =>
                r.OrgId == orgId && r.UserId == userId && r.ProjectId == projectId
                && r.Key == plan.Key && r.RunId != target.RunId).ToList())
            {
                Forget(stale.RunId);
            }

            var state = new RunState
            {
                RunId = target.RunId,
                OrgId = orgId,
                UserId = userId,
                ProjectId = projectId,
                Key = plan.Key,
                Label = plan.Label,
                Kind = plan.Kind,
                RepoId = plan.RepoId,
                Port = plan.Port,
                Url = plan.Url,
                PreviewUrl = PreviewUrlOf(plan.Url, plan.PreviewPath),
                Plan = plan,
                ForceFreePorts = forceFreePorts,
                Ready = plan.Port is null,
            };
            _runs[target.RunId] = state;
            StartPump(state);
            snapshots.Add(ToSnapshot(state));
        }

        await BroadcastAsync(orgId, userId, projectId, ct);
        return snapshots;
    }

    /// <summary>Stops one run, or every run of the project. Always asks the runner too: its
    /// processes are the truth, and a host that only forgot about them would leave dev
    /// servers holding ports with nothing left to stop them.</summary>
    public async Task StopAsync(
        Ulid orgId, string userId, Ulid projectId, string? runId, CancellationToken ct)
    {
        try
        {
            await links.GetLink(orgId, userId).InvokeAsync<RunnerOpResult>(
                RunnerProtocol.StopAppRun,
                [new StopAppRunRequest(ProjectKeyOf(projectId), runId)],
                ct);
        }
        catch (Exception ex)
        {
            // Runner offline: its processes died with it, so forgetting them here is right.
            logger.LogInformation("Stop app run: runner did not answer ({Reason})", ex.Message);
        }

        foreach (var state in _runs.Values
            .Where(r => r.OrgId == orgId && r.UserId == userId && r.ProjectId == projectId
                && (runId is null || r.RunId == runId))
            .ToList())
        {
            Forget(state.RunId);
        }
        await BroadcastAsync(orgId, userId, projectId, ct);
    }

    /// <summary>
    /// Restarts the project's running targets that match <paramref name="select"/> — the
    /// same specs they were started from, so a merge that changed the API's code is picked
    /// up by a target that does not hot-reload. Adopted runs (started by a previous host
    /// process) have no spec here and are left alone. Returns how many were restarted.
    /// </summary>
    public async Task<int> RestartAsync(
        Ulid orgId, string userId, Ulid projectId,
        Func<PlannedTarget, bool> select, CancellationToken ct)
    {
        var plans = _runs.Values
            .Where(r => r.OrgId == orgId && r.UserId == userId && r.ProjectId == projectId
                && r.Status == AppRunStatuses.Running && r.Plan is not null && select(r.Plan!))
            .Select(r => (r.Plan!, r.ForceFreePorts))
            .ToList();
        if (plans.Count == 0)
            return 0;

        // Fresh run ids: the runner keys sessions by (project, key) and replaces the old
        // process itself, but the run id is the stream identity and must not be reused.
        var restarted = plans
            .Select(p => p.Item1 with { RunId = Ulid.NewUlid().ToString() })
            .ToList();
        var force = plans.Any(p => p.ForceFreePorts);
        logger.LogInformation(
            "Restarting {Count} target(s) of project {ProjectId}: {Keys}",
            restarted.Count, projectId, string.Join(", ", restarted.Select(r => r.Key)));
        await StartAsync(orgId, userId, projectId, restarted, force, ct);
        return restarted.Count;
    }

    /// <summary>
    /// Reconciles with the machine: adopts processes still running that this host has lost
    /// track of (its own restart, or a runner reconnect, both of which drop the chunk
    /// channels) and forgets ones that are gone.
    ///
    /// Without this the user is left with a dev server holding port 3000 that Orkka claims
    /// is not running — and no way to stop it from the UI.
    /// </summary>
    public async Task ReconcileAsync(
        Ulid orgId, string userId, Ulid projectId, CancellationToken ct)
    {
        var runner = links.GetLink(orgId, userId);
        AppRunStatusResult status;
        try
        {
            status = await runner.InvokeAsync<AppRunStatusResult>(
                RunnerProtocol.GetAppRunStatus, [], ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "App run reconcile skipped — runner unavailable");
            return;
        }

        var projectKey = ProjectKeyOf(projectId);
        var live = status.Sessions.Where(s => s.ProjectKey == projectKey && s.Running).ToList();
        var liveIds = live.Select(s => s.RunId).ToHashSet(StringComparer.Ordinal);

        foreach (var state in _runs.Values
            .Where(r => r.OrgId == orgId && r.UserId == userId && r.ProjectId == projectId)
            .ToList())
        {
            if (!liveIds.Contains(state.RunId) && state.Status == AppRunStatuses.Running)
            {
                state.Status = AppRunStatuses.Exited;
                Forget(state.RunId);
            }
            else if (liveIds.Contains(state.RunId)
                && live.First(s => s.RunId == state.RunId).Ready && !state.Ready)
            {
                // The runner saw the port open while we were not listening.
                state.Ready = true;
            }
        }

        foreach (var session in live.Where(s => !_runs.ContainsKey(s.RunId)))
        {
            var url = session.Port is { } port ? $"http://localhost:{port}" : null;
            var adopted = new RunState
            {
                RunId = session.RunId,
                OrgId = orgId,
                UserId = userId,
                ProjectId = projectId,
                Key = session.Key,
                Label = session.Label,
                Kind = "other",
                RepoId = "",
                Port = session.Port,
                Url = url,
                PreviewUrl = url,
                Ready = session.Ready,
                StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(session.StartedAtUnixMs),
                Scrollback = new ScrollbackBuffer(MaxScrollbackBytes),
            };
            _runs[session.RunId] = adopted;

            // Re-open the stream: the runner replays what it kept, so an adopted run's log
            // pane is not blank for a process that has been up for an hour.
            try
            {
                runner.OpenRunChannel(session.RunId);
                var attached = await runner.InvokeAsync<RunnerOpResult>(
                    RunnerProtocol.AttachAppRun, [new AttachAppRunRequest(session.RunId)], ct);
                if (attached.Success)
                {
                    StartPump(adopted);
                }
                else
                {
                    runner.CloseRunChannel(session.RunId);
                    _runs.TryRemove(session.RunId, out _);
                }
            }
            catch (Exception ex)
            {
                logger.LogInformation(
                    "Could not re-attach to app run {RunId} ({Reason})", session.RunId, ex.Message);
                runner.CloseRunChannel(session.RunId);
            }
        }

        await BroadcastAsync(orgId, userId, projectId, ct);
    }

    public async Task InputAsync(string runId, string dataBase64, CancellationToken ct)
    {
        if (!_runs.TryGetValue(runId, out var state))
            return;
        await links.GetLink(state.OrgId, state.UserId).SendAsync(
            RunnerProtocol.WriteAppRunInput, new AppRunInputRequest(runId, dataBase64), ct);
    }

    public async Task ResizeAsync(string runId, int cols, int rows, CancellationToken ct)
    {
        if (!_runs.TryGetValue(runId, out var state))
            return;
        await links.GetLink(state.OrgId, state.UserId).SendAsync(
            RunnerProtocol.ResizeAppRun, new ResizeAppRunRequest(runId, cols, rows), ct);
    }

    /// <summary>True when the given run belongs to this user — Input/Resize are keystrokes
    /// on someone's machine, so the edge must be able to refuse a runId it merely knows.</summary>
    public bool Owns(Ulid orgId, string userId, string runId) =>
        _runs.TryGetValue(runId, out var state)
        && state.OrgId == orgId && state.UserId == userId;

    /// <summary>Forwards one run's output to the project's watchers for as long as the
    /// process lives. Runs past any hub invocation, so it only touches singletons.</summary>
    private void StartPump(RunState state)
    {
        var cts = new CancellationTokenSource();
        state.Pump = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                var runner = links.GetLink(state.OrgId, state.UserId);
                await foreach (var chunk in runner.ConsumeRunAsync(state.RunId, cts.Token))
                {
                    switch (chunk.Type)
                    {
                        case "Pty":
                            Append(state, chunk.Content);
                            await events.OutputAsync(
                                state.OrgId, state.UserId, state.ProjectId,
                                state.RunId, chunk.Content, cts.Token);
                            break;
                        case "Ready":
                            if (!state.Ready)
                            {
                                state.Ready = true;
                                await BroadcastAsync(
                                    state.OrgId, state.UserId, state.ProjectId, cts.Token);
                            }
                            break;
                        case "Exit":
                            state.ExitCode = int.TryParse(chunk.Content, out var code) ? code : null;
                            ClassifyExit(state);
                            await BroadcastAsync(
                                state.OrgId, state.UserId, state.ProjectId, cts.Token);
                            break;
                        case "Error":
                            state.Error = chunk.Content;
                            state.Status = AppRunStatuses.Failed;
                            await BroadcastAsync(
                                state.OrgId, state.UserId, state.ProjectId, cts.Token);
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // stopped by its owner — normal teardown
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "App run {RunId} output pump failed", state.RunId);
            }
        }, CancellationToken.None);
    }

    /// <summary>A process that died non-zero moments after starting did not run — it failed
    /// to start, and the last thing it printed is almost always why. Surface that as the
    /// error so the user does not have to scroll a log to learn the port was taken.</summary>
    private static void ClassifyExit(RunState state)
    {
        if (IsEarlyFailure(state.ExitCode, state.StartedAt, DateTimeOffset.UtcNow))
        {
            state.Status = AppRunStatuses.Failed;
            state.Error = FailureReason(state.ExitCode, state.Scrollback?.LastLines(6));
        }
        else
        {
            state.Status = AppRunStatuses.Exited;
        }
    }

    internal static bool IsEarlyFailure(int? exitCode, DateTimeOffset startedAt, DateTimeOffset now)
        => exitCode is not null and not 0 && now - startedAt < EarlyExitWindow;

    internal static string FailureReason(int? exitCode, string? tail)
    {
        var prefix = $"Exited with code {exitCode} right after starting.";
        return string.IsNullOrWhiteSpace(tail) ? prefix : $"{prefix}\n{tail}";
    }

    private static void Append(RunState state, string base64)
        => state.Scrollback?.Append(base64);

    /// <summary>Drops the host's state for a run and stops consuming its channel.</summary>
    private void Forget(string runId)
    {
        if (!_runs.TryRemove(runId, out var state))
            return;
        state.Pump?.Cancel();
        state.Pump?.Dispose();
        links.GetLink(state.OrgId, state.UserId).CloseRunChannel(runId);
    }

    private Task BroadcastAsync(Ulid orgId, string userId, Ulid projectId, CancellationToken ct) =>
        events.StateAsync(orgId, userId, projectId, Snapshot(orgId, userId, projectId), ct);

    private static AppRunSnapshot ToSnapshot(RunState state)
    {
        var scrollback = state.Scrollback?.ToBase64();
        return new AppRunSnapshot(
            state.RunId, state.Key, state.Label, state.Kind, state.RepoId, state.Port,
            state.Url, state.Status, state.ExitCode, state.Error, false, state.StartedAt,
            scrollback,
            Ready: state.Status == AppRunStatuses.Running && state.Ready,
            PreviewUrl: state.Status == AppRunStatuses.Running ? state.PreviewUrl : null);
    }

    /// <summary>Turns a port conflict into the extra sentence that makes it actionable. It
    /// rides the snapshot's scrollback field so a failed target still has something to show
    /// in its log pane.</summary>
    private static string? ConflictHint(AppRunPortConflict? conflict) => conflict is null
        ? null
        : Convert.ToBase64String(Encoding.UTF8.GetBytes(
            // Amber, because this is the one failure the user can fix from the panel itself.
            $"{Amber}Port {conflict.Port} is held by " +
            $"{conflict.ProcessName ?? "another process"}" +
            (conflict.Pid is { } pid ? $" (pid {pid})" : "") +
            $".{Reset}\r\n"));

    // Built from the code point: an inline escape is invisible in a diff, and this string
    // is the one place in the server that emits terminal control bytes.
    private static readonly string Amber = $"{(char)27}[33m";
    private static readonly string Reset = $"{(char)27}[0m";
}
