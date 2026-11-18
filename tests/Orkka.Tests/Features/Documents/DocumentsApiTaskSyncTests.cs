namespace Orkka.Tests.Features.Documents;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Orkka.Modules.Documents.Contracts;
using Orkka.Modules.Documents.Domain;
using Orkka.Modules.Documents.Infrastructure;
using Orkka.Modules.Documents.PublicApi;
using Orkka.SharedKernel.Documents;
using Orkka.Tests.Utilities;

/// <summary>
/// SyncTaskDocumentsAsync is how a finished task's documentation pass persists its work. Unlike
/// the chat upsert it targets updates by id — a rewrite may change title AND path, so title+path
/// matching cannot find the document it means to revise — and it records the causing task on the
/// document (creation) and on every revision (each write).
/// </summary>
public sealed class DocumentsApiTaskSyncTests : IDisposable
{
    private readonly DocumentsDbContext _db;
    private readonly DocumentsApi _api;

    private readonly Ulid _orgId = Ulid.NewUlid();
    private readonly Ulid _projectId = Ulid.NewUlid();
    private readonly Ulid _taskId = Ulid.NewUlid();
    private readonly Ulid _agentId = Ulid.NewUlid();

    public DocumentsApiTaskSyncTests()
    {
        var options = new DbContextOptionsBuilder<DocumentsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new DocumentsDbContext(options);
        var blobs = new InMemoryBlobStorage();
        _api = new DocumentsApi(
            _db,
            blobs,
            new PdfTextSidecarService(
                blobs, new MemoryCache(new MemoryCacheOptions()),
                NullLogger<PdfTextSidecarService>.Instance),
            NullLogger<DocumentsApi>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private Task<IReadOnlyList<CreatedChatDocument>> SyncAsync(params TaskDocumentSpec[] specs) =>
        _api.SyncTaskDocumentsAsync(
            _orgId, _projectId, _taskId, _agentId, "user_creator", specs);

    private async Task<DocumentEntity> SeedDocumentAsync(
        string title, string path, string content, bool isFile = false)
    {
        var document = new DocumentEntity
        {
            Id = Ulid.NewUlid(),
            OrgId = _orgId,
            ProjectId = _projectId,
            Title = title,
            Path = path,
            Content = isFile ? "" : content,
            Source = isFile ? DocumentSource.Manual : DocumentSource.Agent,
            FileBlobName = isFile ? "blob.pdf" : null,
            FileName = isFile ? "spec.pdf" : null,
            FileContentType = isFile ? DocumentRules.PdfContentType : null,
        };
        _db.Documents.Add(document);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return document;
    }

    [Fact]
    public async Task Creating_records_the_task_on_the_document_and_its_first_revision()
    {
        var result = await SyncAsync(new TaskDocumentSpec(null, "Auth flow", "decisions", "# How auth works"));

        var saved = result.Should().ContainSingle().Subject;
        saved.Outcome.Should().Be(ChatDocumentOutcome.Created);
        saved.Version.Should().Be(1);

        var document = (await _db.Documents.ToListAsync()).Should().ContainSingle().Subject;
        document.Source.Should().Be(DocumentSource.Task);
        document.SourceTaskId.Should().Be(_taskId);
        document.SourceAgentId.Should().Be(_agentId);

        var revision = (await _db.DocumentRevisions.ToListAsync()).Should().ContainSingle().Subject;
        revision.Version.Should().Be(1);
        revision.SourceTaskId.Should().Be(_taskId);
        revision.ChangedByAgentId.Should().Be(_agentId);
        revision.RequestedByUserId.Should().Be("user_creator");
    }

    [Fact]
    public async Task Updating_by_id_revises_in_place_even_when_title_and_path_change()
    {
        var existing = await SeedDocumentAsync("Auth flow", "decisions", "# old");

        var result = await SyncAsync(new TaskDocumentSpec(
            existing.Id, "Authentication", "architecture", "# new"));

        var saved = result.Should().ContainSingle().Subject;
        saved.Outcome.Should().Be(ChatDocumentOutcome.Updated);
        saved.Version.Should().Be(2);

        var document = (await _db.Documents.ToListAsync()).Should().ContainSingle().Subject;
        document.Id.Should().Be(existing.Id, "the rewrite revises the document, never forks it");
        document.Title.Should().Be("Authentication");
        document.Path.Should().Be("architecture");
        document.Content.Should().Be("# new");
        // Creation provenance belongs to whoever created it; this task only revised it.
        document.SourceTaskId.Should().BeNull();

        var revision = (await _db.DocumentRevisions.ToListAsync()).Should().ContainSingle().Subject;
        revision.Version.Should().Be(2);
        revision.SourceTaskId.Should().Be(_taskId);
    }

    [Fact]
    public async Task Identical_content_does_not_burn_a_version()
    {
        var existing = await SeedDocumentAsync("Auth flow", "decisions", "# same");

        var result = await SyncAsync(new TaskDocumentSpec(
            existing.Id, "Auth flow", "decisions", "# same"));

        result.Should().ContainSingle().Which.Outcome.Should().Be(ChatDocumentOutcome.Unchanged);
        (await _db.Documents.SingleAsync()).CurrentVersion.Should().Be(1);
        (await _db.DocumentRevisions.ToListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task File_documents_are_never_written()
    {
        var pdf = await SeedDocumentAsync("Spec", "", "", isFile: true);

        var result = await SyncAsync(new TaskDocumentSpec(pdf.Id, "Spec", "", "# markdown"));

        result.Should().BeEmpty();
        var document = await _db.Documents.SingleAsync();
        document.Content.Should().BeEmpty("a PDF's body is its stored bytes, not markdown");
        (await _db.DocumentRevisions.ToListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task An_id_from_another_project_is_skipped_rather_than_created()
    {
        var foreign = new DocumentEntity
        {
            Id = Ulid.NewUlid(),
            OrgId = _orgId,
            ProjectId = Ulid.NewUlid(),
            Title = "Elsewhere",
            Path = "",
            Content = "# theirs",
        };
        _db.Documents.Add(foreign);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var result = await SyncAsync(new TaskDocumentSpec(foreign.Id, "Elsewhere", "", "# mine"));

        result.Should().BeEmpty("falling back to a create would duplicate what it meant to revise");
        (await _db.Documents.SingleAsync(d => d.Id == foreign.Id)).Content.Should().Be("# theirs");
    }

    [Fact]
    public async Task A_create_colliding_on_title_and_path_revises_the_existing_document()
    {
        var existing = await SeedDocumentAsync("Auth flow", "decisions", "# old");

        var result = await SyncAsync(new TaskDocumentSpec(null, "Auth flow", "decisions", "# new"));

        result.Should().ContainSingle().Which.Outcome.Should().Be(ChatDocumentOutcome.Updated);
        (await _db.Documents.ToListAsync()).Should().ContainSingle()
            .Which.Id.Should().Be(existing.Id, "two tasks documenting one area must not produce rivals");
    }

    [Fact]
    public async Task Task_document_links_report_created_and_updated_separately()
    {
        var existing = await SeedDocumentAsync("Auth flow", "decisions", "# old");

        await SyncAsync(
            new TaskDocumentSpec(null, "Deployment", "ops", "# how we deploy"),
            new TaskDocumentSpec(existing.Id, "Auth flow", "decisions", "# new"));

        var links = await _api.GetTaskDocumentLinksAsync(_projectId, _taskId);

        links.Should().HaveCount(2);
        links.Should().ContainSingle(l => l.Title == "Deployment")
            .Which.Outcome.Should().Be(ChatDocumentOutcome.Created);
        links.Should().ContainSingle(l => l.Title == "Auth flow")
            .Which.Outcome.Should().Be(ChatDocumentOutcome.Updated);
    }

    [Fact]
    public async Task A_created_document_is_reported_once_despite_also_having_a_revision()
    {
        await SyncAsync(new TaskDocumentSpec(null, "Auth flow", "decisions", "# v1"));

        var links = await _api.GetTaskDocumentLinksAsync(_projectId, _taskId);

        links.Should().ContainSingle()
            .Which.Outcome.Should().Be(ChatDocumentOutcome.Created);
    }

    [Fact]
    public async Task Summaries_flag_file_documents_so_callers_can_exclude_them()
    {
        await SeedDocumentAsync("Guide", "", "# markdown");
        await SeedDocumentAsync("Spec", "", "", isFile: true);

        var summaries = await _api.ListDocumentSummariesAsync(_orgId, _projectId);

        summaries.Should().ContainSingle(s => s.Title == "Guide").Which.IsFile.Should().BeFalse();
        summaries.Should().ContainSingle(s => s.Title == "Spec").Which.IsFile.Should().BeTrue();
    }
}
