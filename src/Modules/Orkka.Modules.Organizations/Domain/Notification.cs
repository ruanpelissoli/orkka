namespace Orkka.Modules.Organizations.Domain;

using Orkka.SharedKernel.Domain;

public sealed class Notification : TenantEntity
{
    public required string UserId { get; set; }
    // Cross-module reference: plain id, no navigation (tasks live in the Tasks module).
    public Ulid? TaskId { get; set; }
    public NotificationType Type { get; set; }
    public required string Title { get; set; }
    public string? Body { get; set; }
    public bool IsRead { get; set; }

    // Denormalized deep-link data captured at creation time (cross-schema joins are
    // forbidden and per-row resolution at read time would be N+1). A renamed project
    // slug leaves old notifications pointing at the old URL — acceptable.
    public Ulid? ProjectId { get; set; }
    public string? ProjectSlug { get; set; }
    public int? TaskNumber { get; set; }

    // Agent-chat notifications (AgentReplied) deep-link to the team page instead of a
    // task. Cross-module references (agents schema) — plain ids, no navigations.
    public Ulid? AgentId { get; set; }
    public Ulid? ConversationId { get; set; }
}
