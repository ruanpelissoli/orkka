namespace Orkka.Modules.Documents.Features.Documents;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Documents.Contracts;
using Orkka.Modules.Documents.Infrastructure;
using Orkka.Modules.Documents.Infrastructure.Services;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.SharedKernel.Storage;

public sealed class ExportDocumentPdfEndpoint(
    DocumentsDbContext db,
    PdfExportService pdfExport,
    IProjectsApi projectsApi,
    IProjectResolver projectResolver,
    ICurrentUser currentUser,
    IBlobStorageService blobStorage)
    : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/documents/{id:ulid}/pdf");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var projectId = await ProjectAccess.ResolveOwnedProjectAsync(
            projectResolver, projectsApi, currentUser, Route<string>("projectId")!, ct);
        if (projectId is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var id = Ulid.Parse(Route<string>("id"));
        var entity = await db.Documents
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id && d.OrgId == currentUser.OrgId && d.ProjectId == projectId, ct);

        if (entity is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Strip characters that would break the Content-Disposition header.
        var safeName = new string(entity.Title.Where(c => c is not ('"' or ';')).ToArray());

        // An uploaded PDF is already the export — hand back the stored bytes rather than
        // re-rendering an empty markdown body into a blank document.
        if (entity.FileBlobName is { } blobName)
        {
            var stored = await blobStorage.DownloadAsync(DocumentFileStorage.ContainerName, blobName, ct);
            if (stored is null)
            {
                await Send.NotFoundAsync(ct);
                return;
            }

            await using (stored)
            {
                await Send.StreamAsync(
                    stored,
                    fileName: $"{safeName}.pdf",
                    fileLengthBytes: entity.FileSizeBytes,
                    contentType: DocumentRules.PdfContentType,
                    cancellation: ct);
            }
            return;
        }

        var pdf = pdfExport.Render(entity.Title, entity.Content);
        await Send.BytesAsync(pdf, fileName: $"{safeName}.pdf", contentType: DocumentRules.PdfContentType, cancellation: ct);
    }
}
