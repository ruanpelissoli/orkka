namespace Orkka.Modules.Documents.Features.Documents;

using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Documents.Contracts;
using Orkka.Modules.Documents.Domain;
using Orkka.Modules.Documents.Infrastructure;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;

public sealed class UpdateDocumentEndpoint(
    DocumentsDbContext db,
    IProjectsApi projectsApi,
    IProjectResolver projectResolver,
    ICurrentUser currentUser)
    : Endpoint<UpdateDocumentRequest, DocumentResponse>
{
    public override void Configure()
    {
        Patch("/api/projects/{projectId}/documents/{id:ulid}");
    }

    public override async Task HandleAsync(UpdateDocumentRequest req, CancellationToken ct)
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

        var title = entity.Title;
        if (req.Title is not null)
        {
            title = DocumentRules.NormalizeTitle(req.Title);
            if (title is null)
            {
                ThrowError("Title cannot be empty.");
                return;
            }
        }

        var path = req.Path is not null ? DocumentRules.NormalizePath(req.Path) : entity.Path;

        // A file document can be renamed and moved, but its body is the uploaded file — there is
        // nothing to edit, and accepting markdown here would leave the row disagreeing with the blob.
        if (req.Content is not null && entity.IsFile)
        {
            ThrowError("A file document's content cannot be edited — replace the file instead.");
            return;
        }

        if (req.Content is not null
            && (req.Content.Length == 0 || req.Content.Length > DocumentRules.MaxContentLength))
        {
            ThrowError($"Content must be non-empty and at most {DocumentRules.MaxContentLength} characters.");
            return;
        }

        var content = req.Content ?? entity.Content;

        // No-op updates shouldn't burn a revision number.
        if (title == entity.Title && path == entity.Path && content == entity.Content)
        {
            await Send.OkAsync(entity.ToResponse(), ct);
            return;
        }

        if ((title != entity.Title || path != entity.Path)
            && await db.Documents.AnyAsync(
                d => d.Id != id && d.OrgId == currentUser.OrgId && d.ProjectId == projectId
                     && d.Path == path && d.Title == title, ct))
        {
            await Send.ResultAsync(Results.Conflict(
                new { error = "A document with this title already exists in this folder." }));
            return;
        }

        entity.Title = title;
        entity.Path = path;
        entity.Content = content;
        entity.CurrentVersion++;
        db.DocumentRevisions.Add(DocumentSnapshots.FromCurrentState(
            entity, currentUser.UserId, changedByAgentId: null));

        await db.SaveChangesAsync(ct);
        await Send.OkAsync(entity.ToResponse(), ct);
    }
}
