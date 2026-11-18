namespace Orkka.Modules.Agents.Features.DesignAssets;

public sealed record DesignAssetResponse(
    Ulid Id,
    Ulid ProjectId,
    // Both null for user-uploaded assets — they have no authoring agent.
    Ulid? AgentId,
    string? AgentName,
    Ulid? ConversationId,
    Ulid? MessageId,
    string Name,
    string FileName,
    string Kind,
    string MimeType,
    long SizeBytes,
    string? Prompt,
    string SourceModel,
    Ulid? ParentAssetId,
    Ulid RootAssetId,
    int Version,
    string Status,
    string? Feedback,
    string Url,
    // Same signed blob URL but with an attachment Content-Disposition, so a browser
    // saves it under the asset's file name instead of the id-based blob name.
    string DownloadUrl,
    DateTimeOffset? ApprovedAt,
    string? RequestedByUserId,
    string? RequestedByUserName,
    string? ReviewedByUserId,
    string? ReviewedByUserName,
    DateTimeOffset? ReviewedAt,
    DateTimeOffset CreatedAt,
    // Inline SVG markup, set only by the single-asset endpoint for Kind=Svg — the client
    // rasterizes it to PNG for platforms that reject SVG uploads (avatars etc.). The list
    // endpoint leaves it null to stay lean.
    string? SvgText = null,
    // Virtual gallery folder ("" = root) — same path semantics as Documents.
    string FolderPath = "");

public sealed record RejectDesignAssetRequest(string? Feedback);
