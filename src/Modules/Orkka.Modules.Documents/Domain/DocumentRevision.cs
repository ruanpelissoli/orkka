namespace Orkka.Modules.Documents.Domain;

using Orkka.SharedKernel.Domain;

/// <summary>
/// Full snapshot of a document's state AT the given version. Written on create (version 1) and
/// on every update; restoring an old version appends a new revision rather than rolling back.
/// </summary>
public sealed class DocumentRevision : TenantEntity
{
    public Ulid DocumentId { get; set; }
    public int Version { get; set; }
    public required string Title { get; set; }
    public string Path { get; set; } = "";
    public required string Content { get; set; }

    /// <summary>user id when a human made the change; null for agent-authored revisions.</summary>
    public string? ChangedByUserId { get; set; }
    public Ulid? ChangedByAgentId { get; set; }

    /// <summary>user id of the chat user whose message prompted an agent-authored revision.</summary>
    public string? RequestedByUserId { get; set; }
    public Ulid? SourceConversationId { get; set; }
    public Ulid? SourceMessageId { get; set; }

    /// <summary>The completed task that wrote this revision — the answer to "which piece of work
    /// changed this document". Set on creation and on every later revision that task causes.</summary>
    public Ulid? SourceTaskId { get; set; }

    public DocumentEntity Document { get; set; } = null!;
}
