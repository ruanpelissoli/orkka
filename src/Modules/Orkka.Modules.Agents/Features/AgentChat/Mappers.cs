namespace Orkka.Modules.Agents.Features.AgentChat;

using Orkka.Modules.Agents.Domain;

internal static class Mappers
{
    // A run that dies with its process can never clear the marker — past this age the UI
    // must stop showing a phantom thinking indicator. Generous: chat turns run minutes,
    // not hours.
    private static readonly TimeSpan ActiveRunStaleAfter = TimeSpan.FromMinutes(30);

    public static ConversationResponse ToResponse(this AgentConversation conversation) =>
        new(
            conversation.Id.ToString(),
            conversation.AgentId.ToString(),
            conversation.Agent.Name,
            conversation.Title,
            conversation.Messages.Count,
            conversation.Messages.Count > 0 ? conversation.Messages.Max(m => m.CreatedAt) : null,
            conversation.CreatedAt,
            conversation.UpdatedAt,
            HasActiveRun: conversation.ActiveRunStartedAt is { } startedAt
                && DateTimeOffset.UtcNow - startedAt < ActiveRunStaleAfter);

    public static MessageResponse ToResponse(
        this AgentMessage message,
        List<MessageDiagramInfo>? diagrams = null,
        List<MessageTaskInfo>? tasks = null,
        List<MessageDocumentInfo>? documents = null,
        List<MessageDesignAssetInfo>? designAssets = null) =>
        new(
            message.Id.ToString(),
            message.Role,
            message.Content,
            message.CreatedAt,
            diagrams,
            tasks,
            documents,
            designAssets);
}
