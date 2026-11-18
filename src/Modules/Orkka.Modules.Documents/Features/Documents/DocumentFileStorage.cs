namespace Orkka.Modules.Documents.Features.Documents;

using Orkka.Modules.Documents.Contracts;

/// <summary>
/// Blob layout for documents that are stored files rather than markdown. The upload, the
/// download, the delete, the project-deleted cleanup and the agent-side text extraction all
/// have to agree on the container and the name, so they agree here.
/// </summary>
public static class DocumentFileStorage
{
    public const string ContainerName = "project-documents";

    /// <summary>The document id — not the uploaded name — is the whole blob path, so a crafted
    /// file name cannot escape its org/project prefix or collide with another document.</summary>
    public static string BlobName(Ulid orgId, Ulid projectId, Ulid documentId, string extension) =>
        $"{orgId}/{projectId}/{documentId}{extension}";

    /// <summary>Strips the extension off an uploaded file name to seed the document title.
    /// Returns null when nothing usable is left (".pdf", a name that normalizes away).</summary>
    public static string? TitleFromFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var name = Path.GetFileNameWithoutExtension(fileName);
        return DocumentRules.NormalizeTitle(name);
    }
}
