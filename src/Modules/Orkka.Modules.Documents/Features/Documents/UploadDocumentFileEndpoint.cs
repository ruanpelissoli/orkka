namespace Orkka.Modules.Documents.Features.Documents;

using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Documents.Contracts;
using Orkka.Modules.Documents.Domain;
using Orkka.Modules.Documents.Infrastructure;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.SharedKernel.Storage;

public sealed class UploadDocumentFileRequest
{
    public IFormFile File { get; set; } = null!;

    /// <summary>Target folder, "" = root. Same normalization as a markdown document's path.</summary>
    public string? Path { get; set; }
}

/// <summary>
/// Stores an uploaded PDF as the document itself. Nothing is converted: the bytes land in blob
/// storage untouched and the row carries an empty markdown body, which is what tells every
/// reader (viewer, exporter, agent prompt) to go to the file instead.
/// </summary>
public sealed class UploadDocumentFileEndpoint(
    DocumentsDbContext db,
    IProjectsApi projectsApi,
    IProjectResolver projectResolver,
    ICurrentUser currentUser,
    IBlobStorageService blobStorage)
    : Endpoint<UploadDocumentFileRequest, DocumentResponse>
{
    /// <summary>"%PDF-" — a file that lies about its extension is rejected before it is stored,
    /// so the viewer never gets handed something it cannot render.</summary>
    private static readonly byte[] PdfMagic = "%PDF-"u8.ToArray();

    public override void Configure()
    {
        Post("/api/projects/{projectId}/documents/upload");
        AllowFileUploads();
    }

    public override async Task HandleAsync(UploadDocumentFileRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.DocumentsManage))
            return;

        var projectId = await ProjectAccess.ResolveOwnedProjectAsync(
            projectResolver, projectsApi, currentUser, Route<string>("projectId")!, ct);
        if (projectId is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var file = req.File;
        if (file is null || file.Length == 0)
        {
            ThrowError("No file was uploaded.", 400);
            return;
        }

        if (!DocumentRules.IsPdfFileName(file.FileName))
        {
            ThrowError("Only PDF files can be uploaded as documents.", 400);
            return;
        }

        if (file.Length > DocumentRules.MaxFileSizeBytes)
        {
            ThrowError(
                $"The file exceeds the {DocumentRules.MaxFileSizeBytes / (1024 * 1024)} MB document limit.", 400);
            return;
        }

        if (!await LooksLikePdfAsync(file, ct))
        {
            ThrowError("That file is not a PDF.", 400);
            return;
        }

        var title = DocumentFileStorage.TitleFromFileName(file.FileName);
        if (title is null)
        {
            ThrowError("The file name does not produce a usable document title.", 400);
            return;
        }

        var path = DocumentRules.NormalizePath(req.Path);

        var duplicate = await db.Documents.AnyAsync(
            d => d.OrgId == currentUser.OrgId && d.ProjectId == projectId
                 && d.Path == path && d.Title == title, ct);
        if (duplicate)
        {
            await Send.ResultAsync(Results.Conflict(
                new { error = "A document with this title already exists in this folder." }));
            return;
        }

        var documentId = Ulid.NewUlid();
        var blobName = DocumentFileStorage.BlobName(currentUser.OrgId, projectId.Value, documentId, ".pdf");

        await using (var upload = file.OpenReadStream())
        {
            await blobStorage.UploadAsync(
                DocumentFileStorage.ContainerName, blobName, upload, DocumentRules.PdfContentType, ct);
        }

        var entity = new DocumentEntity
        {
            Id = documentId,
            OrgId = currentUser.OrgId,
            ProjectId = projectId.Value,
            Title = title,
            Path = path,
            Content = "",
            FileBlobName = blobName,
            FileContentType = DocumentRules.PdfContentType,
            FileName = System.IO.Path.GetFileName(file.FileName),
            FileSizeBytes = file.Length,
            Source = DocumentSource.Manual,
            CreatedByUserId = currentUser.UserId,
        };
        db.Documents.Add(entity);

        // File documents still get revisions: the body never changes, but renames and folder
        // moves do, and restoring one of those is as meaningful as for any other document.
        db.DocumentRevisions.Add(DocumentSnapshots.FromCurrentState(
            entity, currentUser.UserId, changedByAgentId: null));

        await db.SaveChangesAsync(ct);
        await Send.OkAsync(entity.ToResponse(), ct);
    }

    /// <summary>Reads the header from its own stream so the upload can start from byte zero
    /// without depending on the form stream being seekable.</summary>
    private static async Task<bool> LooksLikePdfAsync(IFormFile file, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        var header = new byte[PdfMagic.Length];
        var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);

        return read == header.Length && header.AsSpan().SequenceEqual(PdfMagic);
    }
}
