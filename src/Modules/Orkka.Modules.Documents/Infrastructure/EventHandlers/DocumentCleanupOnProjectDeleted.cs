namespace Orkka.Modules.Documents.Infrastructure.EventHandlers;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Documents.Features.Documents;
using Orkka.SharedKernel.Documents;
using Orkka.SharedKernel.Events;
using Orkka.SharedKernel.Events.Catalog;
using Orkka.SharedKernel.Storage;

// Projects live in their own module/schema (no cross-module FKs), so this handler removes the
// document rows when a project is deleted. Revisions cascade from their module-internal FK.
public sealed class DocumentCleanupOnProjectDeleted(
    DocumentsDbContext db,
    IBlobStorageService blobStorage,
    ILogger<DocumentCleanupOnProjectDeleted> logger) : IIntegrationEventHandler<ProjectDeleted>
{
    public async Task HandleAsync(ProjectDeleted evt, CancellationToken ct)
    {
        // Uploaded files are blobs with no row of their own — collect their names before the
        // rows go, or nothing is left to say what to delete.
        var blobNames = await db.Documents
            .Where(d => d.ProjectId == evt.ProjectId && d.FileBlobName != null)
            .Select(d => d.FileBlobName!)
            .ToListAsync(ct);

        // ExecuteDelete bypasses cascade config resolution client-side but the database enforces
        // the FK cascade (foreign_keys is on for every connection), so deleting the parents removes revisions too.
        var documents = await db.Documents
            .Where(d => d.ProjectId == evt.ProjectId)
            .ExecuteDeleteAsync(ct);

        foreach (var blobName in blobNames)
        {
            // Best effort: the rows are already gone, so a storage hiccup must not fail the
            // handler and have the whole cleanup retried.
            try
            {
                await blobStorage.DeleteBlobAsync(DocumentFileStorage.ContainerName, blobName);
                await blobStorage.DeleteBlobAsync(
                    DocumentFileStorage.ContainerName, blobName + PdfTextSidecarService.SidecarSuffix);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not delete document blob {Blob} for project {ProjectId}",
                    blobName, evt.ProjectId);
            }
        }

        logger.LogInformation(
            "Cleaned up document data for deleted project {ProjectId}: {Documents} documents, {Files} files",
            evt.ProjectId, documents, blobNames.Count);
    }
}
