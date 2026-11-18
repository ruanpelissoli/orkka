namespace Orkka.Modules.Agents.Domain;

using Orkka.SharedKernel.Domain;

/// <summary>A file the user attached to an agent chat message for context. The binary
/// lives in blob storage; at send time text-like attachments are inlined into the agent's
/// context and image attachments can feed the Designer's Gemini generation as reference
/// inputs. Referenced from messages via visible "[attachment:&lt;ulid&gt; ...]" markers.</summary>
public sealed class ChatAttachment : TenantEntity
{
    // Cross-module reference (Projects module) — plain id, no navigation.
    public Ulid ProjectId { get; set; }

    /// <summary>Conversation the file was attached from; null until first referenced
    /// (uploads happen before the message is sent).</summary>
    public Ulid? ConversationId { get; set; }

    public required string FileName { get; set; }
    public required string MimeType { get; set; }
    public long SizeBytes { get; set; }
    public required string BlobContainer { get; set; }
    public required string BlobName { get; set; }

    /// <summary>user id of the uploader.</summary>
    public required string UploadedByUserId { get; set; }
}
