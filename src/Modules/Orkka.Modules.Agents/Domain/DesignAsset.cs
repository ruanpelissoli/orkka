namespace Orkka.Modules.Agents.Domain;

using Orkka.Modules.Agents.Contracts;
using Orkka.SharedKernel.Domain;

/// <summary>A visual asset on the project's Design page: an SVG authored directly by the
/// Designer agent, a raster image/GIF generated via the Gemini image model, or an image
/// uploaded by a user. The binary lives in blob storage; download URLs are signed on
/// demand, never stored.</summary>
public sealed class DesignAsset : TenantEntity
{
    // Cross-module reference (Projects module) — plain id, no navigation.
    public Ulid ProjectId { get; set; }
    /// <summary>Null for user-uploaded assets — they have no authoring agent.</summary>
    public Ulid? AgentId { get; set; }
    public Ulid? ConversationId { get; set; }
    public Ulid? MessageId { get; set; }
    // Reserved for a future task-pipeline path — always null today.
    public Ulid? TaskId { get; set; }
    public required string Name { get; set; }
    public required string FileName { get; set; }
    public DesignAssetKind Kind { get; set; }
    public required string MimeType { get; set; }
    public required string BlobContainer { get; set; }
    public required string BlobName { get; set; }
    public long SizeBytes { get; set; }
    /// <summary>Generation prompt; for GIFs the serialized frames spec; null for SVGs.</summary>
    public string? Prompt { get; set; }
    /// <summary>"claude-chat" for agent-authored SVGs, "user-upload" for user uploads,
    /// else the Gemini model id.</summary>
    public required string SourceModel { get; set; }
    public Ulid? ParentAssetId { get; set; }
    /// <summary>Groups iteration versions; equals own Id for a v1 asset.</summary>
    public Ulid RootAssetId { get; set; }
    public int Version { get; set; } = 1;
    public DesignAssetStatus Status { get; set; } = DesignAssetStatus.Pending;
    public string? Feedback { get; set; }

    /// <summary>Virtual folder for organizing the Design gallery ("" = root,
    /// e.g. "logos/dark") — same path semantics as Documents.</summary>
    public string FolderPath { get; set; } = "";
    public DateTimeOffset? ApprovedAt { get; set; }

    /// <summary>user id of the chat user whose message prompted the generation/iteration,
    /// or the uploader for user-uploaded assets.</summary>
    public string? RequestedByUserId { get; set; }
    /// <summary>user id of the user who approved or rejected this asset.</summary>
    public string? ReviewedByUserId { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }

    // Navigation (intra-module) — null for user-uploaded assets.
    public ProjectAgent? Agent { get; set; }
}
