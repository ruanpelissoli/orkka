namespace Orkka.Modules.Documents.Features.Documents;

using FastEndpoints;
using Orkka.Modules.Documents.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;

/// <summary>Documents a finished task's documentation pass created or revised. Read-only and
/// open to every org member, like the other document GETs — the write permission that produced
/// these rows was checked when the pass ran.</summary>
public sealed class GetTaskDocumentsEndpoint(
    IDocumentsApi documentsApi,
    IProjectsApi projectsApi,
    IProjectResolver projectResolver,
    ICurrentUser currentUser)
    : EndpointWithoutRequest<List<TaskDocumentResponse>>
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/documents/by-task/{taskId:ulid}");
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

        var taskId = Ulid.Parse(Route<string>("taskId"));
        var links = await documentsApi.GetTaskDocumentLinksAsync(projectId.Value, taskId, ct);

        await Send.OkAsync(
            [.. links.Select(l => new TaskDocumentResponse(
                l.Id, l.Title, l.Path, l.Version, l.Outcome.ToString()))],
            ct);
    }
}
