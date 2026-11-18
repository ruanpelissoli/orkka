namespace Orkka.Modules.Agents.Features.Diagrams;

using System.Text.Json;

public sealed record DiagramResponse(
    Ulid Id,
    Ulid ProjectId,
    Ulid AgentId,
    Ulid ConversationId,
    Ulid? MessageId,
    string Title,
    string Type,
    JsonElement DiagramData,
    int Version,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    // Virtual Architecture-board folder ("" = root) — same path semantics as Documents.
    string FolderPath = "");

public sealed record UpdateDiagramRequest(
    string? Title,
    JsonElement? DiagramData,
    string? Description);

public sealed record DiagramVersionResponse(
    int Version,
    string Title,
    string? Description,
    string? ChangedByUserId,
    string? ChangedByUserName,
    Ulid? ChangedByAgentId,
    string? ChangedByAgentName,
    string? RequestedByUserId,
    string? RequestedByUserName,
    Ulid? SourceConversationId,
    Ulid? SourceMessageId,
    DateTimeOffset CreatedAt);

public sealed record DiagramVersionDetailResponse(
    int Version,
    string Title,
    string? Description,
    JsonElement DiagramData,
    string? ChangedByUserId,
    string? ChangedByUserName,
    Ulid? ChangedByAgentId,
    string? ChangedByAgentName,
    string? RequestedByUserId,
    string? RequestedByUserName,
    Ulid? SourceConversationId,
    Ulid? SourceMessageId,
    DateTimeOffset CreatedAt);
