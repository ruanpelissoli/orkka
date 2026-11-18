namespace Orkka.Modules.Tasks.Features.TaskMessages;

public sealed record SendMessageRequest(
    string Content,
    string MessageType = "Text");

public sealed record TaskMessageResponse(
    Ulid Id,
    Ulid TaskId,
    string Role,
    string Content,
    string MessageType,
    string? SenderId,
    string? Metadata,
    DateTimeOffset CreatedAt);
