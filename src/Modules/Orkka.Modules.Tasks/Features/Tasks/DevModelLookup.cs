namespace Orkka.Modules.Tasks.Features.Tasks;

using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Runners.Contracts;
using Orkka.Modules.Tasks.Contracts;

/// <summary>
/// Builds the runner-coverage query for a task that only exists as ids yet: resolves the
/// dev agent the orchestrator will pick (pinned, else by repo scope and role priority) so
/// the coverage check is made on the model that will actually run.
/// </summary>
internal static class DevModelLookup
{
    public static async Task<RunnerCoverageQuery> QueryAsync(
        IAgentsApi agentsApi, Ulid orgId, string userId, Ulid projectId, Ulid? repoId,
        Ulid? assignedAgentId, string? taskModelOverride, string? projectDefaultModel,
        CancellationToken ct)
    {
        AgentSummary? agent = assignedAgentId is { } id ? await agentsApi.GetAgentAsync(id, ct) : null;
        agent ??= DevAgentSelection.FindDevAgent(await agentsApi.ListAgentsAsync(projectId, ct), repoId);

        // The override only applies to dev-role agents (PM/QA/Reviewer keep their own models),
        // and a pinned non-dev agent is rejected upstream, so the override is passed as-is.
        return new RunnerCoverageQuery(
            orgId, userId, taskModelOverride, agent?.Model, projectDefaultModel);
    }

    /// <summary>
    /// Says in the task thread that the model's provider is not signed in on the owner's
    /// runner. Best-effort: a note that fails to post must never fail the creation that
    /// already committed.
    /// </summary>
    public static async Task AnnounceAsync(
        ITasksApi tasksApi, Ulid taskId, RunnerCoverageDecision decision, CancellationToken ct)
    {
        if (decision.Explanation is not { } text)
            return;
        try
        {
            await tasksApi.AddTaskMessageAsync(taskId, "system", text, MessageType.SystemEvent, ct: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // The note is a courtesy; the run's own pre-flight says the same.
        }
    }
}
