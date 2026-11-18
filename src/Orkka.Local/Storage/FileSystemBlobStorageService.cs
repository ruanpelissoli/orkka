namespace Orkka.Local.Storage;

using Orkka.SharedKernel.Storage;

/// <summary>
/// Blob storage on a plain directory (<c>{DataDir}/blobs</c>) instead of Azure Blob. The URLs it
/// hands out point at this host's <c>/blobs/…</c> endpoints and are HMAC-signed
/// (<see cref="BlobUrlSigner"/>) the way Azure URLs carry a SAS: a browser uploads to a signed
/// PUT URL, and a signed GET URL works without a session — which is what lets the runner fetch
/// design assets a task needs.
/// </summary>
public sealed class FileSystemBlobStorageService(
    LocalSettings settings,
    BlobUrlSigner signer,
    ILogger<FileSystemBlobStorageService> logger) : IBlobStorageService
{
    /// <summary>How long a URL from <see cref="GetBlobUrl"/> or <see cref="UploadAsync"/> works.
    /// Those have no caller-chosen expiry, and callers that persist one regenerate it.</summary>
    public static readonly TimeSpan DefaultUrlLifetime = TimeSpan.FromDays(7);

    private readonly string _root = Path.Combine(settings.DataDir, "blobs");

    public Task<string> GenerateUploadUrlAsync(
        string containerName, string blobName, string contentType, TimeSpan expiresIn)
        => Task.FromResult(SignedUrl(HttpMethods.Put, containerName, blobName, expiresIn));

    public Task<string> GenerateDownloadUrlAsync(
        string containerName, string blobName, TimeSpan expiresIn, string? downloadFileName = null)
        // The blob endpoint turns the filename query into a Content-Disposition header —
        // the local stand-in for the Azure SAS rscd override.
        => Task.FromResult(SignedUrl(HttpMethods.Get, containerName, blobName, expiresIn)
            + (downloadFileName is null ? "" : $"&filename={Uri.EscapeDataString(downloadFileName)}"));

    public async Task<string> UploadAsync(
        string containerName, string blobName, Stream content, string contentType,
        CancellationToken ct = default)
    {
        var path = GetPath(containerName, blobName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Write-then-move so a reader never sees half a file.
        var temp = path + ".uploading";
        await using (var file = File.Create(temp))
            await content.CopyToAsync(file, ct);
        File.Move(temp, path, overwrite: true);

        logger.LogDebug("[LocalBlob] Wrote {Container}/{Blob}", containerName, blobName);
        return GetBlobUrl(containerName, blobName);
    }

    public Task<Stream?> DownloadAsync(string containerName, string blobName, CancellationToken ct = default)
    {
        var path = GetPath(containerName, blobName);
        return Task.FromResult<Stream?>(File.Exists(path) ? File.OpenRead(path) : null);
    }

    public string GetBlobUrl(string containerName, string blobName)
        => SignedUrl(HttpMethods.Get, containerName, blobName, DefaultUrlLifetime);

    public Task DeleteBlobAsync(string containerName, string blobName)
    {
        var path = GetPath(containerName, blobName);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<long?> GetBlobSizeAsync(string containerName, string blobName, CancellationToken ct = default)
    {
        var info = new FileInfo(GetPath(containerName, blobName));
        return Task.FromResult<long?>(info.Exists ? info.Length : null);
    }

    public Task DeleteContainerAsync(string containerName, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(_root);
        var containerPath = Path.GetFullPath(Path.Combine(root, containerName));
        if (!containerPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException($"Container path escapes storage root: {containerName}");
        if (Directory.Exists(containerPath))
            Directory.Delete(containerPath, recursive: true);
        return Task.CompletedTask;
    }

    /// <summary>Resolves and validates the on-disk path for a container/blob pair.</summary>
    public string GetPath(string containerName, string blobName)
    {
        // Blob names come from requests — never let a crafted name escape the blob root.
        var root = Path.GetFullPath(_root);
        var combined = Path.GetFullPath(Path.Combine(root, containerName, blobName));
        if (!combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || containerName.Contains('/') || containerName.Contains('\\') || containerName is "." or "..")
            throw new InvalidOperationException($"Blob path escapes storage root: {containerName}/{blobName}");
        return combined;
    }

    private string SignedUrl(string method, string containerName, string blobName, TimeSpan expiresIn)
    {
        // Blob names contain '/' separators (org/project/asset) that the catch-all route segment
        // must see as real slashes — escape each segment, never the separators.
        var encodedBlob = string.Join('/', blobName.Split('/').Select(Uri.EscapeDataString));
        return $"{settings.LoopbackBaseUrl}/blobs/{Uri.EscapeDataString(containerName)}/{encodedBlob}?"
            + signer.Sign(method, containerName, blobName, expiresIn);
    }
}
