namespace Orkka.LocalRunner;

using Microsoft.AspNetCore.SignalR.Client;
using Orkka.LocalRunner.Contracts;

/// <summary>
/// One workspace's SignalR connection to hosted Orkka: its own token (so its own org and
/// user), its own Hello and InstanceId, its own tracker connections and git identity — and
/// the dispatcher for the operations that host sends. The runner always dials OUT — nothing
/// ever listens on the user's machine. <see cref="RunnerSupervisor"/> runs one per profile.
/// </summary>
public sealed class RunnerSession(
    Cli.WorkspaceProfile profile,
    string instanceId,
    RunnerOperations ops,
    Apps.AppRunManager appRuns,
    ProviderDetection detection,
    ForgeCli forgeCli,
    Trackers.TrackerService trackers,
    ProviderAuthOverrides authOverrides,
    Cli.ConsoleStatus status,
    RunnerHostLink hostLink,
    ILogger<RunnerSession> logger,
    // Stores a renewed runner token for this workspace (the OS secret store). Null in tests that
    // never rotate; then a rotation offered by the host is declined.
    Func<string, bool>? persistToken = null,
    // The repository commands this machine refused for this workspace, newest first — reported in
    // the Hello so the Machine page can list them. Null in tests.
    Func<IReadOnlyList<CommandApproval>>? pendingApprovals = null)
{
    /// <summary>How many refused commands a Hello reports.</summary>
    internal const int MaxReportedApprovals = 20;

    // Every streamed run this runner has in flight — see RunnerRunRegistry for why the book
    // is kept by the stream's real lifetime and not by the hub send's return.
    private readonly RunnerRunRegistry _runs = new();
    private HubConnection? _connection;

    // How long CancelTaskRuns / CleanupWorktree wait for a cancelled run's agent process to
    // exit before proceeding anyway (the lock-tolerant worktree delete is the backstop).
    private static readonly TimeSpan RunTeardownWait = TimeSpan.FromSeconds(30);
    // Machine-wide: every session probes the same CLIs, so their warnings print once.
    private static string? _providerWarnFingerprint;

    /// <summary>Minted once per session per process: lets the host distinguish this session
    /// reconnecting from a second runner process fighting it for the single link slot (see
    /// RunnerHello.InstanceId). Also the tracker poll lease holder (TrackerSyncRequest.InstanceId).</summary>
    public string InstanceId { get; } = instanceId;

    public string Workspace => profile.Name;

    public RunnerHostLink HostLink => hostLink;

    public Trackers.TrackerService Trackers => trackers;

    public RunnerOperations Operations => ops;

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        // A run that failed to authenticate (or the first clean one after it) changes the
        // provider roster the host holds for this runner — announce it now rather than at
        // the next 60s tick, so the status panel and the model pickers flip while the user
        // is still looking at the failed run.
        authOverrides.Changed += OnAuthOverridesChanged;
        try
        {
            await RunSessionsAsync(stoppingToken);
        }
        finally
        {
            authOverrides.Changed -= OnAuthOverridesChanged;
            await StopRunsAsync();
        }
    }

    /// <summary>How long a stopping session waits for its cancelled runs' agent processes to
    /// exit; inside the runner's own shutdown window, after which <c>AgentProcesses.KillAll</c>
    /// ends whatever is left.</summary>
    internal static TimeSpan ShutdownRunWait { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The session is over (the runner is stopping): cancel every run still in flight and wait
    /// for the agents to exit. Without this the process could end first and leave the CLIs
    /// running, orphaned (R5, 2026-11-18). The host parks those tasks when the link drops and
    /// re-queues them when the runner comes back.
    /// </summary>
    internal async Task<int> StopRunsAsync()
    {
        var count = await _runs.CancelAllAsync(ShutdownRunWait);
        if (count > 0)
            logger.LogInformation("[{Workspace}] Stopped {Count} run(s) in flight", profile.Name, count);
        return count;
    }

    private void OnAuthOverridesChanged()
    {
        if (_connection is not { State: HubConnectionState.Connected } connection)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await SendHelloAsync(connection, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogDebug("Re-Hello after auth change failed: {Message}", ex.Message);
            }
        });
    }

    /// <summary>How long without a message from the host before this side redials. Kept equal
    /// to the hub's <c>RunnerHubTimeouts.ClientTimeout</c>.</summary>
    internal static readonly TimeSpan HostTimeout = TimeSpan.FromMinutes(2);

    /// <summary>How often a connected session asks whether its token is due for renewal.</summary>
    internal static readonly TimeSpan RotationCheckInterval = TimeSpan.FromHours(6);

    private async Task RunSessionsAsync(CancellationToken stoppingToken)
    {
        var hostUrl = profile.HostUrl.TrimEnd('/');
        var token = profile.Token;
        var nextRotationCheck = DateTimeOffset.MinValue;
        // The hub validates the Authorization header. Per-user orkr_ tokens must never
        // appear in URLs — ingress access logs would capture them.
        var hubUrl = $"{hostUrl}{RunnerProtocol.HubPath}";
        var appUrl = Cli.ConnectCommand.AppUrlFor(hostUrl);

        // Outer session loop: SignalR's automatic reconnect gives up after ~30s, which a
        // host container rebuild easily exceeds. When a connection dies for good we build
        // a fresh one and dial again — the runner must survive host restarts indefinitely.
        while (!stoppingToken.IsCancellationRequested)
        {
            var connection = new HubConnectionBuilder()
                .WithUrl(hubUrl, options =>
                {
                    options.Headers.Add("Authorization", $"Bearer {token}");
                    // Hosted hub runs several Worker replicas behind one ingress. The
                    // negotiate + transport pair must land on the same pod; skipping
                    // negotiation makes the whole connection ONE WebSocket request, so no
                    // load-balancer affinity is needed at all (the ingress carries cookie
                    // affinity too, for daemons older than this line).
                    options.SkipNegotiation = true;
                    options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;
                })
                .WithAutomaticReconnect()
                .Build();
            // The default 30 s dropped this machine four times in twenty minutes while it was
            // compiling the app, running an agent and diffing a worktree at once (2026-09-17);
            // every drop failed the runs in flight and started the work over. Mirrors the
            // hub's RunnerHubTimeouts.ClientTimeout.
            connection.ServerTimeout = HostTimeout;
            _connection = connection;
            // Published for the components that call the host rather than answer it — today the
            // interactive approval gate. Cleared below when this connection is torn down, so a
            // prompt raised between sessions fails closed instead of dialling a dead socket.
            hostLink.Connection = connection;

            RegisterHandlers(connection);

            var closedForGood = new TaskCompletionSource();
            connection.Reconnecting += ex =>
            {
                status.Reconnecting(profile.Name);
                logger.LogInformation("[{Workspace}] Connection to host dropped — reconnecting. Reason: {Reason}",
                    profile.Name, Redact(ex?.Message ?? "(none)", token));
                return Task.CompletedTask;
            };
            connection.Reconnected += async _ =>
            {
                status.Connected(appUrl, profile.Name);
                logger.LogInformation("Reconnected to host — re-announcing");
                await SendHelloAsync(connection, stoppingToken);
            };
            connection.Closed += _ =>
            {
                // Fires on shutdown too (we dispose the connection) — stay quiet then.
                if (!stoppingToken.IsCancellationRequested)
                {
                    status.ConnectionLost(profile.Name);
                    logger.LogInformation("Connection to host closed — will redial");
                }
                closedForGood.TrySetResult();
                return Task.CompletedTask;
            };

            // Dial loop: the host container may come up after the runner.
            var connected = false;
            while (!stoppingToken.IsCancellationRequested && !connected)
            {
                try
                {
                    await connection.StartAsync(stoppingToken);
                    status.Connected(appUrl, profile.Name);
                    logger.LogInformation("[{Workspace}] Connected to Orkka at {HostUrl}", profile.Name, hostUrl);
                    await SendHelloAsync(connection, stoppingToken);
                    connected = true;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    logger.LogWarning("[{Workspace}] {HostUrl} not reachable ({Message}) — retrying in 5s",
                        profile.Name, hostUrl, Redact(ex.Message, token));
                    status.Unreachable(hostUrl, profile.Name);
                    try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                    catch (OperationCanceledException) { break; }
                }
            }

            // Session loop: periodic re-Hello keeps claude credential status fresh (the
            // user may /login while we run) until the connection dies or we shut down.
            try
            {
                while (!stoppingToken.IsCancellationRequested && !closedForGood.Task.IsCompleted)
                {
                    var tick = Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
                    await Task.WhenAny(tick, closedForGood.Task);
                    if (stoppingToken.IsCancellationRequested || closedForGood.Task.IsCompleted)
                        break;
                    if (connection.State != HubConnectionState.Connected)
                        continue;
                    await SendHelloAsync(connection, stoppingToken);

                    if (DateTimeOffset.UtcNow < nextRotationCheck)
                        continue;
                    nextRotationCheck = DateTimeOffset.UtcNow + RotationCheckInterval;
                    if (await TryRotateTokenAsync(connection, stoppingToken) is { } renewed)
                    {
                        // Redial with the new token: the old one only has a short grace left.
                        token = renewed;
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { /* shutting down */ }

            hostLink.Connection = null;
            await connection.DisposeAsync();
        }
    }

    /// <summary>
    /// Asks the host whether this connection's token is close to expiry; when it is, stores the
    /// renewed one in the OS secret store and returns it. Null: not due, or it could not be
    /// stored (the current token then keeps working until it lapses, and the next check retries).
    /// </summary>
    private async Task<string?> TryRotateTokenAsync(HubConnection connection, CancellationToken ct)
    {
        try
        {
            var rotation = await connection.InvokeAsync<RunnerTokenRotation?>(RunnerProtocol.RotateToken, ct);
            if (rotation?.NewToken is not { Length: > 0 } fresh)
                return null;
            if (persistToken?.Invoke(fresh) != true)
            {
                logger.LogWarning("[{Workspace}] Could not store the renewed runner token — keeping the current one",
                    profile.Name);
                return null;
            }

            logger.LogInformation("[{Workspace}] Runner token renewed (valid until {ExpiresAt:yyyy-MM-dd}) — reconnecting with it",
                profile.Name, rotation.ExpiresAt);
            return fresh;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An older host without the method, or a blip: try again at the next check.
            logger.LogDebug("[{Workspace}] Token renewal check failed: {Message}", profile.Name, ex.Message);
            return null;
        }
    }

    /// <summary>A token never belongs in a log line; SignalR's own messages do not carry it,
    /// but a URL in an exception might.</summary>
    internal static string Redact(string text, string token)
        => string.IsNullOrEmpty(token) ? text : text.Replace(token, "***", StringComparison.Ordinal);

    private async Task SendHelloAsync(HubConnection connection, CancellationToken ct)
        => await connection.SendAsync(RunnerProtocol.Hello, await BuildHelloAsync(ct), ct);

    /// <summary>What this session announces: the machine's providers and CLIs, and THIS
    /// workspace's instance id and tracker connections. Never a token.</summary>
    internal async Task<RunnerHello> BuildHelloAsync(CancellationToken ct)
    {
        // This workspace's view: its own AI sign-in overrides decide each provider's auth mode.
        var providers = detection.DetectAll(profile.Name);
        var claude = providers.First(p => p.Provider == RunnerProviders.Claude);

        // The session loop re-Hellos every 60s to keep credential status fresh — only
        // re-log the provider warnings when something actually changed, or they spam
        // the terminal once a minute.
        var fingerprint = string.Join("|",
            providers.Select(p => $"{p.Provider}:{p.CliFound}:{p.Authenticated}:{p.Detail}"));
        if (fingerprint != _providerWarnFingerprint)
        {
            _providerWarnFingerprint = fingerprint;

            // Any ONE connected provider runs the core pipeline (utilities and merge
            // resolution fall back automatically; only the QA browser lane needs claude).
            // Hard warning only when NOTHING is connected; otherwise per-CLI login hints.
            if (!providers.Any(p => p.Authenticated))
            {
                logger.LogWarning(
                    "No AI provider is connected — agent runs will fail until one is ready (claude, codex, gemini, grok, copilot, opencode, or a local Ollama server)");
            }
            foreach (var p in providers.Where(p => p.CliFound && !p.Authenticated))
                logger.LogWarning(
                    "{Provider} found but not ready — {Remedy}",
                    p.Provider,
                    p.Detail ?? $"run `{ProviderDetection.LoginCommand(p.Provider)}` to use its models");

            // The shim trap silently kills claude runs — always worth surfacing when the
            // claude CLI is present at all.
            if (claude.CliFound && ProviderDetection.DetectClaudeShimTrap() is { } shimWarning)
                logger.LogWarning("{ShimWarning}", shimWarning);
        }

        return new RunnerHello(
            RunnerProtocol.ProtocolVersion,
            Environment.MachineName,
            claude.CliFound,
            claude.Authenticated,
            [.. _runs.ActiveRunIds],
            providers,
            Capabilities:
            [
                RunnerCapabilities.UtilityProvider,
                RunnerCapabilities.AgentResume,
                RunnerCapabilities.ContextFiles,
                RunnerCapabilities.CliResume,
                RunnerCapabilities.Hybrid,
                RunnerCapabilities.Editor,
                RunnerCapabilities.CommandTrust,
                RunnerCapabilities.BaseSync,
                RunnerCapabilities.MergeVerify,
                RunnerCapabilities.WorkingTreeDiff,
                RunnerCapabilities.AppRun,
                RunnerCapabilities.AppRunPrepare,
                RunnerCapabilities.ProjectTools,
                RunnerCapabilities.ChatTools,
                RunnerCapabilities.TaskCancel,
                RunnerCapabilities.LocalFolders,
                RunnerCapabilities.RepoAccessCheck,
                RunnerCapabilities.DeveloperGit,
                RunnerCapabilities.ForgeCli,
                RunnerCapabilities.TrackerConfig,
                RunnerCapabilities.TrackerPoll,
                RunnerCapabilities.RepoDiscovery,
                RunnerCapabilities.TrackerConnect,
                RunnerCapabilities.TrackerBoards,
                RunnerCapabilities.Stories,
            ],
            DaemonVersion: Cli.OrkkaVersion.Current(),
            InstanceId: InstanceId,
            GitIdentity: null,
            // What opens the developer's pull requests — shown on the runner settings page so
            // a CLI that is not signed in is fixed before a task tries to publish.
            ForgeClis: await forgeCli.DetectAsync(ct),
            // Metadata only — the tokens stay in this machine's secret store. Health is
            // re-checked at most every 15 minutes, not on every 60s re-Hello.
            Trackers: await trackers.ListConnectionsAsync(ct),
            PendingCommandApprovals: PendingApprovals());
    }

    private CommandApproval[]? PendingApprovals()
    {
        if (pendingApprovals is null)
            return null;
        try
        {
            return [.. pendingApprovals().Take(MaxReportedApprovals)];
        }
        catch (Exception ex)
        {
            // A broken approvals file must not keep the runner from connecting.
            logger.LogDebug("Could not read the pending command approvals: {Error}", ex.Message);
            return [];
        }
    }

    /// <summary>A connection added or removed from the settings page shows up on the host now,
    /// not at the next 60s Hello.</summary>
    private void ReHelloSoon()
    {
        if (_connection is not { State: HubConnectionState.Connected } connection)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await SendHelloAsync(connection, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogDebug("Re-Hello after a tracker connection change failed: {Message}", ex.Message);
            }
        });
    }

    /// <summary>The settings page's "Add Jira / Azure DevOps": test, store in the OS secret store,
    /// answer metadata only. The request is never logged (its ToString hides the token too).</summary>
    internal async Task<AddTrackerConnectionResult> AddTrackerConnectionAsync(AddTrackerConnectionRequest request)
    {
        var result = await new Trackers.TrackerConnectionManager(trackers).AddAsync(request, CancellationToken.None);
        if (result.Ok)
        {
            logger.LogInformation("Added the tracker connection {Id} from the settings page.", result.Connection?.Id);
            ReHelloSoon();
        }
        return result;
    }

    internal Task<RemoveTrackerConnectionResult> RemoveTrackerConnectionAsync(RemoveTrackerConnectionRequest request)
    {
        var result = new Trackers.TrackerConnectionManager(trackers).Remove(request.Id);
        if (result.Ok)
        {
            logger.LogInformation("Removed the tracker connection {Id} from the settings page.", request.Id);
            ReHelloSoon();
        }
        return Task.FromResult(result);
    }

    private void RegisterHandlers(HubConnection c)
    {
        // Request/response ops (SignalR client results)
        c.On<ListDirectoryRequest, DirectoryListing>(RunnerProtocol.ListDirectory, ops.ListDirectoryAsync);
        c.On<ValidateRepoPathRequest, RepoPathInfo>(RunnerProtocol.ValidateRepoPath, ops.ValidateRepoPathAsync);
        c.On<InitLocalRepoRequest, InitLocalRepoResult>(RunnerProtocol.InitLocalRepo, ops.InitLocalRepoAsync);
        c.On<InitRepoInPlaceRequest, InitLocalRepoResult>(RunnerProtocol.InitRepoInPlace, ops.InitRepoInPlaceAsync);
        c.On<EnsureBareCloneRequest, RunnerOpResult>(RunnerProtocol.EnsureBareClone, ops.EnsureBareCloneAsync);
        c.On<DiscoverReposRequest, DiscoverReposResult>(RunnerProtocol.DiscoverRepos, ops.DiscoverReposAsync);
        c.On<CheckRepoAccessRequest, CheckRepoAccessResponse>(
            RunnerProtocol.CheckRepoAccess, ops.CheckRepoAccessAsync);
        // The project's "Ticket source" settings are built from these answers; the query
        // runs here because the token never leaves this machine.
        c.On<TrackerQueryRequest, TrackerQueryResult>(
            RunnerProtocol.TrackerQuery, req => trackers.QueryAsync(req, CancellationToken.None));
        // Write-only: a token typed into the settings page goes into this machine's secret
        // store; only metadata comes back.
        c.On<AddTrackerConnectionRequest, AddTrackerConnectionResult>(
            RunnerProtocol.AddTrackerConnection, AddTrackerConnectionAsync);
        c.On<RemoveTrackerConnectionRequest, RemoveTrackerConnectionResult>(
            RunnerProtocol.RemoveTrackerConnection, RemoveTrackerConnectionAsync);
        c.On<CreateWorktreeRequest, RunnerOpResult>(RunnerProtocol.CreateWorktree, ops.CreateWorktreeAsync);
        c.On<GetMergeConflictsRequest, RunnerOpResult>(RunnerProtocol.GetMergeConflicts, ops.GetMergeConflictsAsync);
        c.On<ApplyMergeStrategyRequest, RunnerOpResult>(RunnerProtocol.ApplyMergeStrategy, ops.ApplyMergeStrategyAsync);
        c.On<SyncBaseBranchRequest, RunnerOpResult>(RunnerProtocol.SyncBaseBranch, ops.SyncBaseBranchAsync);
        c.On<VerifyMergeRequest, RunnerOpResult>(RunnerProtocol.VerifyMerge, ops.VerifyMergeAsync);
        c.On<RunWorktreeCommandRequest, RunnerOpResult>(
            RunnerProtocol.RunWorktreeCommand, ops.RunWorktreeCommandAsync);
        c.On<WriteWorktreeFilesRequest, RunnerOpResult>(RunnerProtocol.WriteWorktreeFiles, ops.WriteWorktreeFilesAsync);
        c.On<RunSetupCommandRequest, RunnerOpResult>(RunnerProtocol.RunSetupCommand, ops.RunSetupCommandAsync);
        c.On<CommitAndPushRequest, RunnerOpResult>(RunnerProtocol.CommitAndPush, ops.CommitAndPushAsync);
        c.On<EnsurePushedRequest, RunnerOpResult>(RunnerProtocol.EnsurePushed, ops.EnsurePushedAsync);
        c.On<RemoteBranchExistsRequest, RunnerOpResult>(RunnerProtocol.RemoteBranchExists, ops.RemoteBranchExistsAsync);
        c.On<GetDiffRequest, RunnerOpResult>(RunnerProtocol.GetDiff, ops.GetDiffAsync);
        c.On<GetWorkingTreeDiffRequest, RunnerOpResult>(RunnerProtocol.GetWorkingTreeDiff, ops.GetWorkingTreeDiffAsync);
        // A worktree is never deleted out from under a live run: any run of that task still
        // in flight here is cancelled and its agent process waited on first. The host-side
        // reset sweep sends CancelTaskRuns ahead of this anyway; this is the guarantee for
        // every other cleanup path (merged-task sweep, GC) and for older hosts.
        c.On<CleanupWorktreeRequest, RunnerOpResult>(RunnerProtocol.CleanupWorktree, async req =>
        {
            var cancelled = await _runs.CancelTaskRunsAsync(req.TaskId, RunTeardownWait);
            if (cancelled > 0)
                logger.LogInformation(
                    "Cancelled {Count} in-flight run(s) of task {TaskId} before removing its worktree",
                    cancelled, req.TaskId);
            return await ops.CleanupWorktreeAsync(req);
        });
        c.On<RemoveRepoWorkspaceRequest, RunnerOpResult>(RunnerProtocol.RemoveRepoWorkspace, ops.RemoveRepoWorkspaceAsync);
        c.On<GetFileTreeRequest, RunnerFileTreeResult>(RunnerProtocol.GetFileTree, ops.GetFileTreeAsync);
        c.On<GetFileContentRequest, RunnerOpResult>(RunnerProtocol.GetFileContent, ops.GetFileContentAsync);
        c.On<GetCommitLogRequest, RunnerCommitLogResult>(RunnerProtocol.GetCommitLog, ops.GetCommitLogAsync);
        c.On<ListBranchesRequest, RunnerBranchListResult>(RunnerProtocol.ListBranches, ops.ListBranchesAsync);
        c.On<EnsureLocalBranchRequest, RunnerOpResult>(RunnerProtocol.EnsureLocalBranch, ops.EnsureLocalBranchAsync);
        c.On<CreateRepoBranchRequest, RunnerOpResult>(RunnerProtocol.CreateRepoBranch, ops.CreateRepoBranchAsync);
        c.On<GetCommitDiffRequest, RunnerOpResult>(RunnerProtocol.GetCommitDiff, ops.GetCommitDiffAsync);
        c.On<ListTreeRequest, RunnerTreeResult>(RunnerProtocol.ListTree, ops.ListTreeAsync);
        c.On<RemoteSyncStatusRequest, RunnerRemoteSyncStatus>(RunnerProtocol.GetRemoteSyncStatus, ops.GetRemoteSyncStatusAsync);
        c.On<PushToRemoteRequest, RunnerOpResult>(RunnerProtocol.PushToRemote, ops.PushToRemoteAsync);
        c.On<PublishRepoToGitHubRequest, RunnerOpResult>(RunnerProtocol.PublishRepoToGitHub, ops.PublishRepoToGitHubAsync);
        c.On<AttachRemoteRequest, RunnerOpResult>(RunnerProtocol.AttachRemote, ops.AttachRemoteAsync);
        c.On<EnsureChatWorkspaceRequest, RunnerOpResult>(RunnerProtocol.EnsureChatWorkspace, ops.EnsureChatWorkspaceAsync);
        c.On<SquashMergeRequest, RunnerOpResult>(RunnerProtocol.SquashMergeIntoUserRepo, ops.SquashMergeIntoUserRepoAsync);
        c.On<OpenPullRequestRequest, RunnerOpResult>(RunnerProtocol.OpenPullRequest, ops.OpenPullRequestAsync);
        c.On<GetPullRequestStateRequest, RunnerOpResult>(RunnerProtocol.GetPullRequestState, ops.GetPullRequestStateAsync);
        c.On<MarkPullRequestReadyRequest, RunnerOpResult>(RunnerProtocol.MarkPullRequestReady, ops.MarkPullRequestReadyAsync);
        c.On<UpdatePullRequestBodyRequest, RunnerOpResult>(RunnerProtocol.UpdatePullRequestBody, ops.UpdatePullRequestBodyAsync);
        c.On<ResolveReviewBaseRequest, RunnerOpResult>(RunnerProtocol.ResolveReviewBase, ops.ResolveReviewBaseAsync);
        c.On<RunnerUtilityRequest, RunnerUtilityResult>(RunnerProtocol.UtilityComplete, ops.UtilityCompleteAsync);

        // Streamed runs
        c.On<RunnerAgentRunRequest>(RunnerProtocol.StartAgentRun,
            req => StartRun(req.RunId, ct => ops.ExecuteAgentRun(req, ct), taskId: req.TaskId));
        c.On<RunnerChatRunRequest>(RunnerProtocol.StartChatRun,
            req => StartRun(req.RunId, ct => ops.ExecuteChatRun(req, ct)));
        c.On<string>(RunnerProtocol.CancelRun, runId =>
        {
            // Unknown or already-finished ids are a no-op by design: the host re-announces
            // stale ids on reconnect and may race a run's own completion.
            if (_runs.Cancel(runId))
                logger.LogInformation("Cancelling run {RunId} (host request)", runId);
            else
                logger.LogDebug("CancelRun for unknown/finished run {RunId} — nothing to do", runId);
        });
        // Task-scoped cancel: the host replica that asks may not be the one that started
        // the run (a deploy rolled it), so it cannot know the run id — only the task.
        c.On<CancelTaskRunsRequest, RunnerOpResult>(RunnerProtocol.CancelTaskRuns, async req =>
        {
            var cancelled = await _runs.CancelTaskRunsAsync(req.TaskId, RunTeardownWait);
            if (cancelled > 0)
                logger.LogInformation(
                    "Cancelled {Count} in-flight run(s) of task {TaskId} (host request)",
                    cancelled, req.TaskId);
            return RunnerOpResult.Ok(cancelled.ToString());
        });

        c.On<string, RunnerOpResult>(RunnerProtocol.StartProviderLogin,
            provider => StartProviderLogin(c, provider));

        // Editor escape hatch: hand the task's worktree to a desktop editor on this machine.
        c.On<ListEditorsResult>(RunnerProtocol.ListEditors, ops.ListEditorsAsync);
        c.On<OpenInEditorRequest, RunnerOpResult>(RunnerProtocol.OpenInEditor, ops.OpenInEditorAsync);

        // Run button: the user's own app, started in their real working copy. Output rides
        // the run-chunk channel per target exactly like a terminal session, but the run is
        // NOT registered in _runs — an app-run must survive the browser tab, the hub
        // connection, and the stale-run sweep that fires on reconnect. Stopping is explicit
        // (StopAppRun), replaced-by-a-newer-start, or the runner exiting.
        c.On<StartAppRunRequest, StartAppRunResult>(RunnerProtocol.StartAppRun, async req =>
        {
            // Every target's commands are vetted first; a refused target does not start, and
            // nothing that depends on it does either (its dependency would never come up).
            var (allowed, refused) = await ops.VetAppRunAsync(req);
            var started = allowed.Targets.Length == 0
                ? new StartAppRunResult([])
                : await appRuns.StartAsync(
                    allowed,
                    ops.ResolveUserRepoPathAsync,
                    runId => StreamDetached(runId, ct => appRuns.PumpOutput(runId, ct)));
            var byRunId = started.Targets.Concat(refused).ToDictionary(t => t.RunId);
            return new StartAppRunResult([.. req.Targets.Select(t => byRunId[t.RunId])]);
        });
        c.On<StopAppRunRequest, RunnerOpResult>(RunnerProtocol.StopAppRun, async req =>
        {
            var stopped = await appRuns.StopAsync(req);
            return RunnerOpResult.Ok(stopped.ToString());
        });
        c.On<AppRunStatusResult>(RunnerProtocol.GetAppRunStatus,
            () => Task.FromResult(appRuns.Status()));
        c.On<AttachAppRunRequest, RunnerOpResult>(RunnerProtocol.AttachAppRun, req =>
        {
            if (!appRuns.IsActive(req.RunId))
                return Task.FromResult(RunnerOpResult.Fail("That run is no longer active."));
            StreamDetached(req.RunId, ct => appRuns.PumpOutput(req.RunId, ct));
            return Task.FromResult(RunnerOpResult.Ok());
        });
        c.On<AppRunInputRequest>(RunnerProtocol.WriteAppRunInput, req => appRuns.WriteAsync(req));
        c.On<ResizeAppRunRequest>(RunnerProtocol.ResizeAppRun, req => appRuns.Resize(req));
    }

    /// <summary>
    /// Opens a visible terminal on this machine running the provider's login command —
    /// each CLI walks the user through its own browser sign-in. We then watch the
    /// credentials file and re-Hello the moment login completes so the UI flips green
    /// without any manual refresh.
    /// </summary>
    private Task<RunnerOpResult> StartProviderLogin(HubConnection connection, string provider)
    {
        if (!RunnerProviders.All.Contains(provider))
            return Task.FromResult(RunnerOpResult.Fail($"Unknown provider '{provider}'."));

        var loginCommand = ProviderDetection.LoginCommand(provider);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c start \"{Capitalize(provider)} Login\" cmd /k {loginCommand}",
                    UseShellExecute = true,
                });
            }
            else if (OperatingSystem.IsMacOS())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "osascript",
                    Arguments = $"-e \"tell application \\\"Terminal\\\" to do script \\\"{loginCommand}\\\" activate\"",
                    UseShellExecute = false,
                });
            }
            else
            {
                return Task.FromResult(RunnerOpResult.Fail(
                    $"Open a terminal on this machine and run `{loginCommand}`, then complete the login."));
            }

            // Watch for fresh credentials for up to 5 minutes; announce as soon as they land.
            // Credentials appearing is what clears a failure a run recorded against this
            // provider — the probe alone would keep reporting the stale failure. The second
            // leg covers CLIs whose login leaves no file the probe recognises (copilot's OS
            // store, grok): Detect drops the failure when the config dir was written after it.
            _ = Task.Run(async () =>
            {
                var deadline = DateTimeOffset.UtcNow.AddMinutes(5);
                while (DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10));
                    if (detection.HasCredentials(provider))
                    {
                        logger.LogInformation("{Provider} login detected — announcing to host", provider);
                        authOverrides.ClearFailure(provider);
                        if (connection.State == HubConnectionState.Connected)
                            await SendHelloAsync(connection, CancellationToken.None);
                        return;
                    }
                    if (authOverrides.FailureDetail(provider) is not null
                        && detection.Detect(provider).Authenticated)
                    {
                        logger.LogInformation("{Provider} credentials refreshed since the last auth failure — announcing to host", provider);
                        if (connection.State == HubConnectionState.Connected)
                            await SendHelloAsync(connection, CancellationToken.None);
                        return;
                    }
                }
            });

            return Task.FromResult(RunnerOpResult.Ok(
                "A terminal opened on this machine — complete the login there."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(RunnerOpResult.Fail(
                $"Could not open a login terminal: {ex.Message}. Run `{loginCommand}` manually and log in."));
        }
    }

    private static string Capitalize(string s) =>
        string.Create(s.Length, s, (span, src) =>
        {
            src.AsSpan().CopyTo(span);
            span[0] = char.ToUpperInvariant(span[0]);
        });

    /// <summary>
    /// Starts a streamed run and keeps it registered for as long as it really runs. The
    /// streamed <c>SendAsync</c> returns once the invocation is on the wire — SignalR pumps
    /// the chunks in the background — so the run's end is observed through the registry's
    /// tracking wrapper (the enumerator's teardown), never through the send's return.
    /// <paramref name="taskId"/> makes the run addressable by <c>CancelTaskRuns</c> and
    /// makes <c>CleanupWorktree</c> wait for it.
    /// </summary>
    private void StartRun(
        string runId, Func<CancellationToken, IAsyncEnumerable<RunnerChunk>> factory, string? taskId = null)
    {
        if (!_runs.TryStart(runId, taskId, out var run))
        {
            logger.LogWarning("Run {RunId} already active — ignoring duplicate start", runId);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                logger.LogInformation("Run {RunId} starting", runId);
                await _connection!.SendAsync(
                    RunnerProtocol.StreamRunChunks, runId,
                    _runs.Track(run, factory(run.Cts.Token)), run.Cts.Token);
                // Wait for the producer's actual end: completion, a CancelRun/CancelTaskRuns,
                // or the connection dropping (SignalR then disposes the upload stream).
                await run.Completed;
                if (run.Cts.IsCancellationRequested)
                    logger.LogInformation("Run {RunId} cancelled", runId);
                else
                    logger.LogInformation("Run {RunId} stream completed", runId);
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Run {RunId} cancelled", runId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Run {RunId} failed to stream", runId);
                try
                {
                    await _connection!.SendAsync(
                        RunnerProtocol.StreamRunChunks, runId, ErrorStream(ex.Message), CancellationToken.None);
                }
                catch { /* connection gone — the host fails the run on disconnect */ }
            }
            finally
            {
                // Normally already done by the tracking wrapper; this covers a send that
                // failed before the stream was ever pulled.
                _runs.Complete(runId);
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Streams output for something whose lifetime is NOT the stream's — app runs.
    ///
    /// <see cref="StartRun"/> is deliberately not reusable here: it registers the id in
    /// <see cref="_runs"/>, which makes the work cancellable by <c>CancelRun</c>, reportable
    /// in <c>Hello.ActiveRunIds</c>, and subject to the host's stale-run sweep on reconnect —
    /// all correct for an agent run, all fatal for a dev server the user expects to still be
    /// serving after a page reload. Failing to send just detaches the stream (the manager
    /// keeps draining the process); the host re-attaches with <c>AttachAppRun</c>.
    /// </summary>
    private void StreamDetached(string runId, Func<CancellationToken, IAsyncEnumerable<RunnerChunk>> factory)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _connection!.SendAsync(
                    RunnerProtocol.StreamRunChunks, runId, factory(CancellationToken.None),
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogInformation(
                    "App run {RunId} output stream detached ({Reason}) — the process keeps running",
                    runId, ex.Message);
            }
        }, CancellationToken.None);
    }

    private static async IAsyncEnumerable<RunnerChunk> ErrorStream(string message)
    {
        // Known CLI failure signatures get an actionable message; anything else keeps the
        // raw reason so the host can persist SOMETHING useful for the user.
        var described = Orkka.SharedKernel.Ai.ClaudeCliErrors.Describe(message);
        yield return new RunnerChunk("Error", described ?? $"Runner-side failure: {message}");
        yield return new RunnerChunk("Done", "");
        await Task.CompletedTask;
    }

}
