namespace Orkka.Modules.Tasks.Features.AgentStats;

public sealed record AgentStatsRequest
{
    /// <summary>Lookback window: <c>24h</c>, <c>7d</c> (default) or <c>30d</c>.</summary>
    public string? Window { get; init; }
}

/// <summary>
/// Operational counters for one agent over the requested window, derived from
/// <c>tasks.task_executions</c>. Cost and token totals deliberately live on the agent
/// response (they come from the Usage module / denormalized counters); this record only answers
/// "how has this agent been performing lately".
/// </summary>
public sealed record AgentStatsResponse(
    Ulid AgentId,
    int Runs,
    int Succeeded,
    int Failed,
    int Running,
    double? AvgDurationSeconds,
    DateTimeOffset? LastRunAt,
    string? LastError,
    DateTimeOffset? LastErrorAt);

public sealed record AgentStatsEnvelope(
    string Window,
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<AgentStatsResponse> Agents);
