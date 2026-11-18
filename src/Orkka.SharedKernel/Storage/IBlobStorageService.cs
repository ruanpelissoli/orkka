namespace Orkka.SharedKernel.Storage;

public interface IBlobStorageService
{
    Task<string> GenerateUploadUrlAsync(string containerName, string blobName, string contentType, TimeSpan expiresIn);
    /// <summary>When <paramref name="downloadFileName"/> is set, the URL serves the blob as an
    /// attachment saved under that name — without it browsers fall back to the blob name (the
    /// asset id), since the anchor download attribute is ignored on cross-origin URLs.</summary>
    Task<string> GenerateDownloadUrlAsync(string containerName, string blobName, TimeSpan expiresIn, string? downloadFileName = null);
    Task<string> UploadAsync(string containerName, string blobName, Stream content, string contentType, CancellationToken ct = default);
    /// <summary>Reads a blob's content, or null when it doesn't exist. Caller disposes the stream.</summary>
    Task<Stream?> DownloadAsync(string containerName, string blobName, CancellationToken ct = default);
    string GetBlobUrl(string containerName, string blobName);
    Task DeleteBlobAsync(string containerName, string blobName);
    /// <summary>Size of a blob in bytes, or null when it doesn't exist. Used to reconcile
    /// (and bill) direct-to-storage uploads the API never saw pass through.</summary>
    Task<long?> GetBlobSizeAsync(string containerName, string blobName, CancellationToken ct = default);
    /// <summary>Deletes an entire container and everything in it — managed-project
    /// deprovisioning only. Idempotent: a missing container is success.</summary>
    Task DeleteContainerAsync(string containerName, CancellationToken ct = default);
}
