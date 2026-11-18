namespace Orkka.Modules.Agents.Domain;

using Orkka.Modules.Agents.Contracts;
using Orkka.SharedKernel.Domain;

/// <summary>
/// Append-only record of a security-relevant agent action (tool call, blocked command, limit hit,
/// PR create/merge, run lifecycle). Tenant-scoped and immutable — writes only, never updated or
/// deleted (enforced in <c>AgentsDbContext.OnBeforeSave</c>). Used for incident response and to make
/// the agent's behaviour investigable after the fact.
/// </summary>
public sealed class AgentAuditEvent : TenantEntity
{
    /// <summary>Null for org-level events (membership, API keys, settings) that have no project.</summary>
    public Ulid? ProjectId { get; set; }
    public Ulid? TaskId { get; set; }
    public Ulid? AgentId { get; set; }
    public Ulid? ExecutionId { get; set; }

    public AgentAuditEventType EventType { get; set; }

    /// <summary>Who acted — agent name, "system", "platform", or a display for the user.</summary>
    public string Actor { get; set; } = "agent";

    /// <summary>What kind of principal acted.</summary>
    public AuditActorKind ActorKind { get; set; } = AuditActorKind.Agent;

    /// <summary>The user's id when a person or their API key acted; null for agents/system.</summary>
    public string? ActorUserId { get; set; }

    /// <summary>Tool name for <see cref="AgentAuditEventType.ToolUse"/> events; otherwise null.</summary>
    public string? Tool { get; set; }

    /// <summary>Short, redacted description of the event (never contains secrets/tokens).</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Optional structured detail (JSON): changed settings keys, target member, key
    /// prefix… Redacted by the writer; capped at 8 KB.</summary>
    public string? MetadataJson { get; set; }

    /// <summary>Tamper-evidence: SHA-256 hex of the previous entry in this org's chain (null
    /// on the first chained row, and on rows written before chaining existed).</summary>
    public string? PreviousHash { get; set; }

    /// <summary>SHA-256 hex over (PreviousHash | Id | OrgId | ProjectId | EventType | Actor |
    /// ActorKind | ActorUserId | Summary | MetadataJson | CreatedAt). Null on pre-chain rows.
    /// Verified by <c>POST /api/audit/verify</c>.</summary>
    public string? EntryHash { get; set; }
}
