namespace Orkka.Modules.Agents.Contracts;

/// <summary>
/// Context for a single audit event. TaskId/AgentId/ExecutionId are optional for platform-level
/// events that are not tied to a specific run.
/// </summary>
public sealed record AgentAuditRecord(
    Ulid OrgId,
    Ulid? ProjectId,
    AgentAuditEventType EventType,
    string Summary,
    Ulid? TaskId = null,
    Ulid? AgentId = null,
    Ulid? ExecutionId = null,
    string Actor = "agent",
    string? Tool = null,
    /// <summary>What kind of principal acted. Defaults to Agent for the run-observability callers.</summary>
    AuditActorKind ActorKind = AuditActorKind.Agent,
    /// <summary>The acting user's id when a person (or their API key) did it.</summary>
    string? ActorUserId = null,
    /// <summary>Optional structured detail, serialized to JSON by the writer (≤ 8 KB after redaction).</summary>
    object? Metadata = null)
{
    /// <summary>A person did it (through the UI or their API key). <paramref name="userId"/> is the
    /// user id; it becomes both the display actor and the filterable ActorUserId.</summary>
    public static AgentAuditRecord ByUser(
        Ulid orgId, string userId, AgentAuditEventType eventType, string summary,
        Ulid? projectId = null, object? metadata = null, Ulid? taskId = null, Ulid? agentId = null,
        AuditActorKind kind = AuditActorKind.User) =>
        new(orgId, projectId, eventType, summary, TaskId: taskId, AgentId: agentId,
            Actor: userId, ActorKind: kind, ActorUserId: userId, Metadata: metadata);

    /// <summary>The platform did it on its own (provisioning, sweeps, webhooks).</summary>
    public static AgentAuditRecord BySystem(
        Ulid orgId, AgentAuditEventType eventType, string summary,
        Ulid? projectId = null, object? metadata = null, string? relatedUserId = null) =>
        new(orgId, projectId, eventType, summary,
            Actor: "platform", ActorKind: AuditActorKind.System, ActorUserId: relatedUserId, Metadata: metadata);
}

/// <summary>
/// Writes to the append-only agent audit log. Implementations persist each record independently of
/// the caller's transaction (audit must survive even if the caller rolls back) and are safe to call
/// from singletons.
/// </summary>
public interface IAgentAuditLog
{
    Task RecordAsync(AgentAuditRecord record, CancellationToken ct = default);
}
