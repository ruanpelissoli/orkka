namespace Orkka.Modules.Documents.Features.Documents;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Documents.Domain;
using Orkka.Modules.Documents.Infrastructure;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;

/// <summary>Restore = append a new revision carrying the old snapshot's state. History is never
/// rewritten; the restored-from version stays intact.</summary>
public sealed class RestoreDocumentRevisionEndpoint(
    DocumentsDbContext db,
    IProjectsApi projectsApi,
    IProjectResolver projectResolver,
    ICurrentUser currentUser)
    : EndpointWithoutRequest<DocumentResponse>
{
    public override void Configure()
    {
        Post("/api/projects/{projectId}/documents/{id:ulid}/revisions/{version:int}/restore");
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
        var version = Route<int>("version");

        var entity = await db.Documents
            .FirstOrDefaultAsync(d => d.Id == id && d.OrgId == currentUser.OrgId && d.ProjectId == projectId, ct);
        if (entity is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var revision = await db.DocumentRevisions
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.DocumentId == id && r.Version == version, ct);
        if (revision is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (revision.Version == entity.CurrentVersion)
        {
            // Restoring the current version is a no-op.
            await Send.OkAsync(entity.ToResponse(), ct);
            return;
        }

        entity.Title = revision.Title;
        entity.Path = revision.Path;
        entity.Content = revision.Content;
        entity.CurrentVersion++;
        db.DocumentRevisions.Add(DocumentSnapshots.FromCurrentState(
            entity, currentUser.UserId, changedByAgentId: null));

        await db.SaveChangesAsync(ct);
        await Send.OkAsync(entity.ToResponse(), ct);
    }
}
