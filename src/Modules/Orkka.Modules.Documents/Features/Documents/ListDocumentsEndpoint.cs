namespace Orkka.Modules.Documents.Features.Documents;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Documents.Infrastructure;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;

public sealed class ListDocumentsEndpoint(
    DocumentsDbContext db,
    IProjectsApi projectsApi,
    IProjectResolver projectResolver,
    ICurrentUser currentUser)
    : EndpointWithoutRequest<List<DocumentListItemResponse>>
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/documents");
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

        var documents = await db.Documents
            .AsNoTracking()
            .Where(d => d.OrgId == currentUser.OrgId && d.ProjectId == projectId)
            .OrderBy(d => d.Path).ThenBy(d => d.Title)
            .Select(d => new DocumentListItemResponse(
                d.Id, d.Title, d.Path, d.Source.ToString(), d.CurrentVersion, d.CreatedAt, d.UpdatedAt,
                d.FileName, d.FileContentType))
            .ToListAsync(ct);

        await Send.OkAsync(documents, ct);
    }
}
