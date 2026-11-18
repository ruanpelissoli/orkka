namespace Orkka.Modules.Documents.Features.Documents;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Documents.Infrastructure;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Security;

public sealed class GetDocumentRevisionEndpoint(
    DocumentsDbContext db,
    IProjectsApi projectsApi,
    IProjectResolver projectResolver,
    IAgentsApi agentsApi,
    ITasksApi tasksApi,
    IUserDirectory userDirectory,
    ICurrentUser currentUser)
    : EndpointWithoutRequest<DocumentRevisionDetailResponse>
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/documents/{id:ulid}/revisions/{version:int}");
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
        var version = Route<int>("version");

        var revision = await db.DocumentRevisions
            .AsNoTracking()
            .Where(r => r.DocumentId == id && r.Version == version
                        && r.OrgId == currentUser.OrgId
                        && r.Document.ProjectId == projectId)
            .FirstOrDefaultAsync(ct);

        if (revision is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var (agentNames, users, taskTitles) = await RevisionActorNames.ResolveAsync(
            agentsApi, tasksApi, userDirectory,
            [revision.ChangedByAgentId],
            [revision.ChangedByUserId, revision.RequestedByUserId],
            [revision.SourceTaskId],
            ct);

        await Send.OkAsync(revision.ToDetailResponse(
            users.NameFor(revision.ChangedByUserId),
            agentNames.NameFor(revision.ChangedByAgentId),
            users.NameFor(revision.RequestedByUserId),
            taskTitles.NameFor(revision.SourceTaskId)), ct);
    }
}
