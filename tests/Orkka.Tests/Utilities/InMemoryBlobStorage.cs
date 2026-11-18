namespace Orkka.Tests.Utilities;

using Orkka.SharedKernel.Storage;

/// <summary>Dictionary-backed <see cref="IBlobStorageService"/> for tests that need to see what
/// was written, not just that the call was made.</summary>
internal sealed class InMemoryBlobStorage : IBlobStorageService
{
    public Dictionary<string, byte[]> Blobs { get; } = [];
    public List<string> Deleted { get; } = [];

    public Task<Stream?> DownloadAsync(string containerName, string blobName, CancellationToken ct = default) =>
        Task.FromResult<Stream?>(
            Blobs.TryGetValue(Key(containerName, blobName), out var bytes)
                ? new MemoryStream(bytes, writable: false)
                : null);

    public async Task<string> UploadAsync(
        string containerName, string blobName, Stream content, string contentType,
        CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        Blobs[Key(containerName, blobName)] = buffer.ToArray();
        return GetBlobUrl(containerName, blobName);
    }

    public Task DeleteBlobAsync(string containerName, string blobName)
    {
        Deleted.Add(Key(containerName, blobName));
        Blobs.Remove(Key(containerName, blobName));
        return Task.CompletedTask;
    }

    public string GetBlobUrl(string containerName, string blobName) =>
        $"https://blobs.test/{containerName}/{blobName}";

    /// <summary>Every download-URL request, so a test can assert HOW the URL was signed and
    /// not merely that it was. A non-null DownloadFileName means the real service stamps an
    /// attachment disposition on the SAS — which renders blank in a frame.</summary>
    public List<(string Container, string Blob, string? DownloadFileName)> DownloadUrlRequests
    { get; } = [];

    public Task<string> GenerateDownloadUrlAsync(
        string containerName, string blobName, TimeSpan expiresIn, string? downloadFileName = null)
    {
        DownloadUrlRequests.Add((containerName, blobName, downloadFileName));
        return Task.FromResult($"{GetBlobUrl(containerName, blobName)}?sig=test");
    }

    public Task<string> GenerateUploadUrlAsync(
        string containerName, string blobName, string contentType, TimeSpan expiresIn) =>
        Task.FromResult($"{GetBlobUrl(containerName, blobName)}?sig=upload");

    public Task<long?> GetBlobSizeAsync(
        string containerName, string blobName, CancellationToken ct = default) =>
        Task.FromResult<long?>(
            Blobs.TryGetValue(Key(containerName, blobName), out var bytes) ? bytes.Length : null);

    public Task DeleteContainerAsync(string containerName, CancellationToken ct = default)
    {
        foreach (var key in Blobs.Keys.Where(k => k.StartsWith($"{containerName}/")).ToList())
        {
            Deleted.Add(key);
            Blobs.Remove(key);
        }
        return Task.CompletedTask;
    }

    public static string Key(string containerName, string blobName) => $"{containerName}/{blobName}";
}
