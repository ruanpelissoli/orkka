namespace Orkka.SharedKernel.Documents;

using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Orkka.SharedKernel.Storage;

/// <summary>An extracted-text blob sitting next to the PDF it came from.</summary>
public sealed record PdfSidecar(string BlobName, long Bytes);

public interface IPdfTextSidecarService
{
    /// <summary>Returns the extracted-text sibling of a PDF blob, extracting and storing it on
    /// first use. Null means there is no text to offer (image-only PDF, oversized file, missing
    /// blob, or a storage hiccup) — callers render the PDF alone in that case.</summary>
    Task<PdfSidecar?> GetOrCreateAsync(
        string containerName, string pdfBlobName, string fileName, long pdfSizeBytes,
        CancellationToken ct = default);
}

/// <summary>
/// Extraction happens on the host, once per PDF, and the result is stored as
/// <c>{blobName}.txt</c> in the same container — so agents get a plain-text file through the
/// existing signed-URL model instead of a PDF they cannot open and an extractor they may not
/// have. Sidecars are immutable, so a process-local cache keeps repeat prompt builds (every
/// phase of every task re-renders the attachments section) from re-reading storage.
/// </summary>
public sealed class PdfTextSidecarService(
    IBlobStorageService blobStorage,
    IMemoryCache cache,
    ILogger<PdfTextSidecarService> logger) : IPdfTextSidecarService
{
    public const string SidecarSuffix = ".txt";

    /// <summary>PDFs above this never get extracted — the download and parse would cost more
    /// prompt-build time than the text is worth. 20 MB covers ordinary specs and design docs.</summary>
    public const long MaxPdfBytes = 20L * 1024 * 1024;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    public static bool IsPdf(string fileName, string? contentType) =>
        fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase);

    public async Task<PdfSidecar?> GetOrCreateAsync(
        string containerName, string pdfBlobName, string fileName, long pdfSizeBytes,
        CancellationToken ct = default)
    {
        var cacheKey = $"pdf-sidecar:{containerName}/{pdfBlobName}";
        // Cached null is meaningful (this PDF has no text) — TryGetValue, not the value itself,
        // is what says whether we've already answered for this blob.
        if (cache.TryGetValue(cacheKey, out PdfSidecar? cached))
            return cached;

        var sidecarName = pdfBlobName + SidecarSuffix;

        try
        {
            var existing = await MeasureExistingAsync(containerName, sidecarName, ct);
            if (existing is not null)
                return Remember(cacheKey, new PdfSidecar(sidecarName, existing.Value));

            if (pdfSizeBytes > MaxPdfBytes)
            {
                logger.LogInformation(
                    "Skipping PDF text extraction for {Blob} — {Size:N0} bytes exceeds the {Max:N0} byte cap",
                    pdfBlobName, pdfSizeBytes, MaxPdfBytes);
                return Remember(cacheKey, null);
            }

            var pdfBytes = await ReadPdfAsync(containerName, pdfBlobName, ct);
            if (pdfBytes is null)
                return Remember(cacheKey, null);

            var result = PdfTextExtractor.Extract(pdfBytes);
            if (result.Text is null)
            {
                logger.LogInformation("No text extracted from PDF {Blob}: {Reason}", pdfBlobName, result.Failure);
                return Remember(cacheKey, null);
            }

            var content = Compose(fileName, result);
            var bytes = Encoding.UTF8.GetBytes(content);

            await blobStorage.UploadAsync(
                containerName, sidecarName, new MemoryStream(bytes), "text/plain; charset=utf-8", ct);

            logger.LogInformation(
                "Extracted {Chars:N0} chars from {Pages} page(s) of PDF {Blob} into {Sidecar}",
                result.Text.Length, result.PageCount, pdfBlobName, sidecarName);

            return Remember(cacheKey, new PdfSidecar(sidecarName, bytes.Length));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A storage failure must not take down prompt building — the attachment is simply
            // rendered without its text sibling. Not cached: the next build retries.
            logger.LogWarning(ex, "PDF text extraction failed for {Container}/{Blob}", containerName, pdfBlobName);
            return null;
        }
    }

    /// <summary>Size of an already-extracted sidecar, or null when it doesn't exist yet.</summary>
    private async Task<long?> MeasureExistingAsync(string containerName, string sidecarName, CancellationToken ct)
    {
        await using var stream = await blobStorage.DownloadAsync(containerName, sidecarName, ct);
        if (stream is null)
            return null;

        if (stream.CanSeek)
            return stream.Length;

        var counter = new CountingStream();
        await stream.CopyToAsync(counter, ct);
        return counter.BytesWritten;
    }

    private async Task<byte[]?> ReadPdfAsync(string containerName, string pdfBlobName, CancellationToken ct)
    {
        await using var stream = await blobStorage.DownloadAsync(containerName, pdfBlobName, ct);
        if (stream is null)
        {
            logger.LogWarning("PDF blob {Container}/{Blob} is missing — no text to extract", containerName, pdfBlobName);
            return null;
        }

        // The recorded FileSize is client-supplied; enforce the cap against the real bytes too.
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxPdfBytes)
            {
                logger.LogInformation(
                    "Skipping PDF text extraction for {Blob} — content exceeds the {Max:N0} byte cap",
                    pdfBlobName, MaxPdfBytes);
                return null;
            }
        }

        return buffer.ToArray();
    }

    /// <summary>The header tells the agent what it is holding; the footer is only present when
    /// the cap cut the document short, so nobody mistakes a partial spec for the whole one.</summary>
    private static string Compose(string fileName, PdfTextResult result)
    {
        var sb = new StringBuilder();
        sb.Append("# Text extracted by Orkka from ").Append(fileName)
          .Append(" (").Append(result.PageCount).Append(result.PageCount == 1 ? " page" : " pages")
          .AppendLine(")");
        sb.AppendLine("# Layout is approximate; images, charts and handwriting are not included.");
        sb.Append(result.Text);
        if (result.Truncated)
        {
            sb.AppendLine();
            sb.AppendLine($"... (truncated at {PdfTextExtractor.DefaultMaxChars:N0} characters — " +
                          "the rest of the document was not extracted)");
        }
        return sb.ToString();
    }

    private PdfSidecar? Remember(string cacheKey, PdfSidecar? sidecar)
    {
        cache.Set(cacheKey, sidecar, CacheTtl);
        return sidecar;
    }

    private sealed class CountingStream : Stream
    {
        public long BytesWritten { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => BytesWritten += count;
        public override void Write(ReadOnlySpan<byte> buffer) => BytesWritten += buffer.Length;
    }
}
