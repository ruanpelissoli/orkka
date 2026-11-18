namespace Orkka.IntegrationTests.Fixtures;

using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Projects.Infrastructure;

public sealed class MockBlobStorageService : IBlobStorageService
{
    public Task<string> GenerateUploadUrlAsync(
        string containerName, string blobName, string contentType, TimeSpan expiresIn)
    {
        return Task.FromResult($"https://test.blob.core.windows.net/{containerName}/{blobName}?sas=mock");
    }

    public Task<string> GenerateDownloadUrlAsync(
        string containerName, string blobName, TimeSpan expiresIn, string? downloadFileName = null)
    {
        var suffix = downloadFileName is null ? "" : $"&filename={Uri.EscapeDataString(downloadFileName)}";
        return Task.FromResult(
            $"https://test.blob.core.windows.net/{containerName}/{blobName}?sas=mock-read{suffix}");
    }

    public string GetBlobUrl(string containerName, string blobName)
    {
        return $"https://test.blob.core.windows.net/{containerName}/{blobName}";
    }

    public Task DeleteBlobAsync(string containerName, string blobName)
    {
        return Task.CompletedTask;
    }

    public Task<string> UploadAsync(string container, string blobName, Stream content, string contentType, CancellationToken ct = default)
    {
        return Task.FromResult($"https://test.blob.core.windows.net/{container}/{blobName}");
    }

    public Task<Stream?> DownloadAsync(string container, string blobName, CancellationToken ct = default)
    {
        return Task.FromResult<Stream?>(null);
    }

    /// <summary>Settable per-test: key is "{container}/{blobName}"; missing key = blob absent.</summary>
    public Dictionary<string, long> BlobSizes { get; } = [];

    /// <summary>Containers removed by DeleteContainerAsync — deprovisioning assertions.</summary>
    public List<string> DeletedContainers { get; } = [];

    public Task DeleteContainerAsync(string containerName, CancellationToken ct = default)
    {
        DeletedContainers.Add(containerName);
        return Task.CompletedTask;
    }

    public Task<long?> GetBlobSizeAsync(
        string containerName, string blobName, CancellationToken ct = default)
    {
        return Task.FromResult<long?>(
            BlobSizes.TryGetValue($"{containerName}/{blobName}", out var size) ? size : null);
    }
}
