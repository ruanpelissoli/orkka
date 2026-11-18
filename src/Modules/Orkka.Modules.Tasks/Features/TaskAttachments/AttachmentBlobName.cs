namespace Orkka.Modules.Tasks.Features.TaskAttachments;

/// <summary>Maps a stored attachment URL back to its container-relative blob name.
/// Handles both editions: Azure (…/task-attachments/{blob}) and the local host
/// (…/api/local/blobs/task-attachments/{blob}); path segments are URL-escaped.</summary>
internal static class AttachmentBlobName
{
    public static string? FromUrl(string fileUrl)
    {
        if (!Uri.TryCreate(fileUrl, UriKind.Absolute, out var uri))
            return null;
        const string marker = "/task-attachments/";
        var path = uri.AbsolutePath;
        var idx = path.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
            return null;
        var rest = path[(idx + marker.Length)..];
        return rest.Length == 0
            ? null
            : string.Join('/', rest.Split('/').Select(Uri.UnescapeDataString));
    }
}
