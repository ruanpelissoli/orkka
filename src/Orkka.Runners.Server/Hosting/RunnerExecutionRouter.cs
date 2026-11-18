namespace Orkka.Runners.Server.Hosting;

using System.Collections.Concurrent;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Execution.Orchestration;
using Orkka.Modules.Runners.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Runners.Server;

/// <summary>
/// The Worker's <see cref="IExecutionRouter"/>: every task runs on its owner's runner link —
/// their machine, their git credentials, their AI subscriptions. Routes are cached per
/// identity: links are stable handles and the proxies are stateless. A task with no owner
/// (<see cref="TaskSnapshot.RunnerUserId"/> null) routes to a link nobody can connect to, so
/// it reads as an offline runner rather than running anywhere else.
/// </summary>
public sealed class RunnerExecutionRouter(
    IRunnerLinkProvider links,
    ProjectToolRegistry toolRegistry,
    Orkka.Git.IGitOperationLedger ledger,
    ILoggerFactory loggerFactory) : IExecutionRouter
{
    private readonly ConcurrentDictionary<(Ulid OrgId, string UserId), ExecutionRoute> _routes = new();

    public ExecutionRoute Resolve(TaskSnapshot task)
        => _routes.GetOrAdd((task.OrgId, task.RunnerUserId ?? ""), key =>
        {
            var link = links.GetLink(key.OrgId, key.UserId);
            return new ExecutionRoute(
                new RunnerAgentInvoker(
                    link, loggerFactory.CreateLogger<RunnerAgentInvoker>(),
                    toolRegistry: toolRegistry),
                new RunnerGitWorkspaceManager(link,
                    new Orkka.Git.TopologyLedger(ledger, Orkka.Git.GitTopologies.UserRunner)),
                new HostedRunnerReadiness(link),
                // The runner's own bare clone is the one this task's worktree forks from.
                Repo: new RunnerGitRepoReader(link),
                BackendLive: () => link.IsConnected);
        });
}

/// <summary>Per-link provider readiness: mirrors the runner's last Hello (which vendor CLIs
/// are signed in on the owner's machine).</summary>
public sealed class HostedRunnerReadiness(IRunnerLink link) : IProviderReadiness
{
    private const string RunnerOffline =
        "Your runner is not connected. Start it on your machine with `./orkka start` and retry.";

    private static readonly AiProvider[] PreferenceOrder = ProviderPreference.Order;

    public string? GetBlocker(AiProvider provider)
    {
        var (connected, _) = link.Status;
        if (!connected)
            return RunnerOffline;
        return link.GetProviderBlocker(provider);
    }

    public IReadOnlyList<AiProvider> Connected
    {
        get
        {
            var (connected, hello) = link.Status;
            if (!connected || hello is null)
                return [];
            return PreferenceOrder
                .Where(p => hello.Providers.Any(s =>
                    s.Provider == RunnerProviders.FromAiProviderName(p.ToString()) && s.Authenticated))
                .ToList();
        }
    }

    public string? GetAnyProviderBlocker()
    {
        var (connected, hello) = link.Status;
        if (!connected || hello is null)
            return RunnerOffline;
        if (hello.Providers.Any(p => p.Authenticated))
            return null;
        return $"No AI provider is connected on your runner ({hello.MachineName}). " +
               "Sign in to Claude, Codex, Gemini, Grok, Copilot or OpenCode on that machine and retry.";
    }
}

/// <summary>Claim affinity over the live connection registry: a task is only claimable by the
/// Worker replica holding its owner's runner WebSocket.</summary>
public sealed class WorkerRunnerClaimAffinity(
    MultiTenantRunnerLinkProvider links) : IRunnerClaimAffinity
{
    public bool CanExecuteHere(Ulid orgId, string userId)
        => links.IsConnectedLocally(orgId, userId);

    public bool SupportsCapability(Ulid orgId, string userId, string capability)
        => links.SupportsCapability(orgId, userId, capability);
}
