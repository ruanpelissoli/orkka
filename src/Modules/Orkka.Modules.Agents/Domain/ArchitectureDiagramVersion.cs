namespace Orkka.Modules.Agents.Domain;

using Orkka.SharedKernel.Domain;

/// <summary>
/// Full snapshot of a diagram's state AT the given version. Written on create (version 1) and
/// on every update; restoring an old version appends a new snapshot rather than rolling back.
/// </summary>
public sealed class ArchitectureDiagramVersion : TenantEntity
{
    public Ulid DiagramId { get; set; }
    public int Version { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public string DiagramData { get; set; } = "{}";

    /// <summary>user id when a human made the change; null for agent-authored versions.</summary>
    public string? ChangedByUserId { get; set; }
    public Ulid? ChangedByAgentId { get; set; }

    /// <summary>user id of the chat user whose message prompted an agent-authored version.</summary>
    public string? RequestedByUserId { get; set; }
    public Ulid? SourceConversationId { get; set; }
    public Ulid? SourceMessageId { get; set; }

    public ArchitectureDiagram Diagram { get; set; } = null!;
}

public static class DiagramSnapshots
{
    /// <summary>Snapshot of the diagram's CURRENT state as a version row. Call after mutating
    /// the entity (and bumping Version) so the snapshot reflects what was saved.</summary>
    public static ArchitectureDiagramVersion FromCurrentState(
        ArchitectureDiagram diagram, string? changedByUserId, Ulid? changedByAgentId,
        string? requestedByUserId = null, Ulid? sourceConversationId = null, Ulid? sourceMessageId = null) => new()
    {
        Id = Ulid.NewUlid(),
        OrgId = diagram.OrgId,
        DiagramId = diagram.Id,
        Version = diagram.Version,
        Title = diagram.Title,
        Description = diagram.Description,
        DiagramData = diagram.DiagramData,
        ChangedByUserId = changedByUserId,
        ChangedByAgentId = changedByAgentId,
        RequestedByUserId = requestedByUserId,
        SourceConversationId = sourceConversationId,
        SourceMessageId = sourceMessageId,
    };
}
