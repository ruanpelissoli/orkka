namespace Orkka.Modules.Agents.Features.DesignAssets;

using Orkka.Modules.Agents.Domain;
using Orkka.SharedKernel.Security;

internal static class Mappers
{
    public static string? DisplayNameFor(this IReadOnlyDictionary<string, UserInfo> users, string? userId)
        => userId is not null && users.TryGetValue(userId, out var info) ? info.Name ?? info.Email : null;

    /// <summary>Display URLs are signed per response — pass a fresh short-lived one.</summary>
    public static DesignAssetResponse ToResponse(
        this DesignAsset entity, string? agentName, string url, string downloadUrl,
        string? requestedByUserName = null, string? reviewedByUserName = null,
        string? svgText = null) => new(
        entity.Id,
        entity.ProjectId,
        entity.AgentId,
        agentName,
        entity.ConversationId,
        entity.MessageId,
        entity.Name,
        entity.FileName,
        entity.Kind.ToString(),
        entity.MimeType,
        entity.SizeBytes,
        entity.Prompt,
        entity.SourceModel,
        entity.ParentAssetId,
        entity.RootAssetId,
        entity.Version,
        entity.Status.ToString(),
        entity.Feedback,
        url,
        downloadUrl,
        entity.ApprovedAt,
        entity.RequestedByUserId,
        requestedByUserName,
        entity.ReviewedByUserId,
        reviewedByUserName,
        entity.ReviewedAt,
        entity.CreatedAt,
        svgText,
        entity.FolderPath);
}
