namespace Orkka.Modules.Agents.Contracts;

/// <summary>
/// Which dev agent picks up a task when none is pinned. Lives in Contracts because two
/// modules need the same answer before the orchestrator ever runs: the Tasks module decides
/// where a task will execute (and therefore whether it bills) from the dev model at
/// creation time, and the forecaster prices the dev lanes — both must name the agent the
/// orchestrator's <c>AgentAssigner</c> will actually choose, or the estimate and the
/// routing decision describe a run that never happens.
/// </summary>
public static class DevAgentSelection
{
    /// <summary>Role priority. The generalist FullStackDev outranks specialists, and the niche
    /// GameDev/MobileDev only pick up unassigned tasks when no other dev agent fits — explicit
    /// pins and repo scoping still route to them.</summary>
    public static readonly AgentRole[] DevRoles =
    [
        AgentRole.FullStackDev, AgentRole.BackendDev, AgentRole.FrontendDev,
        AgentRole.GameDev, AgentRole.MobileDev
    ];

    /// <summary>The dev agent for a task in <paramref name="repoId"/>: an agent scoped to that
    /// repo first, then a cross-repo agent (empty scope), then the highest-priority dev role.
    /// Null when the project has no dev agent at all.</summary>
    public static AgentSummary? FindDevAgent(IReadOnlyList<AgentSummary> agents, Ulid? repoId)
    {
        // Role-priority order makes selection deterministic (the agents API returns rows in
        // no guaranteed order).
        var candidates = agents
            .Where(a => DevRoles.Contains(a.Role))
            .OrderBy(a => Array.IndexOf(DevRoles, a.Role))
            .ToList();

        if (candidates.Count == 0)
            return null;

        AgentSummary? agent = null;
        if (repoId is { } taskRepoId)
            agent = candidates.FirstOrDefault(a => a.RepoIds.Contains(taskRepoId));

        return agent ?? candidates.FirstOrDefault(a => a.RepoIds.Count == 0) ?? candidates[0];
    }
}

/// <summary>
/// The one precedence rule for which model a dev run uses: the task's own override (picked
/// on the create form), else the agent's Team-page model, else the project default. The
/// orchestrator, the forecaster and the execution-target resolver all call this — a second
/// copy of the rule is how a task gets routed for one model and run on another.
/// </summary>
public static class DevModelResolution
{
    public static string? Resolve(string? taskModelOverride, string? agentModel, string? projectDefaultModel)
    {
        if (!string.IsNullOrWhiteSpace(taskModelOverride))
            return taskModelOverride;

        if (!string.IsNullOrWhiteSpace(agentModel))
            return agentModel;

        return projectDefaultModel;
    }
}
