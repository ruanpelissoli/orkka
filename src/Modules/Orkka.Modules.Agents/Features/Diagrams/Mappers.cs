namespace Orkka.Modules.Agents.Features.Diagrams;

using System.Text.Json;
using Orkka.Modules.Agents.Domain;

internal static class Mappers
{
    public static DiagramResponse ToResponse(this ArchitectureDiagram entity) =>
        new(
            entity.Id,
            entity.ProjectId,
            entity.AgentId,
            entity.ConversationId,
            entity.MessageId,
            entity.Title,
            entity.Type.ToString(),
            ParseDiagramData(entity.DiagramData),
            entity.Version,
            entity.Description,
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.FolderPath);

    public static DiagramVersionDetailResponse ToDetailResponse(
        this ArchitectureDiagramVersion v, string? changedByUserName, string? changedByAgentName,
        string? requestedByUserName) =>
        new(
            v.Version,
            v.Title,
            v.Description,
            ParseDiagramData(v.DiagramData),
            v.ChangedByUserId, changedByUserName,
            v.ChangedByAgentId, changedByAgentName,
            v.RequestedByUserId, requestedByUserName,
            v.SourceConversationId, v.SourceMessageId,
            v.CreatedAt);

    private static JsonElement ParseDiagramData(string diagramData)
    {
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(diagramData);
        }
        catch (JsonException)
        {
            // Malformed legacy rows: surface an empty scene instead of failing the whole response
            return JsonSerializer.Deserialize<JsonElement>("{}");
        }
    }
}
