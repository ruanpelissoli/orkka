namespace Orkka.Tests.Persistence;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Documents.Contracts;
using Orkka.Modules.Documents.Infrastructure;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Persistence;

/// <summary>
/// Contains() over a NULLABLE Ulid column once failed to translate (the column's Ulid?→Guid?
/// value converter broke collection-parameter mapping on the previous provider), which
/// surfaced as a 500 on the agent-chat messages endpoint. GetChatCreatedTasks/DocumentsAsync
/// therefore filter by project server-side and match SourceMessageId in memory. These tests
/// compile those exact query shapes against the real provider (SQLite, configured exactly as
/// the host configures it) via ToQueryString() — no connection needed — so a regression in a
/// shape the code relies on fails fast.
/// </summary>
public sealed class NullableUlidContainsTranslationTests
{
    private static DbContextOptions<TContext> SqliteOptions<TContext>() where TContext : DbContext =>
        (DbContextOptions<TContext>)new DbContextOptionsBuilder<TContext>()
            .UseOrkkaSqlite<TContext>(SqliteDatabase.ConnectionStringFor("never-opened.db"), "test")
            .Options;

    [Fact]
    public void Contains_on_non_nullable_ulid_column_compiles()
    {
        using var db = new TasksDbContext(SqliteOptions<TasksDbContext>());
        var ids = new List<Ulid> { Ulid.NewUlid() };

        var sql = db.Tasks.AsNoTracking()
            .Where(t => ids.Contains(t.Id))
            .ToQueryString();

        sql.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ChatCreatedTasks_query_compiles()
    {
        using var db = new TasksDbContext(SqliteOptions<TasksDbContext>());
        var projectId = Ulid.NewUlid();

        var sql = db.Tasks.AsNoTracking()
            .Where(t => t.ProjectId == projectId && t.SourceMessageId != null)
            .OrderBy(t => t.Number)
            .Select(t => new ChatTaskLink(t.SourceMessageId!.Value, t.Id, t.Number, t.Title))
            .ToQueryString();

        sql.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ChatCreatedDocuments_query_compiles()
    {
        using var db = new DocumentsDbContext(SqliteOptions<DocumentsDbContext>());
        var projectId = Ulid.NewUlid();

        var sql = db.Documents.AsNoTracking()
            .Where(d => d.ProjectId == projectId && d.SourceMessageId != null)
            .Select(d => new ChatDocumentLink(d.SourceMessageId!.Value, d.Id, d.Title))
            .ToQueryString();

        sql.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ChatRevisedDocuments_query_compiles()
    {
        using var db = new DocumentsDbContext(SqliteOptions<DocumentsDbContext>());
        var projectId = Ulid.NewUlid();

        var sql = db.DocumentRevisions.AsNoTracking()
            .Where(r => r.Document.ProjectId == projectId && r.SourceMessageId != null)
            .Select(r => new ChatDocumentLink(r.SourceMessageId!.Value, r.DocumentId, r.Document.Title))
            .ToQueryString();

        sql.Should().NotBeNullOrWhiteSpace();
    }
}
