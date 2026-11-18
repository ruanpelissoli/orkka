namespace Orkka.Modules.Documents.Features.Documents;

public sealed record CreateDocumentRequest(
    string Title,
    string? Path,
    string Content);

public sealed record UpdateDocumentRequest(
    string? Title,
    string? Path,
    string? Content);

public sealed record DocumentListItemResponse(
    Ulid Id,
    string Title,
    string Path,
    string Source,
    int CurrentVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    // Set only for file documents — the tree uses it for the extension, and the browser needs
    // to know the kind before the detail request lands.
    string? FileName = null,
    string? FileContentType = null);

public sealed record DocumentResponse(
    Ulid Id,
    Ulid ProjectId,
    string Title,
    string Path,
    string Content,
    string Source,
    int CurrentVersion,
    string? CreatedByUserId,
    Ulid? SourceAgentId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    // Null on markdown documents; set together on a file document, which is rendered from its
    // stored bytes instead of Content (always empty for those).
    string? FileName = null,
    string? FileContentType = null,
    long? FileSizeBytes = null);

public sealed record DocumentRevisionResponse(
    int Version,
    string Title,
    string Path,
    string? ChangedByUserId,
    string? ChangedByUserName,
    Ulid? ChangedByAgentId,
    string? ChangedByAgentName,
    string? RequestedByUserId,
    string? RequestedByUserName,
    Ulid? SourceConversationId,
    Ulid? SourceMessageId,
    DateTimeOffset CreatedAt,
    // Set when a finished task's documentation pass wrote this revision. The title is null
    // when the task has since been deleted — the history still says a task caused it.
    Ulid? SourceTaskId = null,
    string? SourceTaskTitle = null);

public sealed record DocumentRevisionDetailResponse(
    int Version,
    string Title,
    string Path,
    string Content,
    string? ChangedByUserId,
    string? ChangedByUserName,
    Ulid? ChangedByAgentId,
    string? ChangedByAgentName,
    string? RequestedByUserId,
    string? RequestedByUserName,
    Ulid? SourceConversationId,
    Ulid? SourceMessageId,
    DateTimeOffset CreatedAt,
    Ulid? SourceTaskId = null,
    string? SourceTaskTitle = null);

/// <summary>A document a task's documentation pass created or revised, for the task page.</summary>
public sealed record TaskDocumentResponse(
    Ulid Id,
    string Title,
    string Path,
    int Version,
    /// <summary>"Created" or "Updated".</summary>
    string Outcome);
