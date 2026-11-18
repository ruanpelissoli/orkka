namespace Orkka.Modules.Agents.Domain;

using Orkka.Modules.Agents.Contracts;
using Orkka.SharedKernel.Domain;

/// <summary>
/// One thing the team knows about a project: a decision, a convention, a learning, a
/// mistake, a preference. Project-scoped; <see cref="AgentId"/> narrows it to one agent's
/// private notes (null = the whole team sees it). Written by agents (remember tool, post-run
/// outcome), by humans (Memory page) or by the platform; retrieved by relevance for every
/// run and chat turn (see MemoryRetriever), never by "newest N".
/// </summary>
public sealed class ProjectMemory : TenantEntity
{
    public Ulid ProjectId { get; set; }
    /// <summary>Null = shared with every agent on the project.</summary>
    public Ulid? AgentId { get; set; }
    // Cross-module references (Tasks module) — plain ids, no navigation.
    public Ulid? TaskId { get; set; }
    public Ulid? ExecutionId { get; set; }

    public MemoryType Type { get; set; }
    /// <summary>One line — what the memory is about. What lists and prompts show first.</summary>
    public required string Title { get; set; }
    public required string Content { get; set; }
    public MemorySource Source { get; set; }
    public string? CreatedByUserId { get; set; }
    /// <summary>Optional upsert key within the project (e.g. "outcome:{taskId}:{agentId}").</summary>
    public string? DedupeKey { get; set; }

    /// <summary>Always retrieved, ahead of ranking — the human said "this matters".</summary>
    public bool IsPinned { get; set; }
    /// <summary>Kept for the record but never retrieved.</summary>
    public bool IsArchived { get; set; }
    public int UseCount { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public string? Metadata { get; set; }

    // Navigation (intra-module)
    public ProjectAgent? Agent { get; set; }
}
