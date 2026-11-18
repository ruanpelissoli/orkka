namespace Orkka.Modules.Documents.Domain;

public static class DocumentSnapshots
{
    /// <summary>Snapshot of the document's CURRENT state as a revision row. Call after mutating
    /// the entity (and bumping CurrentVersion) so the revision reflects what was saved.</summary>
    public static DocumentRevision FromCurrentState(
        DocumentEntity document, string? changedByUserId, Ulid? changedByAgentId,
        string? requestedByUserId = null, Ulid? sourceConversationId = null, Ulid? sourceMessageId = null,
        Ulid? sourceTaskId = null) => new()
    {
        Id = Ulid.NewUlid(),
        OrgId = document.OrgId,
        DocumentId = document.Id,
        Version = document.CurrentVersion,
        Title = document.Title,
        Path = document.Path,
        Content = document.Content,
        ChangedByUserId = changedByUserId,
        ChangedByAgentId = changedByAgentId,
        RequestedByUserId = requestedByUserId,
        SourceConversationId = sourceConversationId,
        SourceMessageId = sourceMessageId,
        SourceTaskId = sourceTaskId,
    };
}
