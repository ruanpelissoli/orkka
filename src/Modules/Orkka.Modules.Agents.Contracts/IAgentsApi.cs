namespace Orkka.Modules.Agents.Contracts;

/// <summary>Cross-module projection of a project agent (no counters — those are write-only via
/// <see cref="IAgentsApi.RecordAgentRunResultAsync"/>).</summary>
public sealed record AgentSummary(
    Ulid Id,
    Ulid OrgId,
    Ulid ProjectId,
    // Repo scope: empty = works across all repos, otherwise the subset it handles.
    IReadOnlyList<Ulid> RepoIds,
    string Name,
    AgentRole Role,
    string Model,
    string SystemPrompt,
    string UserInstructions);

/// <summary>A memory selected for a prompt (or shown by a recall tool). <see cref="AgentId"/>
/// null = shared by the whole team; otherwise private to that agent.</summary>
public sealed record MemoryItem(
    Ulid Id,
    MemoryType Type,
    string Title,
    string Content,
    Ulid? AgentId,
    bool IsPinned,
    DateTimeOffset CreatedAt);

/// <summary>What to retrieve: the project, the acting agent (its private memories are eligible
/// alongside team memories), free text describing the current work (task title + description,
/// or the chat message) that drives full-text ranking, and the size caps.</summary>
public sealed record MemoryQuery(
    Ulid OrgId,
    Ulid ProjectId,
    Ulid? AgentId,
    string? QueryText,
    int Take = 20,
    int CharBudget = 4_000);

/// <summary>An enabled skill assigned to an agent, exposed for prompt assembly. Callers list
/// slug + description in the prompt and write <see cref="Content"/> to the workspace (or
/// inline it when they can't) so the agent reads the body only when the work matches.</summary>
public sealed record AgentSkillSummary(
    Ulid Id,
    string Slug,
    string Name,
    string Description,
    string Content);

/// <summary>An approved design asset exposed for dev-agent task context. Callers sign
/// their own download URLs from the blob coordinates.</summary>
public sealed record ApprovedDesignAssetInfo(
    Ulid Id,
    string Name,
    string FileName,
    DesignAssetKind Kind,
    string MimeType,
    string BlobContainer,
    string BlobName);

/// <summary>A memory to persist. <see cref="AgentId"/> null = team-wide. <see cref="DedupeKey"/>
/// (optional) makes the write an upsert: a second memory with the same key in the same project
/// replaces the first — how per-task outcome memories stop piling up on retries.</summary>
public sealed record NewMemory(
    Ulid OrgId,
    Ulid ProjectId,
    Ulid? AgentId,
    MemoryType Type,
    string Title,
    string Content,
    MemorySource Source,
    string? CreatedByUserId = null,
    Ulid? TaskId = null,
    Ulid? ExecutionId = null,
    string? DedupeKey = null,
    bool IsPinned = false);

/// <summary>
/// Public surface of the Agents module.
/// </summary>
public interface IAgentsApi
{
    /// <summary>Batch name lookup for cross-module display (e.g. usage breakdowns).</summary>
    Task<IReadOnlyDictionary<Ulid, string>> GetAgentNamesAsync(
        IReadOnlyCollection<Ulid> agentIds, CancellationToken ct = default);

    /// <summary>Single agent lookup.</summary>
    Task<AgentSummary?> GetAgentAsync(Ulid agentId, CancellationToken ct = default);

    /// <summary>Batch agent lookup (e.g. task execution history display).</summary>
    Task<IReadOnlyList<AgentSummary>> GetAgentsAsync(
        IReadOnlyCollection<Ulid> agentIds, CancellationToken ct = default);

    /// <summary>All agents of a project — callers apply their own selection logic.</summary>
    Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(
        Ulid projectId, CancellationToken ct = default);

    /// <summary>
    /// Set-based counter update after an agent run: token totals plus completed/failed task
    /// deltas. No-ops when the agent no longer exists.
    /// </summary>
    Task RecordAgentRunResultAsync(
        Ulid agentId, long tokensInDelta, long tokensOutDelta,
        int completedTasksDelta, int failedTasksDelta, CancellationToken ct = default);

    /// <summary>The memories worth putting in front of an agent for this piece of work:
    /// pinned + decisions/conventions first (the project's standing rules), then full-text
    /// matches on <see cref="MemoryQuery.QueryText"/>, then the agent's newest learnings —
    /// within the caps. Marks the returned rows as used.</summary>
    Task<IReadOnlyList<MemoryItem>> RetrieveMemoriesAsync(MemoryQuery query, CancellationToken ct = default);

    /// <summary>Persists (or, with a dedupe key, replaces) a memory. Returns its id, or
    /// <see cref="Ulid.Empty"/> when the project already holds the maximum number of active
    /// memories and this would have been a new row — the caller decides whether that is worth
    /// telling anyone about (an agent tool says so; post-run bookkeeping just skips).</summary>
    Task<Ulid> AddMemoryAsync(NewMemory memory, CancellationToken ct = default);

    /// <summary>The agent's assigned, enabled skills (project playbooks) for prompt
    /// assembly, in name order. Empty when none are assigned.</summary>
    Task<IReadOnlyList<AgentSkillSummary>> ListAgentSkillsAsync(
        Ulid agentId, CancellationToken ct = default);

    /// <summary>Approved design assets of a project, newest first — injected into
    /// frontend/full-stack dev-agent task context.</summary>
    Task<IReadOnlyList<ApprovedDesignAssetInfo>> ListApprovedDesignAssetsAsync(
        Ulid projectId, int take, CancellationToken ct = default);

    /// <summary>Whether the chat conversation exists AND belongs to the org — the SignalR
    /// hub's group-join guard (group names are ULIDs and must never be joinable by a caller
    /// who merely knows the id).</summary>
    Task<bool> ConversationBelongsToOrgAsync(
        Ulid conversationId, Ulid orgId, CancellationToken ct = default);

    /// <summary>Deletes a task's Discuss conversations (and their messages) — the task itself
    /// was deleted. Returns how many conversations went.</summary>
    Task<int> DeleteTaskConversationsAsync(Ulid orgId, Ulid taskId, CancellationToken ct = default);

    /// <summary>The project a conversation belongs to, or null when it isn't the org's.
    /// Lets the hub apply the project-membership check as well as the org one.</summary>
    Task<Ulid?> GetConversationProjectIdAsync(
        Ulid conversationId, Ulid orgId, CancellationToken ct = default);
}
