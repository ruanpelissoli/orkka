namespace Orkka.Modules.Agents.Features.AgentChat;

public sealed record ConversationResponse(
    string Id,
    string AgentId,
    string AgentName,
    string? Title,
    int MessageCount,
    DateTimeOffset? LastMessageAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool HasActiveRun = false);

public sealed record CreateConversationRequest(string? Title = null);

public sealed record MessageDiagramInfo(string Id, string Title);

public sealed record MessageTaskInfo(string Id, int Number, string Title);

public sealed record MessageDocumentInfo(string Id, string Title);

public sealed record MessageDesignAssetInfo(string Id, string Name, string Kind, int Version, string Url);

public sealed record MessageResponse(
    string Id,
    string Role,
    string Content,
    DateTimeOffset CreatedAt,
    List<MessageDiagramInfo>? Diagrams = null,
    List<MessageTaskInfo>? Tasks = null,
    List<MessageDocumentInfo>? Documents = null,
    List<MessageDesignAssetInfo>? DesignAssets = null);

/// <param name="DesignFolderPath">
/// The gallery folder the user is looking at, when the turn was started from the Design page.
/// Assets the agent creates are filed here instead of at the root — the folder is a fact about
/// where the user is, not something the model should have to be told and remember.
/// </param>
public sealed record SendMessageRequest(string Content, string? DesignFolderPath = null);

public sealed record MessageListResponse(List<MessageResponse> Messages, bool HasMore);
