namespace Orkka.Modules.Documents.Features.Documents;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Documents.Infrastructure;
using Orkka.Modules.Documents.Infrastructure.Services;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;

public sealed class ExportDocumentDocxEndpoint(
    DocumentsDbContext db,
    DocxExportService docxExport,
    IProjectsApi projectsApi,
    IProjectResolver projectResolver,
    ICurrentUser currentUser)
    : EndpointWithoutRequest
{
    private const string DocxContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public override void Configure()
    {
        Get("/api/projects/{projectId}/documents/{id:ulid}/docx");
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

        // A file document has no markdown to convert — rendering its empty body would hand back
        // a blank Word file. The UI offers "download" for these instead of the export menu.
        if (entity.IsFile)
        {
            ThrowError("A file document cannot be exported to Word — download the original file.", 400);
            return;
        }

        var docx = await docxExport.RenderAsync(entity.Title, entity.Content, ct);

        // Strip characters that would break the Content-Disposition header.
        var safeName = new string(entity.Title.Where(c => c is not ('"' or ';')).ToArray());
        await Send.BytesAsync(docx, fileName: $"{safeName}.docx", contentType: DocxContentType, cancellation: ct);
    }
}
