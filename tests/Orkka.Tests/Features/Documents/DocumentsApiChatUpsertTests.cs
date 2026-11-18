namespace Orkka.Tests.Features.Documents;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Orkka.Modules.Documents.Contracts;
using Orkka.Modules.Documents.Infrastructure;
using Orkka.Modules.Documents.PublicApi;
using Orkka.SharedKernel.Documents;
using Orkka.Tests.Utilities;

/// <summary>
/// CreateChatDocumentsAsync upserts by title+path: re-saving an existing document revises it
/// in place (CurrentVersion bump + revision snapshot carrying the updating message's
/// provenance) instead of inserting a duplicate row, and GetChatCreatedDocumentsAsync links
/// both the creating and the updating message to the document.
/// </summary>
public sealed class DocumentsApiChatUpsertTests : IDisposable
{
    private readonly DocumentsDbContext _db;
    private readonly DocumentsApi _api;

    private readonly Ulid _orgId = Ulid.NewUlid();
    private readonly Ulid _projectId = Ulid.NewUlid();
    private readonly Ulid _agentId = Ulid.NewUlid();
    private readonly Ulid _conversationId = Ulid.NewUlid();

    public DocumentsApiChatUpsertTests()
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

    private Task<IReadOnlyList<CreatedChatDocument>> SaveFromChatAsync(
        Ulid messageId, params ChatDocumentSpec[] specs) =>
        _api.CreateChatDocumentsAsync(
            _orgId, _projectId, _agentId, _conversationId, messageId, "user_requester", specs);

    [Fact]
    public async Task New_document_is_created_with_revision_one()
    {
        var messageId = Ulid.NewUlid();

        var result = await SaveFromChatAsync(messageId, new ChatDocumentSpec("Auth decision", "decisions", "# v1"));

        var saved = result.Should().ContainSingle().Subject;
        saved.Outcome.Should().Be(ChatDocumentOutcome.Created);
        saved.Version.Should().Be(1);

        var document = (await _db.Documents.ToListAsync()).Should().ContainSingle().Subject;
        document.CurrentVersion.Should().Be(1);
        document.SourceMessageId.Should().Be(messageId);

        var revision = (await _db.DocumentRevisions.ToListAsync()).Should().ContainSingle().Subject;
        revision.Version.Should().Be(1);
        revision.SourceMessageId.Should().Be(messageId);
        revision.ChangedByAgentId.Should().Be(_agentId);
        revision.RequestedByUserId.Should().Be("user_requester");
    }

    [Fact]
    public async Task Same_title_and_path_revises_existing_document_instead_of_duplicating()
    {
        await SaveFromChatAsync(Ulid.NewUlid(), new ChatDocumentSpec("Auth decision", "decisions", "# v1"));
        var updateMessageId = Ulid.NewUlid();

        var result = await SaveFromChatAsync(
            updateMessageId, new ChatDocumentSpec("Auth decision", "decisions", "# v2"));

        var saved = result.Should().ContainSingle().Subject;
        saved.Outcome.Should().Be(ChatDocumentOutcome.Updated);
        saved.Version.Should().Be(2);

        var document = (await _db.Documents.ToListAsync()).Should().ContainSingle().Subject;
        document.Content.Should().Be("# v2");
        document.CurrentVersion.Should().Be(2);

        var revisions = await _db.DocumentRevisions.OrderBy(r => r.Version).ToListAsync();
        revisions.Should().HaveCount(2);
        revisions[1].Content.Should().Be("# v2");
        revisions[1].SourceMessageId.Should().Be(updateMessageId);
        revisions[1].ChangedByAgentId.Should().Be(_agentId);
    }

    [Fact]
    public async Task Identical_content_is_a_no_op_and_burns_no_revision()
    {
        await SaveFromChatAsync(Ulid.NewUlid(), new ChatDocumentSpec("Auth decision", "decisions", "# v1"));

        var result = await SaveFromChatAsync(
            Ulid.NewUlid(), new ChatDocumentSpec("Auth decision", "decisions", "# v1"));

        var saved = result.Should().ContainSingle().Subject;
        saved.Outcome.Should().Be(ChatDocumentOutcome.Unchanged);
        saved.Version.Should().Be(1);

        (await _db.Documents.CountAsync()).Should().Be(1);
        (await _db.DocumentRevisions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Same_title_in_a_different_path_creates_a_separate_document()
    {
        await SaveFromChatAsync(Ulid.NewUlid(), new ChatDocumentSpec("Notes", "backend", "# backend"));

        var result = await SaveFromChatAsync(Ulid.NewUlid(), new ChatDocumentSpec("Notes", "frontend", "# frontend"));

        result.Should().ContainSingle().Which.Outcome.Should().Be(ChatDocumentOutcome.Created);
        (await _db.Documents.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Repeated_spec_within_one_batch_upserts_the_pending_document()
    {
        var messageId = Ulid.NewUlid();

        var result = await SaveFromChatAsync(
            messageId,
            new ChatDocumentSpec("Auth decision", "decisions", "# v1"),
            new ChatDocumentSpec("Auth decision", "decisions", "# v2"));

        result.Should().HaveCount(2);
        result[0].Outcome.Should().Be(ChatDocumentOutcome.Created);
        result[1].Outcome.Should().Be(ChatDocumentOutcome.Updated);

        var document = (await _db.Documents.ToListAsync()).Should().ContainSingle().Subject;
        document.Content.Should().Be("# v2");
        document.CurrentVersion.Should().Be(2);
        (await _db.DocumentRevisions.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Chat_links_cover_both_the_creating_and_the_updating_message()
    {
        var createMessageId = Ulid.NewUlid();
        var updateMessageId = Ulid.NewUlid();
        await SaveFromChatAsync(createMessageId, new ChatDocumentSpec("Auth decision", "decisions", "# v1"));
        await SaveFromChatAsync(updateMessageId, new ChatDocumentSpec("Auth decision", "decisions", "# v2"));

        var links = await _api.GetChatCreatedDocumentsAsync(
            _projectId, [createMessageId, updateMessageId]);

        var documentId = (await _db.Documents.SingleAsync()).Id;
        links.Should().HaveCount(2);
        links.Should().ContainSingle(l => l.SourceMessageId == createMessageId)
            .Which.Id.Should().Be(documentId);
        links.Should().ContainSingle(l => l.SourceMessageId == updateMessageId)
            .Which.Id.Should().Be(documentId);
    }

    [Fact]
    public async Task Brief_document_revised_from_chat_keeps_its_source_and_links_the_message()
    {
        await _api.CreateBriefDocumentsAsync(
            _orgId, _projectId, "user_creator", [new ChatDocumentSpec("PRD", "", "# draft")]);
        var messageId = Ulid.NewUlid();

        var result = await SaveFromChatAsync(messageId, new ChatDocumentSpec("PRD", "", "# refined"));

        result.Should().ContainSingle().Which.Outcome.Should().Be(ChatDocumentOutcome.Updated);

        var document = (await _db.Documents.ToListAsync()).Should().ContainSingle().Subject;
        document.Source.Should().Be(DocumentSource.Brief);
        document.Content.Should().Be("# refined");
        document.CurrentVersion.Should().Be(2);

        var links = await _api.GetChatCreatedDocumentsAsync(_projectId, [messageId]);
        links.Should().ContainSingle().Which.Id.Should().Be(document.Id);
    }
}
