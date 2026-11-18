namespace Orkka.Modules.Agents.Features.Agents;

public sealed record AgentResponse(
    Ulid Id,
    Ulid ProjectId,
    // Repo scope: empty = works across all repos, otherwise the subset it handles.
    List<Ulid> RepoIds,
    // Assigned skills (project playbooks). Empty = none.
    List<Ulid> SkillIds,
    string Name,
    string Role,
    string Avatar,
    string Model,
    string SystemPrompt,
    string UserInstructions,
    bool IsDefault,
    long TotalTokensIn,
    long TotalTokensOut,
    decimal TotalCostUsd,
    /// <summary>Lifetime tokens from the usage rows, cache included. Null when the agent has no
    /// usage rows — read <see cref="TotalTokensIn"/>/<see cref="TotalTokensOut"/> then, which are
    /// the stored counters and exclude cache entirely.</summary>
    AgentTokenBreakdown? Billed,
    int TasksCompleted,
    int TasksFailed,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// What the agent's <c>TotalCostUsd</c> was actually charged for. The stored
/// <c>TotalTokensIn</c>/<c>TotalTokensOut</c> counters have no cache buckets, so on agentic work
/// they report a small fraction of the real volume — see <c>IUsageQuery.GetAgentTokenTotalsAsync</c>.
/// </summary>
public sealed record AgentTokenBreakdown(
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long TotalTokens);

public sealed record UpdateAgentRequest(
    string? Name,
    string? Avatar,
    string? Model,
    string? UserInstructions,
    // Repo scope: null = unchanged, [] = clear (all repos), ids = scope to that subset.
    List<string>? RepoIds = null,
    // Assigned skills: null = unchanged, [] = none, ids = exactly that set (project skills only).
    List<string>? SkillIds = null);
