namespace Orkka.LocalRunner.Contracts;

// Contracts for "run this project on my machine" — the Run button. Distinct from the
// agent/QA run paths: these processes are long-lived, belong to the human rather than to a
// task, and outlive the browser tab that started them.

/// <summary>
/// One process to start. RepoToken is a <see cref="RunnerTokens.UserRepo"/> token — the
/// runner resolves the real working copy itself, so no filesystem path crosses the wire.
/// WorkingDirectory is relative to that repo root (monorepo packages: "apps/web") and is
/// rejected runner-side if it escapes it.
/// </summary>
/// <param name="InstallCommand">Run once, before <paramref name="Command"/>, when the
/// target's dependencies are evidently missing (no node_modules for a JS app) — see the
/// runner's prepare step. Additive; an older runner ignores it.</param>
/// <param name="StopCommand">Run (bounded) before the process tree is killed. Needed by
/// `docker compose up` targets, whose containers otherwise outlive the PTY.</param>
/// <param name="Kind">The stored target's kind ("web", "api", "infra", …). The runner uses
/// it only for the infra/Docker readiness probe; the host owns everything else about it.</param>
public sealed record AppRunTargetSpec(
    string RunId,
    string Key,
    string Label,
    string RepoToken,
    string? WorkingDirectory,
    string Command,
    int? Port,
    string[] DependsOn,
    string? InstallCommand = null,
    string? StopCommand = null,
    string? Kind = null);

/// <summary>
/// Starts every selected target of one project. ProjectKey scopes replace/stop semantics:
/// starting a target whose key is already live under the same ProjectKey kills the old
/// process first, which is what makes the Run button idempotent rather than a way to
/// accumulate orphaned dev servers.
///
/// ForceFreePorts extends that to processes Orkka did NOT start: without it a port held by
/// a foreign process fails its target with an <see cref="AppRunPortConflict"/> so the user
/// can decide, because killing an unrelated process on a developer's machine is not a
/// default anyone should get by surprise.
/// </summary>
public sealed record StartAppRunRequest(
    string ProjectKey,
    AppRunTargetSpec[] Targets,
    bool ForceFreePorts,
    int Cols = 120,
    int Rows = 30);

/// <summary>The process found holding a port a target wants. Pid/ProcessName are
/// best-effort — the OS lookup can fail or the process can be owned by another user.</summary>
public sealed record AppRunPortConflict(int Port, int? Pid, string? ProcessName);

public sealed record AppRunTargetStart(
    string RunId,
    string Key,
    bool Started,
    string? Cwd,
    string? Shell,
    int? Pid,
    AppRunPortConflict? Conflict,
    string? Error,
    CommandApproval? Approval = null);

public sealed record StartAppRunResult(AppRunTargetStart[] Targets);

/// <summary>Null <paramref name="RunId"/> stops every target of the project — the "stop
/// everything" the UI offers and what the host calls before a fresh start.</summary>
public sealed record StopAppRunRequest(string ProjectKey, string? RunId);

/// <summary>What is still alive on the machine. Lets the host adopt sessions it lost track
/// of (its own restart) instead of leaving the user with dev servers it can no longer
/// stop.</summary>
/// <param name="Ready">True once the target's port accepts connections (always true for a
/// portless target). What turns "starting…" into a link the user can open without landing
/// on a dead tab.</param>
public sealed record AppRunSessionInfo(
    string RunId,
    string ProjectKey,
    string Key,
    string Label,
    int? Port,
    bool Running,
    long StartedAtUnixMs,
    bool Ready = true);

public sealed record AppRunStatusResult(AppRunSessionInfo[] Sessions);

/// <summary>Re-opens the output stream of a run that is still alive but whose channel the
/// host lost — its own restart, or a runner reconnect. The runner replays the scrollback it
/// kept and then resumes live output, so the log pane is not blank for a process that has
/// been running for an hour.</summary>
public sealed record AttachAppRunRequest(string RunId);

/// <summary>Keystrokes for a live app-run process, base64-encoded. Dev tooling is
/// interactive far more often than it looks — Expo, Flutter and Vite all drive reloads off
/// single keypresses — so the log pane is a real terminal, not a read-only tail.</summary>
public sealed record AppRunInputRequest(string RunId, string DataBase64);

public sealed record ResizeAppRunRequest(string RunId, int Cols, int Rows);
