namespace Orkka.Modules.Agents.Features.TaskChat;

using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Tasks.Contracts;

/// <summary>
/// Who answers in a task's Discuss chat by default: the agent working the task now
/// (<c>CurrentAgentId</c>); otherwise the lane's agent — the PM before planning, the task's
/// dev agent after (its assigned agent, else the project's dev agent). The UI may pick any of
/// the project's agents instead.
/// </summary>
public static class TaskChatAgentSelector
{
    // Same priority the pipeline's AgentAssigner falls back through.
    private static readonly AgentRole[] DevRolePriority =
        [AgentRole.FullStackDev, AgentRole.BackendDev, AgentRole.FrontendDev, AgentRole.MobileDev, AgentRole.GameDev];

    public static ProjectAgent? Select(TaskSnapshot task, IReadOnlyList<ProjectAgent> agents)
    {
        if (agents.Count == 0)
            return null;

        if (task.CurrentAgentId is { } current && agents.FirstOrDefault(a => a.Id == current) is { } working)
            return working;

        if (IsBeforePlanning(task))
            return ByRole(agents, AgentRole.PM) ?? DevAgent(task, agents) ?? agents[0];

        return DevAgent(task, agents) ?? ByRole(agents, AgentRole.PM) ?? agents[0];
    }

    /// <summary>Drafting/Ready, or questions asked from there (the PM's review or the repo
    /// routing question) — the PM's side of the pipeline.</summary>
    internal static bool IsBeforePlanning(TaskSnapshot task) => task.CurrentLane switch
    {
        Lane.Drafting or Lane.Ready => true,
        Lane.PendingQuestions => task.QuestionSourceLane is null or Lane.Drafting or Lane.Ready,
        _ => false,
    };

    private static ProjectAgent? DevAgent(TaskSnapshot task, IReadOnlyList<ProjectAgent> agents)
    {
        if (task.AssignedAgentId is { } assigned && agents.FirstOrDefault(a => a.Id == assigned) is { } picked)
            return picked;

        var repoGuid = task.RepoId?.ToGuid();
        foreach (var role in DevRolePriority)
        {
            var candidates = agents.Where(a => a.Role == role).ToList();
            // A dev agent scoped to the task's repo beats one scoped elsewhere.
            var match = candidates.FirstOrDefault(a => repoGuid is { } g && a.RepoIds.Contains(g))
                ?? candidates.FirstOrDefault(a => a.RepoIds.Count == 0)
                ?? candidates.FirstOrDefault();
            if (match is not null)
                return match;
        }
        return null;
    }

    private static ProjectAgent? ByRole(IReadOnlyList<ProjectAgent> agents, AgentRole role)
        => agents.Where(a => a.Role == role).OrderByDescending(a => a.IsDefault).FirstOrDefault();
}
