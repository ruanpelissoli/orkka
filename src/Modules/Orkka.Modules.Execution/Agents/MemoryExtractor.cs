namespace Orkka.Modules.Execution.Agents;

using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Agents.Contracts;
using Microsoft.Extensions.Logging;

public interface IMemoryExtractor
{
    /// <summary>
    /// After a task execution completes, extracts learnings and saves them as agent memories.
    /// The agents API is passed in so the caller's scope owns data access (this service is a singleton).
    /// </summary>
    Task ExtractAndSaveAsync(
        IAgentsApi agentsApi, ExecutionSnapshot execution, AgentSummary agent,
        string? taskTitle = null, CancellationToken ct = default);
}

public sealed class MemoryExtractor(ILogger<MemoryExtractor> logger) : IMemoryExtractor
{
    /// <summary>Excerpt budget per memory. Outcome notes are retrieved by relevance alongside
    /// the team's decisions and conventions under a hard char budget — each entry has to earn
    /// its share of it.</summary>
    private const int ExcerptChars = 240;

    public async Task ExtractAndSaveAsync(
        IAgentsApi agentsApi, ExecutionSnapshot execution, AgentSummary agent,
        string? taskTitle = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(execution.AgentLog))
            return;

        // Save execution outcome as a memory
        var memoryType = execution.Status == ExecutionStatus.Failed
            ? MemoryType.Mistake
            : MemoryType.Learning;

        // The memory has to say WHAT the work was, not how many tokens it burned: these
        // lines are injected into every later run as "your memories from previous work",
        // and a page of duration/token telemetry taught the next run nothing. Task title
        // first (the recall key), then the outcome — the error for failures, the run's own
        // closing summary for successes.
        var about = string.IsNullOrWhiteSpace(taskTitle) ? "" : $" for task \"{Trim(taskTitle, 120)}\"";
        var content = execution.Status == ExecutionStatus.Failed
            ? $"{execution.Lane} failed{about}: {Trim(execution.ErrorMessage ?? "unknown error", ExcerptChars)}"
            : $"Completed {execution.Lane}{about}. {TailExcerpt(execution.AgentLog!)}";
        var title = execution.Status == ExecutionStatus.Failed
            ? $"{execution.Lane} failed: {Trim(taskTitle ?? "task", 100)}"
            : $"{execution.Lane} done: {Trim(taskTitle ?? "task", 100)}";

        // Private to the agent (its own run history) and deduped per task + lane + agent, so a
        // retried phase replaces its earlier note instead of stacking a memory per attempt.
        var id = await agentsApi.AddMemoryAsync(new NewMemory(
            OrgId: agent.OrgId,
            ProjectId: agent.ProjectId,
            AgentId: agent.Id,
            Type: memoryType,
            Title: title,
            Content: content,
            Source: MemorySource.Agent,
            TaskId: execution.TaskId,
            ExecutionId: execution.Id,
            DedupeKey: $"outcome:{execution.TaskId}:{execution.Lane}:{agent.Id}"), ct);

        // A full project drops the outcome note rather than failing the run that just
        // succeeded — but it says so, because silence here looks identical to "nothing to save".
        if (id == Ulid.Empty)
        {
            logger.LogWarning(
                "Project {ProjectId} is at the memory limit — dropped the {MemoryType} outcome note for execution {ExecutionId}",
                agent.ProjectId, memoryType, execution.Id);
            return;
        }

        logger.LogInformation(
            "Extracted {MemoryType} memory for agent {AgentName} ({AgentId}) from execution {ExecutionId}",
            memoryType, agent.Name, agent.Id, execution.Id);
    }

    /// <summary>The end of the agent's own output — where a run summarises what it did and
    /// what it left open, which is the only part worth remembering verbatim.</summary>
    private static string TailExcerpt(string agentLog)
    {
        var trimmed = agentLog.TrimEnd();
        var tail = trimmed.Length <= ExcerptChars ? trimmed : "…" + trimmed[^ExcerptChars..];
        // Newlines flatten so one memory stays one line in the injected section.
        return tail.ReplaceLineEndings(" ");
    }

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
