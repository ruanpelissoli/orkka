namespace Orkka.Modules.Tasks.Features.Tasks;

using Orkka.Modules.Agents.Contracts;

/// <summary>
/// Shared guard for pinning a task to a dev agent: AgentAssigner honors AssignedAgentId
/// verbatim for Planning/Developing lanes, so the endpoint must ensure it points at a
/// dev-role agent of the same project — otherwise a PM or Designer would end up
/// writing code.
/// </summary>
internal static class AssignedAgentValidation
{
    /// <summary>Returns an error message, or null when the agent is a valid assignee.</summary>
    public static async Task<string?> ValidateAsync(
        IAgentsApi agentsApi, Ulid agentId, Ulid projectId, Ulid orgId, CancellationToken ct)
    {
        var agent = await agentsApi.GetAgentAsync(agentId, ct);

        if (agent is null || agent.ProjectId != projectId || agent.OrgId != orgId)
            return "The assigned agent does not belong to this project.";

        if (!agent.Role.IsDevRole())
            return $"{agent.Name} is a {agent.Role} agent — only dev agents (Frontend, Backend, Full Stack, Game Dev) can be assigned tasks.";

        return null;
    }
}
