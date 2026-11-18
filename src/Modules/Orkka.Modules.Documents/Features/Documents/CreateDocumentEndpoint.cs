namespace Orkka.Modules.Documents.Features.Documents;

using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Documents.Contracts;
using Orkka.Modules.Documents.Domain;
using Orkka.Modules.Documents.Infrastructure;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;

public sealed class CreateDocumentEndpoint(
    DocumentsDbContext db,
    IProjectsApi projectsApi,
    IProjectResolver projectResolver,
    ICurrentUser currentUser)
    : Endpoint<CreateDocumentRequest, DocumentResponse>
{
    public override void Configure()
    {
        Post("/api/projects/{projectId}/documents");
    }

    public override async Task HandleAsync(CreateDocumentRequest req, CancellationToken ct)
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

        var title = DocumentRules.NormalizeTitle(req.Title);
        if (title is null)
        {
            ThrowError("Title is required.");
            return;
        }

        if (string.IsNullOrWhiteSpace(req.Content) || req.Content.Length > DocumentRules.MaxContentLength)
        {
            ThrowError($"Content is required and must be at most {DocumentRules.MaxContentLength} characters.");
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

        var entity = new DocumentEntity
        {
            Id = Ulid.NewUlid(),
            OrgId = currentUser.OrgId,
            ProjectId = projectId.Value,
            Title = title,
            Path = path,
            Content = req.Content,
            Source = DocumentSource.Manual,
            CreatedByUserId = currentUser.UserId,
        };
        db.Documents.Add(entity);
        db.DocumentRevisions.Add(DocumentSnapshots.FromCurrentState(
            entity, currentUser.UserId, changedByAgentId: null));

        await db.SaveChangesAsync(ct);
        await Send.OkAsync(entity.ToResponse(), ct);
    }
}
