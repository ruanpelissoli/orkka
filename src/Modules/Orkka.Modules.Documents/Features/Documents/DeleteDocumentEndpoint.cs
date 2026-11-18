namespace Orkka.Modules.Documents.Features.Documents;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Documents.Infrastructure;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Documents;
using Orkka.SharedKernel.Security;
using Orkka.SharedKernel.Storage;

public sealed class DeleteDocumentEndpoint(
    DocumentsDbContext db,
    IProjectsApi projectsApi,
    IProjectResolver projectResolver,
    ICurrentUser currentUser,
    IBlobStorageService blobStorage)
    : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("/api/projects/{projectId}/documents/{id:ulid}");
    }

    public override async Task HandleAsync(CancellationToken ct)
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

        var id = Ulid.Parse(Route<string>("id"));
        var entity = await db.Documents
            .FirstOrDefaultAsync(d => d.Id == id && d.OrgId == currentUser.OrgId && d.ProjectId == projectId, ct);

        if (entity is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        db.Documents.Remove(entity);
        await db.SaveChangesAsync(ct);

        // Blobs have no row of their own, so they outlive the document unless dropped here —
        // along with the extracted-text sibling the agent prompt builder creates lazily.
        if (entity.FileBlobName is { } blobName)
        {
            await blobStorage.DeleteBlobAsync(DocumentFileStorage.ContainerName, blobName);
            await blobStorage.DeleteBlobAsync(
                DocumentFileStorage.ContainerName, blobName + PdfTextSidecarService.SidecarSuffix);
        }

        await Send.NoContentAsync(ct);
    }
}
