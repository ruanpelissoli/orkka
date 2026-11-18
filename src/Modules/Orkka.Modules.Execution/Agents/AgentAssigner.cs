namespace Orkka.Modules.Execution.Agents;

using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Agents.Contracts;
using Microsoft.Extensions.Logging;

public interface IAgentAssigner
{
    /// <summary>
    /// Auto-assigns the appropriate agent for the given task based on lane and repo type.
    /// Returns the assigned agent, or null if no suitable agent is available.
    /// The agents API is passed in so the caller's scope owns data access (this service is a singleton).
    /// </summary>
    Task<AgentSummary?> AssignAgentAsync(IAgentsApi agentsApi, TaskSnapshot task, Lane lane, CancellationToken ct = default);
}

public sealed class AgentAssigner(ILogger<AgentAssigner> logger) : IAgentAssigner
{
    // The dev-role priority and the repo-scope rule live in Agents.Contracts
    // (DevAgentSelection) because the Tasks module needs the same answer at creation time to
    // decide where — and whether billed — the task will run.
    private static readonly AgentRole[] DevRoles = DevAgentSelection.DevRoles;

    public async Task<AgentSummary?> AssignAgentAsync(
        IAgentsApi agentsApi, TaskSnapshot task, Lane lane, CancellationToken ct = default)
    {
        // If task already has an explicitly assigned agent, use it for dev lanes
        if (task.AssignedAgentId.HasValue && lane is Lane.Planning or Lane.Developing)
        {
            var assigned = await agentsApi.GetAgentAsync(task.AssignedAgentId.Value, ct);

            if (assigned is not null)
            {
                logger.LogInformation("Task {TaskId} using explicitly assigned agent {AgentName} ({AgentId})",
                    task.Id, assigned.Name, assigned.Id);
                return assigned;
            }

            logger.LogWarning("Task {TaskId} has AssignedAgentId {AgentId} but the agent no longer exists — falling back to auto-assign",
                task.Id, task.AssignedAgentId.Value);
        }

        var candidates = await agentsApi.ListAgentsAsync(task.ProjectId, ct);

        // For dev lanes (Planning/Developing), find a dev agent scoped to the task's repo
        if (lane is Lane.Planning or Lane.Developing)
            return FindDevAgent(candidates, task);

        var targetRole = GetRoleForLane(lane);
        if (targetRole is null)
        {
            logger.LogWarning("No agent role mapped for lane {Lane}", lane);
            return null;
        }

        // Find an agent with the target role for this project
        var roleCandidates = candidates.Where(a => a.Role == targetRole.Value).ToList();

        if (roleCandidates.Count == 0)
        {
            logger.LogWarning("No {Role} agent found for project {ProjectId}, task {TaskId}",
                targetRole, task.ProjectId, task.Id);
            return null;
        }

        var agent = roleCandidates[0];

        logger.LogInformation("Auto-assigned {Role} agent {AgentName} ({AgentId}) to task {TaskId} for lane {Lane}",
            agent.Role, agent.Name, agent.Id, task.Id, lane);

        return agent;
    }

    private AgentSummary? FindDevAgent(IReadOnlyList<AgentSummary> agents, TaskSnapshot task)
    {
        var agent = DevAgentSelection.FindDevAgent(agents, task.RepoId);
        if (agent is null)
        {
            logger.LogWarning("No dev agent found for project {ProjectId}, task {TaskId}",
                task.ProjectId, task.Id);
            return null;
        }

        logger.LogInformation("Auto-assigned dev agent {AgentName} ({AgentId}, {Role}) to task {TaskId}",
            agent.Name, agent.Id, agent.Role, task.Id);

        return agent;
    }

    private static AgentRole? GetRoleForLane(Lane lane) => lane switch
    {
        Lane.Ready => AgentRole.PM,
        Lane.Testing => AgentRole.QA,
        Lane.CodeReview => AgentRole.CodeReviewer,
        _ => null
    };
}
