namespace Orkka.Tests.Worker;

using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Orkka.SharedKernel.Documents;
using Orkka.SharedKernel.Storage;
using Orkka.Tests.Utilities;

public sealed class PdfTextSidecarServiceTests
{
    private const string Container = "task-attachments";
    private const string PdfBlob = "org/task/01H-spec.pdf";
    private const string SidecarBlob = PdfBlob + ".txt";

    private static (PdfTextSidecarService Service, FakeBlobStorage Storage) Create(FakeBlobStorage? storage = null)
    {
        storage ??= new FakeBlobStorage();
        var service = new PdfTextSidecarService(
            storage, new MemoryCache(new MemoryCacheOptions()), NullLogger<PdfTextSidecarService>.Instance);
        return (service, storage);
    }

    private static Task<PdfSidecar?> GetOrCreate(PdfTextSidecarService service, long size = 4096) =>
        service.GetOrCreateAsync(Container, PdfBlob, "spec.pdf", size);

    [Fact]
    public async Task GetOrCreateAsync_PublishesTheExtractedTextAsATxtSibling()
    {
        var (service, storage) = Create();
        storage.Blobs[Key(PdfBlob)] = TestPdfs.SinglePage("Wave 25 rollout plan");

        var sidecar = await GetOrCreate(service);

        sidecar!.BlobName.Should().Be(SidecarBlob);
        var text = Encoding.UTF8.GetString(storage.Blobs[Key(SidecarBlob)]);
        text.Should().StartWith("# Text extracted by Orkka from spec.pdf (1 page)");
        text.Should().Contain("rollout").And.Contain("Wave");
        sidecar.Bytes.Should().Be(storage.Blobs[Key(SidecarBlob)].Length);
        storage.UploadedContentTypes.Should().ContainSingle().Which.Should().StartWith("text/plain");
    }

    [Fact]
    public async Task GetOrCreateAsync_ExtractsOncePerPdf()
    {
        // Every phase of every task re-renders the attachments section — re-parsing nine
        // PDFs each time would make prompt building the slowest part of a run.
        var (service, storage) = Create();
        storage.Blobs[Key(PdfBlob)] = TestPdfs.SinglePage("Wave 25 rollout plan");

        await GetOrCreate(service);
        var downloadsAfterFirst = storage.Downloads;
        var second = await GetOrCreate(service);

        second!.BlobName.Should().Be(SidecarBlob);
        storage.Uploads.Should().Be(1);
        storage.Downloads.Should().Be(downloadsAfterFirst);
    }

    [Fact]
    public async Task GetOrCreateAsync_ReusesASidecarLeftByAnEarlierRun()
    {
        // Cold process (worker restart, second replica): the blob is the durable cache.
        var storage = new FakeBlobStorage();
        storage.Blobs[Key(PdfBlob)] = TestPdfs.SinglePage("Wave 25 rollout plan");
        storage.Blobs[Key(SidecarBlob)] = Encoding.UTF8.GetBytes("# already extracted\nbody");

        var (service, _) = Create(storage);
        var sidecar = await GetOrCreate(service);

        sidecar!.BlobName.Should().Be(SidecarBlob);
        sidecar.Bytes.Should().Be(storage.Blobs[Key(SidecarBlob)].Length);
        storage.Uploads.Should().Be(0);
        Encoding.UTF8.GetString(storage.Blobs[Key(SidecarBlob)]).Should().StartWith("# already extracted");
    }

    [Fact]
    public async Task GetOrCreateAsync_SkipsPdfsOverTheSizeCap()
    {
        var (service, storage) = Create();
        storage.Blobs[Key(PdfBlob)] = TestPdfs.SinglePage("Wave 25 rollout plan");

        var sidecar = await service.GetOrCreateAsync(
            Container, PdfBlob, "spec.pdf", PdfTextSidecarService.MaxPdfBytes + 1);

        sidecar.Should().BeNull();
        storage.Uploads.Should().Be(0);
        storage.Blobs.Should().NotContainKey(Key(SidecarBlob));
    }

    [Fact]
    public async Task GetOrCreateAsync_EnforcesTheSizeCapAgainstTheRealBytes()
    {
        // FileSize comes from the upload client — a lying (or stale) value must not let an
        // arbitrarily large blob through.
        var (service, storage) = Create();
        storage.Blobs[Key(PdfBlob)] = new byte[PdfTextSidecarService.MaxPdfBytes + 1024];

        var sidecar = await GetOrCreate(service, size: 4096);

        sidecar.Should().BeNull();
        storage.Uploads.Should().Be(0);
    }

    [Fact]
    public async Task GetOrCreateAsync_ReturnsNullForImageOnlyPdfs()
    {
        var (service, storage) = Create();
        storage.Blobs[Key(PdfBlob)] = TestPdfs.ImageOnly();

        var sidecar = await GetOrCreate(service);

        sidecar.Should().BeNull();
        storage.Blobs.Should().NotContainKey(Key(SidecarBlob));
    }

    [Fact]
    public async Task GetOrCreateAsync_ReturnsNullWhenThePdfBlobIsGone()
    {
        var (service, _) = Create();

        (await GetOrCreate(service)).Should().BeNull();
    }

    [Fact]
    public async Task GetOrCreateAsync_SwallowsStorageFailures()
    {
        // Prompt building must survive a storage hiccup — the attachment is then rendered
        // without its text sibling instead of the task failing.
        var (service, storage) = Create();
        storage.ThrowOnDownload = true;

        (await GetOrCreate(service)).Should().BeNull();
    }

    [Theory]
    [InlineData("spec.pdf", "application/octet-stream", true)]
    [InlineData("SPEC.PDF", "application/octet-stream", true)]
    [InlineData("spec", "application/pdf", true)]
    [InlineData("notes.pdf.md", "text/markdown", false)]
    [InlineData("mockup.png", "image/png", false)]
    public void IsPdf_LooksAtBothTheNameAndTheContentType(string fileName, string contentType, bool expected)
        => PdfTextSidecarService.IsPdf(fileName, contentType).Should().Be(expected);

    private static string Key(string blobName) => $"{Container}/{blobName}";

    private sealed class FakeBlobStorage : IBlobStorageService
    {
        public Dictionary<string, byte[]> Blobs { get; } = [];
        public List<string> UploadedContentTypes { get; } = [];
        public int Uploads { get; private set; }
        public int Downloads { get; private set; }
        public bool ThrowOnDownload { get; set; }

        public Task<Stream?> DownloadAsync(string containerName, string blobName, CancellationToken ct = default)
        {
            if (ThrowOnDownload)
                throw new InvalidOperationException("storage unavailable");

            Downloads++;
            return Task.FromResult<Stream?>(
                Blobs.TryGetValue($"{containerName}/{blobName}", out var bytes)
                    ? new MemoryStream(bytes, writable: false)
                    : null);
        }

        public async Task<string> UploadAsync(
            string containerName, string blobName, Stream content, string contentType,
            CancellationToken ct = default)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct);
            Blobs[$"{containerName}/{blobName}"] = buffer.ToArray();
            UploadedContentTypes.Add(contentType);
            Uploads++;
            return $"https://blobs.test/{containerName}/{blobName}";
        }

        public Task DeleteBlobAsync(string containerName, string blobName)
        {
            Blobs.Remove($"{containerName}/{blobName}");
            return Task.CompletedTask;
        }

        public Task<long?> GetBlobSizeAsync(
            string containerName, string blobName, CancellationToken ct = default) =>
            Task.FromResult<long?>(
                Blobs.TryGetValue($"{containerName}/{blobName}", out var bytes) ? bytes.Length : null);

        public Task DeleteContainerAsync(string containerName, CancellationToken ct = default)
        {
            foreach (var key in Blobs.Keys.Where(k => k.StartsWith($"{containerName}/")).ToList())
                Blobs.Remove(key);
            return Task.CompletedTask;
        }

        public string GetBlobUrl(string containerName, string blobName) =>
            $"https://blobs.test/{containerName}/{blobName}";

        public Task<string> GenerateDownloadUrlAsync(
            string containerName, string blobName, TimeSpan expiresIn, string? downloadFileName = null) =>
            Task.FromResult($"{GetBlobUrl(containerName, blobName)}?sig=test");

        public Task<string> GenerateUploadUrlAsync(
            string containerName, string blobName, string contentType, TimeSpan expiresIn) =>
            Task.FromResult($"{GetBlobUrl(containerName, blobName)}?sig=upload");
    }
}
