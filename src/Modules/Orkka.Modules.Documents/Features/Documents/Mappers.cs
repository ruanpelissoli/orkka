namespace Orkka.Modules.Documents.Features.Documents;

using Orkka.Modules.Documents.Domain;

public static class Mappers
{
    public static DocumentListItemResponse ToListItem(this DocumentEntity d) => new(
        d.Id, d.Title, d.Path, d.Source.ToString(), d.CurrentVersion, d.CreatedAt, d.UpdatedAt,
        d.FileName, d.FileContentType);

    public static DocumentResponse ToResponse(this DocumentEntity d) => new(
        d.Id, d.ProjectId, d.Title, d.Path, d.Content, d.Source.ToString(), d.CurrentVersion,
        d.CreatedByUserId, d.SourceAgentId, d.CreatedAt, d.UpdatedAt,
        d.FileName, d.FileContentType, d.FileSizeBytes);

    public static DocumentRevisionResponse ToResponse(
        this DocumentRevision r, string? changedByUserName, string? changedByAgentName,
        string? requestedByUserName, string? sourceTaskTitle = null) => new(
        r.Version, r.Title, r.Path,
        r.ChangedByUserId, changedByUserName, r.ChangedByAgentId, changedByAgentName,
        r.RequestedByUserId, requestedByUserName,
        r.SourceConversationId, r.SourceMessageId, r.CreatedAt,
        r.SourceTaskId, sourceTaskTitle);

    public static DocumentRevisionDetailResponse ToDetailResponse(
        this DocumentRevision r, string? changedByUserName, string? changedByAgentName,
        string? requestedByUserName, string? sourceTaskTitle = null) => new(
        r.Version, r.Title, r.Path, r.Content,
        r.ChangedByUserId, changedByUserName, r.ChangedByAgentId, changedByAgentName,
        r.RequestedByUserId, requestedByUserName,
        r.SourceConversationId, r.SourceMessageId, r.CreatedAt,
        r.SourceTaskId, sourceTaskTitle);
}
