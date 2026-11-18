namespace Orkka.Modules.Documents.Domain;

using Orkka.Modules.Documents.Contracts;
using Orkka.SharedKernel.Domain;

public sealed class DocumentEntity : TenantEntity
{
    // Cross-module reference (Projects module) — plain id, no navigation.
    public Ulid ProjectId { get; set; }
    public required string Title { get; set; }

    /// <summary>Folder path within the project's document tree, "" = root (e.g. "decisions/backend").</summary>
    public string Path { get; set; } = "";

    /// <summary>Markdown body. Empty for file documents — their content is the stored blob.</summary>
    public required string Content { get; set; }

    // ── Uploaded source file (PDF today) ────────────────────────────────
    // A file document keeps the bytes the user uploaded and is rendered by a viewer rather
    // than the markdown pipeline. All four move together: null on every markdown document.

    /// <summary>Blob name inside the project-documents container, null for markdown documents.</summary>
    public string? FileBlobName { get; set; }

    /// <summary>MIME type the file is served back with.</summary>
    public string? FileContentType { get; set; }

    /// <summary>The name the file was uploaded under — display and download only; the blob path
    /// is derived from the document id so a crafted name can never shape storage layout.</summary>
    public string? FileName { get; set; }

    public long? FileSizeBytes { get; set; }

    /// <summary>True when this document is a stored file rather than a markdown body.</summary>
    public bool IsFile => FileBlobName is not null;

    public int CurrentVersion { get; set; } = 1;
    public DocumentSource Source { get; set; }

    /// <summary>user id for manually created documents.</summary>
    public string? CreatedByUserId { get; set; }

    // Chat provenance (Agents module) — plain ids, no navigations.
    public Ulid? SourceAgentId { get; set; }
    public Ulid? SourceConversationId { get; set; }
    public Ulid? SourceMessageId { get; set; }

    /// <summary>The task whose completion created this document (Tasks module) — plain id, no
    /// navigation. Creation provenance only; a task that *revises* an existing document records
    /// itself on the revision row instead, exactly as chat provenance is split.</summary>
    public Ulid? SourceTaskId { get; set; }

    public ICollection<DocumentRevision> Revisions { get; set; } = [];
}
