namespace Orkka.Modules.Execution.Orchestration;

using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Execution.Agents;
using Orkka.Modules.Runners.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.Modules.Tasks.Contracts;

/// <summary>
/// Everything a phase needs to execute a task on its owner's runner: the agent invoker, the
/// git workspace, provider readiness (the runner's connected CLIs) and ref-level git reads.
/// Runs authenticate from the developer's own subscriptions, so AI usage is recorded at $0.
/// </summary>
/// <param name="Repo">Ref-level git on the same machine as <paramref name="Git"/>: the runner's
/// own bare clone, the one this task's worktree forks from.</param>
public sealed record ExecutionRoute(
    IAiAgentInvoker Invoker,
    IGitWorkspaceManager Git,
    IProviderReadiness Readiness,
    IGitRepoReader Repo,
    Func<bool>? BackendLive = null)
{
    /// <summary>
    /// Is the runner serving this route reachable right now? A delegate, not a bool: routes
    /// are cached per identity, so a value captured at Resolve time would be stale by the time
    /// a run fails. It is how the orchestrator tells a dropped connection apart from a genuine
    /// agent failure. Null (tests) means always live.
    /// </summary>
    public bool IsBackendLive => BackendLive?.Invoke() ?? true;
}

/// <summary>
/// Resolves the runner a task executes on. The orchestrator calls this once per phase (after
/// loading the snapshot) and uses the route's members instead of process-global services.
/// The hosted Worker routes every task to its owner's runner link.
/// </summary>
public interface IExecutionRouter
{
    ExecutionRoute Resolve(TaskSnapshot task);
}

/// <summary>Default claim affinity: this process can execute anything. The hosted Worker
/// substitutes an adapter over its live connection registry.</summary>
public sealed class AlwaysHereRunnerClaimAffinity : IRunnerClaimAffinity
{
    public bool CanExecuteHere(Ulid orgId, string userId) => true;

    public bool SupportsCapability(Ulid orgId, string userId, string capability) => true;
}
