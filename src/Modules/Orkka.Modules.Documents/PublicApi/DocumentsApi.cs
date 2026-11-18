namespace Orkka.Modules.Documents.PublicApi;

using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Documents.Contracts;
using Orkka.Modules.Documents.Domain;
using Orkka.Modules.Documents.Features.Documents;
using Orkka.Modules.Documents.Infrastructure;
using Orkka.SharedKernel.Documents;
using Orkka.SharedKernel.Storage;

public sealed class DocumentsApi(
    DocumentsDbContext db,
    IBlobStorageService blobStorage,
    IPdfTextSidecarService pdfTextSidecar,
    ILogger<DocumentsApi> logger) : IDocumentsApi
{
    public async Task<IReadOnlyList<CreatedChatDocument>> CreateChatDocumentsAsync(
        Ulid orgId, Ulid projectId, Ulid agentId, Ulid conversationId, Ulid messageId,
        string requesterUserId, IReadOnlyList<ChatDocumentSpec> specs, CancellationToken ct = default)
    {
        if (specs.Count == 0)
            return [];

        var created = new List<CreatedChatDocument>();

        foreach (var spec in specs)
        {
            // Defense in depth: the Agents module sanitizes before calling, but never trust it.
            var title = DocumentRules.NormalizeTitle(spec.Title);
            if (title is null || string.IsNullOrWhiteSpace(spec.Content))
                continue;

            var content = spec.Content.Length <= DocumentRules.MaxContentLength
                ? spec.Content
                : spec.Content[..DocumentRules.MaxContentLength];
            var path = DocumentRules.NormalizePath(spec.Path);

            // Upsert by title+path so agents re-saving a document revise it instead of
            // duplicating it. Check pending (Local) adds first — a batch can repeat a spec.
            var existing = db.Documents.Local.FirstOrDefault(
                    d => d.OrgId == orgId && d.ProjectId == projectId && d.Title == title && d.Path == path)
                ?? await db.Documents.FirstOrDefaultAsync(
                    d => d.OrgId == orgId && d.ProjectId == projectId && d.Title == title && d.Path == path, ct);

            if (existing is not null)
            {
                // Identical content shouldn't burn a revision number (mirrors UpdateDocumentEndpoint).
                if (existing.Content == content)
                {
                    created.Add(new CreatedChatDocument(
                        existing.Id, existing.Title, existing.CurrentVersion, ChatDocumentOutcome.Unchanged));
                    continue;
                }

                existing.Content = content;
                existing.CurrentVersion++;
                db.DocumentRevisions.Add(DocumentSnapshots.FromCurrentState(
                    existing, changedByUserId: null, changedByAgentId: agentId,
                    requestedByUserId: requesterUserId,
                    sourceConversationId: conversationId,
                    sourceMessageId: messageId));
                created.Add(new CreatedChatDocument(
                    existing.Id, existing.Title, existing.CurrentVersion, ChatDocumentOutcome.Updated));
                continue;
            }

            var entity = new DocumentEntity
            {
                Id = Ulid.NewUlid(),
                OrgId = orgId,
                ProjectId = projectId,
                Title = title,
                Path = path,
                Content = content,
                Source = DocumentSource.Agent,
                SourceAgentId = agentId,
                SourceConversationId = conversationId,
                SourceMessageId = messageId,
            };
            db.Documents.Add(entity);
            db.DocumentRevisions.Add(DocumentSnapshots.FromCurrentState(
                entity, changedByUserId: null, changedByAgentId: agentId,
                requestedByUserId: requesterUserId,
                sourceConversationId: conversationId,
                sourceMessageId: messageId));
            created.Add(new CreatedChatDocument(entity.Id, entity.Title));
        }

        if (created.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Saved {Count} chat documents ({Created} created, {Updated} updated, {Unchanged} unchanged) " +
                "for project {ProjectId} from message {MessageId}",
                created.Count,
                created.Count(c => c.Outcome == ChatDocumentOutcome.Created),
                created.Count(c => c.Outcome == ChatDocumentOutcome.Updated),
                created.Count(c => c.Outcome == ChatDocumentOutcome.Unchanged),
                projectId, messageId);
        }

        return created;
    }

    public async Task<IReadOnlyList<CreatedChatDocument>> CreateBriefDocumentsAsync(
        Ulid orgId, Ulid projectId, string? requesterUserId,
        IReadOnlyList<ChatDocumentSpec> specs, CancellationToken ct = default)
    {
        if (specs.Count == 0)
            return [];

        var created = new List<CreatedChatDocument>();

        foreach (var spec in specs)
        {
            var title = DocumentRules.NormalizeTitle(spec.Title);
            if (title is null || string.IsNullOrWhiteSpace(spec.Content))
                continue;

            var content = spec.Content.Length <= DocumentRules.MaxContentLength
                ? spec.Content
                : spec.Content[..DocumentRules.MaxContentLength];
            var path = DocumentRules.NormalizePath(spec.Path);

            // Generation retries re-run the whole step — skip documents that already exist.
            var exists = await db.Documents.AnyAsync(
                d => d.OrgId == orgId && d.ProjectId == projectId && d.Title == title && d.Path == path, ct);
            if (exists)
                continue;

            var entity = new DocumentEntity
            {
                Id = Ulid.NewUlid(),
                OrgId = orgId,
                ProjectId = projectId,
                Title = title,
                Path = path,
                Content = content,
                Source = DocumentSource.Brief,
            };
            db.Documents.Add(entity);
            db.DocumentRevisions.Add(DocumentSnapshots.FromCurrentState(
                entity, changedByUserId: null, changedByAgentId: null,
                requestedByUserId: requesterUserId));
            created.Add(new CreatedChatDocument(entity.Id, entity.Title));
        }

        if (created.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Created {Count} brief documents for project {ProjectId}", created.Count, projectId);
        }

        return created;
    }

    public async Task<IReadOnlyList<ChatDocumentLink>> GetChatCreatedDocumentsAsync(
        Ulid projectId, IReadOnlyCollection<Ulid> sourceMessageIds, CancellationToken ct = default)
    {
        if (sourceMessageIds.Count == 0)
            return [];

        // No Contains over the nullable SourceMessageId column (the Ulid?→Guid? converter has
        // broken collection-parameter translation before), so fetch the project's
        // chat-touched documents and match message ids in memory — the sets stay small.
        // Creation provenance lives on the document row; chat upserts record theirs on the
        // revision row — union both so a message that revised a document still gets its card.
        var createdLinks = await db.Documents.AsNoTracking()
            .Where(d => d.ProjectId == projectId && d.SourceMessageId != null)
            .Select(d => new ChatDocumentLink(d.SourceMessageId!.Value, d.Id, d.Title))
            .ToListAsync(ct);

        var revisedLinks = await db.DocumentRevisions.AsNoTracking()
            .Where(r => r.Document.ProjectId == projectId && r.SourceMessageId != null)
            .Select(r => new ChatDocumentLink(r.SourceMessageId!.Value, r.DocumentId, r.Document.Title))
            .ToListAsync(ct);

        var ids = sourceMessageIds.ToHashSet();
        return createdLinks.Concat(revisedLinks)
            .Where(c => ids.Contains(c.SourceMessageId))
            .DistinctBy(c => (c.SourceMessageId, c.Id))
            .ToList();
    }

    public async Task<IReadOnlyList<DocumentContent>> GetDocumentsContentAsync(
        Ulid orgId, Ulid projectId, IReadOnlyCollection<Ulid> documentIds, CancellationToken ct = default)
    {
        if (documentIds.Count == 0)
            return [];

        // Called from the Worker where no ICurrentUser exists and the tenant filter is bypassed —
        // org/project filtering here is the isolation boundary.
        var documents = await db.Documents.AsNoTracking()
            .Where(d => d.OrgId == orgId && d.ProjectId == projectId && documentIds.Contains(d.Id))
            .Select(d => new
            {
                d.Id, d.Title, d.Path, d.Content,
                d.FileBlobName, d.FileName, d.FileSizeBytes,
            })
            .ToListAsync(ct);

        var contents = new List<DocumentContent>(documents.Count);
        foreach (var d in documents)
        {
            // A file document's Content column is empty by design — the readable form of a PDF
            // is the extracted-text sidecar, the same one attachments hand to agents. Without
            // this, pulling a PDF into a task's context would inject an empty document.
            var content = d.FileBlobName is null
                ? d.Content
                : await ReadFileTextAsync(d.FileBlobName, d.FileName ?? d.Title, d.FileSizeBytes ?? 0, ct);

            contents.Add(new DocumentContent(d.Id, d.Title, d.Path, content));
        }

        return contents;
    }

    /// <summary>Extracted text for a stored PDF, or a short placeholder when there is none to
    /// give (image-only scan, oversized file, storage trouble) — an agent reading "no text could
    /// be extracted" can say so, where an empty string just looks like an empty document.</summary>
    private async Task<string> ReadFileTextAsync(
        string blobName, string fileName, long sizeBytes, CancellationToken ct)
    {
        const string unavailable = "(No text could be extracted from this PDF.)";

        try
        {
            var sidecar = await pdfTextSidecar.GetOrCreateAsync(
                DocumentFileStorage.ContainerName, blobName, fileName, sizeBytes, ct);
            if (sidecar is null)
                return unavailable;

            await using var stream = await blobStorage.DownloadAsync(
                DocumentFileStorage.ContainerName, sidecar.BlobName, ct);
            if (stream is null)
                return unavailable;

            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read extracted text for document blob {Blob}", blobName);
            return unavailable;
        }
    }

    public async Task<IReadOnlyList<DocumentSummary>> ListDocumentSummariesAsync(
        Ulid orgId, Ulid projectId, CancellationToken ct = default)
    {
        // Same isolation note as GetDocumentsContentAsync — no ICurrentUser in the calling scope.
        return await db.Documents.AsNoTracking()
            .Where(d => d.OrgId == orgId && d.ProjectId == projectId)
            .OrderBy(d => d.Path).ThenBy(d => d.Title)
            .Select(d => new DocumentSummary(
                d.Id, d.Title, d.Path, d.CurrentVersion, d.UpdatedAt, d.FileBlobName != null))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<CreatedChatDocument>> SyncTaskDocumentsAsync(
        Ulid orgId, Ulid projectId, Ulid taskId, Ulid agentId, string? requesterUserId,
        IReadOnlyList<TaskDocumentSpec> specs, CancellationToken ct = default)
    {
        if (specs.Count == 0)
            return [];

        var results = new List<CreatedChatDocument>();

        foreach (var spec in specs)
        {
            // Defense in depth: the sync service sanitizes before calling, but never trust it.
            var title = DocumentRules.NormalizeTitle(spec.Title);
            if (title is null || string.IsNullOrWhiteSpace(spec.Content))
                continue;

            var content = spec.Content.Length <= DocumentRules.MaxContentLength
                ? spec.Content
                : spec.Content[..DocumentRules.MaxContentLength];
            var path = DocumentRules.NormalizePath(spec.Path);

            var existing = spec.DocumentId is { } documentId
                ? await db.Documents.FirstOrDefaultAsync(
                    d => d.Id == documentId && d.OrgId == orgId && d.ProjectId == projectId, ct)
                : null;

            // The model named a document that was deleted, or belongs to another project. Falling
            // back to a create would silently duplicate whatever it meant to revise.
            if (spec.DocumentId is not null && existing is null)
            {
                logger.LogWarning(
                    "Task {TaskId} documentation targeted document {DocumentId}, which is not in "
                    + "project {ProjectId} — skipped", taskId, spec.DocumentId, projectId);
                continue;
            }

            // A file document's body is stored bytes; its Content column is empty by design, so
            // writing markdown into it would produce a document that renders as neither.
            if (existing?.FileBlobName is not null)
            {
                logger.LogWarning(
                    "Task {TaskId} documentation targeted file document {DocumentId} — skipped; "
                    + "only markdown documents are maintained", taskId, existing.Id);
                continue;
            }

            if (existing is not null)
            {
                // Nothing really changed — don't burn a version (mirrors UpdateDocumentEndpoint).
                if (existing.Content == content && existing.Title == title && existing.Path == path)
                {
                    results.Add(new CreatedChatDocument(
                        existing.Id, existing.Title, existing.CurrentVersion,
                        ChatDocumentOutcome.Unchanged));
                    continue;
                }

                existing.Title = title;
                existing.Path = path;
                existing.Content = content;
                existing.CurrentVersion++;
                db.DocumentRevisions.Add(DocumentSnapshots.FromCurrentState(
                    existing, changedByUserId: null, changedByAgentId: agentId,
                    requestedByUserId: requesterUserId,
                    sourceTaskId: taskId));
                results.Add(new CreatedChatDocument(
                    existing.Id, existing.Title, existing.CurrentVersion, ChatDocumentOutcome.Updated));
                continue;
            }

            // A create whose title+path already exists becomes a revision of that document —
            // two tasks documenting the same area must not produce two rival documents.
            var collision = db.Documents.Local.FirstOrDefault(
                    d => d.OrgId == orgId && d.ProjectId == projectId && d.Title == title && d.Path == path)
                ?? await db.Documents.FirstOrDefaultAsync(
                    d => d.OrgId == orgId && d.ProjectId == projectId && d.Title == title && d.Path == path, ct);

            if (collision is not null)
            {
                if (collision.FileBlobName is not null || collision.Content == content)
                {
                    results.Add(new CreatedChatDocument(
                        collision.Id, collision.Title, collision.CurrentVersion,
                        ChatDocumentOutcome.Unchanged));
                    continue;
                }

                collision.Content = content;
                collision.CurrentVersion++;
                db.DocumentRevisions.Add(DocumentSnapshots.FromCurrentState(
                    collision, changedByUserId: null, changedByAgentId: agentId,
                    requestedByUserId: requesterUserId,
                    sourceTaskId: taskId));
                results.Add(new CreatedChatDocument(
                    collision.Id, collision.Title, collision.CurrentVersion, ChatDocumentOutcome.Updated));
                continue;
            }

            var entity = new DocumentEntity
            {
                Id = Ulid.NewUlid(),
                OrgId = orgId,
                ProjectId = projectId,
                Title = title,
                Path = path,
                Content = content,
                Source = DocumentSource.Task,
                SourceAgentId = agentId,
                SourceTaskId = taskId,
            };
            db.Documents.Add(entity);
            db.DocumentRevisions.Add(DocumentSnapshots.FromCurrentState(
                entity, changedByUserId: null, changedByAgentId: agentId,
                requestedByUserId: requesterUserId,
                sourceTaskId: taskId));
            results.Add(new CreatedChatDocument(entity.Id, entity.Title));
        }

        if (results.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Task {TaskId} documentation sync wrote {Count} document(s) "
                + "({Created} created, {Updated} updated, {Unchanged} unchanged) for project {ProjectId}",
                taskId, results.Count,
                results.Count(r => r.Outcome == ChatDocumentOutcome.Created),
                results.Count(r => r.Outcome == ChatDocumentOutcome.Updated),
                results.Count(r => r.Outcome == ChatDocumentOutcome.Unchanged),
                projectId);
        }

        return results;
    }

    public async Task<IReadOnlyList<TaskDocumentLink>> GetTaskDocumentLinksAsync(
        Ulid projectId, Ulid taskId, CancellationToken ct = default)
    {
        // Same caveat as GetChatCreatedDocumentsAsync: no Contains over the nullable Ulid?
        // column, so equality (not Contains) is used and the
        // union happens in memory — a single task touches a handful of documents at most.
        var created = await db.Documents.AsNoTracking()
            .Where(d => d.ProjectId == projectId && d.SourceTaskId == taskId)
            .Select(d => new TaskDocumentLink(
                d.Id, d.Title, d.Path, d.CurrentVersion, ChatDocumentOutcome.Created))
            .ToListAsync(ct);

        var revised = await db.DocumentRevisions.AsNoTracking()
            .Where(r => r.Document.ProjectId == projectId && r.SourceTaskId == taskId)
            .OrderByDescending(r => r.Version)
            .Select(r => new TaskDocumentLink(
                r.DocumentId, r.Document.Title, r.Document.Path, r.Version, ChatDocumentOutcome.Updated))
            .ToListAsync(ct);

        // A created document also has a revision row for the same task — creation wins, so the
        // card says "created" rather than reporting the same document twice.
        var createdIds = created.Select(c => c.Id).ToHashSet();

        return [.. created, .. revised
            .Where(r => !createdIds.Contains(r.Id))
            .DistinctBy(r => r.Id)];
    }
}
