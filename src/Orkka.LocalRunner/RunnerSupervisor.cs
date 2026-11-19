namespace Orkka.LocalRunner;

using Orkka.Git;
using Orkka.LocalRunner.Cli;
using Orkka.LocalRunner.Trackers;
using Orkka.Modules.Execution.Agents;
using Orkka.Modules.Execution.Git;

/// <summary>Everything one workspace runs: its hub session, its tracker poller, and the
/// configuration its git work reads (the job's commit identity).</summary>
public sealed record RunnerWorkspace(
    WorkspaceProfile Profile,
    RunnerSession Session,
    TrackerPollerService Poller,
    IConfiguration Configuration,
    TrackerStore TrackerStore,
    TrackerAttachmentCache AttachmentCache);

/// <summary>
/// Builds a workspace's session out of the machine-wide pieces (agent invokers, CLI probes,
/// app runs, the run-slot semaphore) and the per-workspace ones: a hub link, an
/// InstanceId, a git workspace manager carrying that job's commit identity, and the job's
/// tracker store, secrets, attachment cache and poller under
/// <c>~/.orkka/workspaces/{name}/</c>. The repo cache stays shared — it is keyed by org id
/// (<c>~/.orkka/repos/{org}/…</c>), and every token is bound to one org, so two jobs never
/// share a mirror or a worktree.
/// </summary>
public sealed class RunnerSessionFactory(
    string orkkaRoot,
    IConfiguration baseConfiguration,
    ILoggerFactory loggers,
    GitProcess gitProcess,
    IGitOperationLedger ledger,
    IAiAgentInvoker invoker,
    ProviderDetection detection,
    ForgeCli forgeCli,
    Apps.AppRunManager appRuns,
    ProviderAuthOverrides authOverrides,
    ConsoleStatus status,
    MachineRunSlots runSlots,
    RunnerHostRouter router,
    JiraCloudClient jira,
    AzureDevOpsClient ado,
    HttpClient attachmentHttp,
    Func<string, string, IWorkspaceSecretStore> secretStores,
    Commands.CommandTrust commandTrust,
    GitHubProjectsClient? github = null)
{
    public RunnerWorkspace Create(WorkspaceProfile profile)
    {
        var configuration = new ConfigurationBuilder()
            .AddConfiguration(baseConfiguration)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Clones live under ~/.orkka/repos, where an `includeIf "gitdir:~/work/"` rule in
                // the developer's git config never matches — this is how each job keeps its own
                // author. Unset: the developer's git config decides.
                ["Git:CommitAuthorName"] = profile.GitAuthorName,
                ["Git:CommitAuthorEmail"] = profile.GitAuthorEmail,
            })
            .Build();

        var instanceId = Guid.NewGuid().ToString("N");
        var hostLink = new RunnerHostLink();
        router.AddSession(hostLink);

        var git = new GitWorkspaceManager(configuration, loggers.CreateLogger<GitWorkspaceManager>(), gitProcess, ledger);
        var trackerStore = new TrackerStore(orkkaRoot, profile.Name);
        var secrets = secretStores(orkkaRoot, profile.Name);
        // One resolver per workspace: its tokens, or the developer's gh login for a GitHub connection.
        var credentials = new TrackerCredentialResolver(secrets);
        var trackers = new TrackerService(trackerStore, secrets, jira, ado, loggers.CreateLogger<TrackerService>(),
            github: github, credentials: credentials);
        var cache = new TrackerAttachmentCache(TrackerPaths.TicketsDir(orkkaRoot, profile.Name), attachmentHttp,
            loggers.CreateLogger<TrackerAttachmentCache>());

        var ops = new RunnerOperations(git, invoker, detection, configuration, hostLink,
            loggers.CreateLogger<RunnerOperations>(), gitProcess, forgeCli, cache, runSlots, router,
            commandTrust, profile.Name);
        var session = new RunnerSession(profile, instanceId, ops, appRuns, detection, forgeCli, trackers,
            authOverrides, status, hostLink, loggers.CreateLogger<RunnerSession>(),
            persistToken: fresh =>
            {
                try
                {
                    Cli.WorkspaceProfiles.SaveToken(orkkaRoot, profile.Name, fresh, secrets);
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            },
            pendingApprovals: () => PendingApprovals(orkkaRoot, profile.Name));

        var poller = new TrackerTicketPoller(trackerStore, secrets, trackers.SourceFor, cache,
            loggers.CreateLogger<TrackerTicketPoller>(), credentials: credentials);
        // The setup page's dry run reads tickets through the very poller that imports them.
        trackers.Poller = poller;
        var writeBack = new TrackerWriteBack(trackerStore, secrets, trackers.WriterFor, TrackerWriteBack.ViaForgeCli(forgeCli),
            loggers.CreateLogger<TrackerWriteBack>(), credentials);
        var pollerService = new TrackerPollerService(new RunnerTrackerHub(hostLink, instanceId), trackerStore, poller,
            writeBack, cache, loggers.CreateLogger<TrackerPollerService>());

        return new RunnerWorkspace(profile, session, pollerService, configuration, trackerStore, cache);
    }

    /// <summary>Commands refused for this workspace and not yet approved, newest first.</summary>
    internal static IReadOnlyList<Orkka.LocalRunner.Contracts.CommandApproval> PendingApprovals(string orkkaRoot, string workspace)
        => [.. new Commands.CommandApprovalStore(orkkaRoot).Pending()
            .Where(e => string.IsNullOrEmpty(e.Workspace) || string.Equals(e.Workspace, workspace, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.FirstSeenAt)
            .Take(RunnerSession.MaxReportedApprovals)
            .Select(e => new Orkka.LocalRunner.Contracts.CommandApproval(e.Id, e.Repo, e.Command, e.Kind, Environment.MachineName))];
}

/// <summary>
/// Runs one <see cref="RunnerSession"/> (and its tracker poller) per workspace profile — a
/// developer with two jobs runs one runner (`./orkka start`) connected to both. A session that
/// cannot reach its host keeps redialing on its own; it never holds up the others.
/// <para>
/// Profiles added while it runs are picked up too (hot-add): <c>./orkka start</c> writes a
/// profile for every workspace created in the UI on its next bootstrap, and the runner it
/// started must serve it without a restart. <paramref name="discover"/> re-reads the profiles
/// every <see cref="DiscoveryInterval"/>; a new name starts a session. A profile removed or
/// changed on disk keeps its running session until the runner restarts — a session rotates its
/// own token, so a changed token on disk is not a reason to drop it.
/// </para>
/// </summary>
public sealed class RunnerSupervisor(
    IReadOnlyList<WorkspaceProfile> profiles,
    RunnerSessionFactory factory,
    ILogger<RunnerSupervisor> logger,
    Func<IReadOnlyList<WorkspaceProfile>>? discover = null) : BackgroundService
{
    /// <summary>How often new workspace profiles are looked for.</summary>
    internal static TimeSpan DiscoveryInterval { get; set; } = TimeSpan.FromSeconds(10);

    private readonly List<RunnerWorkspace> _workspaces = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<RunnerWorkspace> Workspaces
    {
        get
        {
            lock (_gate)
                return [.. _workspaces];
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var running = new List<Task>();
        foreach (var profile in profiles)
            running.Add(await StartWorkspaceAsync(profile, stoppingToken));
        logger.LogInformation("Serving {Count} workspace(s): {Names}",
            running.Count, string.Join(", ", Workspaces.Select(w => w.Profile.Name)));

        try
        {
            if (discover is null)
            {
                await Task.WhenAll(running);
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(DiscoveryInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                foreach (var profile in NewProfiles())
                {
                    logger.LogInformation("New workspace '{Workspace}' — connecting to {Host}", profile.Name, profile.HostUrl);
                    running.Add(await StartWorkspaceAsync(profile, stoppingToken));
                }
            }
            await Task.WhenAll(running);
        }
        finally
        {
            foreach (var workspace in Workspaces)
            {
                try { await workspace.Poller.StopAsync(CancellationToken.None); }
                catch (Exception ex) { logger.LogDebug("Stopping the tracker poller of {Workspace} failed: {Error}", workspace.Profile.Name, ex.Message); }
            }
            // Every session has cancelled its runs and waited for them; an agent CLI still
            // running now (its stream was not being pulled, its teardown never ran) would
            // outlive this process — end it with its children.
            var killed = AgentProcesses.KillAll();
            if (killed > 0)
                logger.LogWarning("Stopped {Count} agent process(es) that outlived their run", killed);
        }
    }

    /// <summary>Profiles on disk that have no session yet. Never throws: a profile that cannot
    /// be read now is tried again on the next pass.</summary>
    internal IReadOnlyList<WorkspaceProfile> NewProfiles()
    {
        IReadOnlyList<WorkspaceProfile> current;
        try
        {
            current = discover?.Invoke() ?? [];
        }
        catch (Exception ex)
        {
            logger.LogDebug("Reading the workspace profiles failed: {Error}", ex.Message);
            return [];
        }
        var known = Workspaces.Select(w => w.Profile.Name).ToHashSet(StringComparer.Ordinal);
        return [.. current.Where(p => !known.Contains(p.Name))];
    }

    private async Task<Task> StartWorkspaceAsync(WorkspaceProfile profile, CancellationToken ct)
    {
        var workspace = factory.Create(profile);
        lock (_gate)
            _workspaces.Add(workspace);
        await workspace.Poller.StartAsync(ct);
        return RunSessionAsync(workspace, ct);
    }

    private async Task RunSessionAsync(RunnerWorkspace workspace, CancellationToken ct)
    {
        try
        {
            await workspace.Session.RunAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            // One job's session failing must not take the other jobs down with it.
            logger.LogError("[{Workspace}] The session stopped: {Error}", workspace.Profile.Name,
                RunnerSession.Redact(ex.Message, workspace.Profile.Token));
        }
    }
}
