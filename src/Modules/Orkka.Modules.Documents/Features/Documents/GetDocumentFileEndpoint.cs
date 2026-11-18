namespace Orkka.Modules.Documents.Features.Documents;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Documents.Contracts;
using Orkka.Modules.Documents.Infrastructure;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.SharedKernel.Storage;

/// <summary>
/// Streams a file document's stored bytes through the API rather than handing out a storage URL.
/// The viewer fetches this with the caller's bearer token and renders the response — which keeps
/// the same org/project check as every other document read, and works identically against Azure
/// Blob and the local filesystem store.
/// </summary>
public sealed class GetDocumentFileEndpoint(
    DocumentsDbContext db,
    IProjectsApi projectsApi,
    IProjectResolver projectResolver,
    ICurrentUser currentUser,
    IBlobStorageService blobStorage)
    : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/documents/{id:ulid}/file");
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

        // A markdown document has no file to serve — 404 rather than an empty body, so the
        // client never mistakes "wrong kind" for "empty PDF".
        if (entity?.FileBlobName is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var stream = await blobStorage.DownloadAsync(DocumentFileStorage.ContainerName, entity.FileBlobName, ct);
        if (stream is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await using (stream)
        {
            // Inline: the browser's PDF viewer renders it in place. The client turns the
            // response into an object URL, so no Content-Disposition file name is needed.
            await Send.StreamAsync(
                stream,
                fileName: null,
                fileLengthBytes: entity.FileSizeBytes,
                contentType: entity.FileContentType ?? DocumentRules.PdfContentType,
                cancellation: ct);
        }
    }
}
