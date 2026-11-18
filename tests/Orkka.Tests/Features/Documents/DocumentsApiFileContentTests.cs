namespace Orkka.Tests.Features.Documents;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Orkka.Modules.Documents.Contracts;
using Orkka.Modules.Documents.Domain;
using Orkka.Modules.Documents.Features.Documents;
using Orkka.Modules.Documents.Infrastructure;
using Orkka.Modules.Documents.PublicApi;
using Orkka.SharedKernel.Documents;
using Orkka.Tests.Utilities;

/// <summary>
/// An uploaded PDF is stored as the file it is, so its Content column is empty. Agents cannot
/// read a PDF, so GetDocumentsContentAsync — the prompt-injection path — has to substitute the
/// extracted-text sidecar; without it, attaching a PDF to a task would inject nothing at all.
/// </summary>
public sealed class DocumentsApiFileContentTests : IDisposable
{
    private readonly DocumentsDbContext _db;
    private readonly InMemoryBlobStorage _blobs = new();
    private readonly DocumentsApi _api;

    private readonly Ulid _orgId = Ulid.NewUlid();
    private readonly Ulid _projectId = Ulid.NewUlid();

    public DocumentsApiFileContentTests()
    {
        var options = new DbContextOptionsBuilder<DocumentsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new DocumentsDbContext(options);
        _api = new DocumentsApi(
            _db,
            _blobs,
            new PdfTextSidecarService(
                _blobs, new MemoryCache(new MemoryCacheOptions()),
                NullLogger<PdfTextSidecarService>.Instance),
            NullLogger<DocumentsApi>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private async Task<DocumentEntity> AddFileDocumentAsync(byte[]? pdfBytes)
    {
        var id = Ulid.NewUlid();
        var blobName = DocumentFileStorage.BlobName(_orgId, _projectId, id, ".pdf");

        if (pdfBytes is not null)
            _blobs.Blobs[InMemoryBlobStorage.Key(DocumentFileStorage.ContainerName, blobName)] = pdfBytes;

        var entity = new DocumentEntity
        {
            Id = id,
            OrgId = _orgId,
            ProjectId = _projectId,
            Title = "Wave 25 spec",
            Path = "specs",
            Content = "",
            FileBlobName = blobName,
            FileContentType = DocumentRules.PdfContentType,
            FileName = "wave-25-spec.pdf",
            FileSizeBytes = pdfBytes?.Length ?? 0,
            Source = DocumentSource.Manual,
        };
        _db.Documents.Add(entity);
        await _db.SaveChangesAsync();
        return entity;
    }

    [Fact]
    public async Task File_document_content_comes_from_the_extracted_text_sidecar()
    {
        var document = await AddFileDocumentAsync(TestPdfs.SinglePage("Wave 25 rollout plan"));

        var contents = await _api.GetDocumentsContentAsync(_orgId, _projectId, [document.Id]);

        var content = contents.Should().ContainSingle().Subject;
        content.Title.Should().Be("Wave 25 spec");
        content.Content.Should().Contain("Wave").And.Contain("rollout");
    }

    [Fact]
    public async Task Image_only_pdf_says_so_rather_than_injecting_an_empty_document()
    {
        var document = await AddFileDocumentAsync(TestPdfs.ImageOnly());

        var contents = await _api.GetDocumentsContentAsync(_orgId, _projectId, [document.Id]);

        contents.Should().ContainSingle().Which.Content
            .Should().Be("(No text could be extracted from this PDF.)");
    }

    [Fact]
    public async Task Missing_blob_does_not_fail_the_prompt_build()
    {
        var document = await AddFileDocumentAsync(pdfBytes: null);

        var contents = await _api.GetDocumentsContentAsync(_orgId, _projectId, [document.Id]);

        contents.Should().ContainSingle().Which.Content
            .Should().Be("(No text could be extracted from this PDF.)");
    }

    [Fact]
    public async Task Markdown_documents_still_return_their_own_body()
    {
        var entity = new DocumentEntity
        {
            Id = Ulid.NewUlid(),
            OrgId = _orgId,
            ProjectId = _projectId,
            Title = "Auth decision",
            Path = "decisions",
            Content = "# Auth\nWe use Clerk.",
            Source = DocumentSource.Manual,
        };
        _db.Documents.Add(entity);
        await _db.SaveChangesAsync();

        var contents = await _api.GetDocumentsContentAsync(_orgId, _projectId, [entity.Id]);

        contents.Should().ContainSingle().Which.Content.Should().Be("# Auth\nWe use Clerk.");
    }

    [Fact]
    public async Task Another_orgs_document_is_never_returned()
    {
        var document = await AddFileDocumentAsync(TestPdfs.SinglePage("Wave 25 rollout plan"));

        var contents = await _api.GetDocumentsContentAsync(Ulid.NewUlid(), _projectId, [document.Id]);

        contents.Should().BeEmpty();
    }
}
