namespace Orkka.Modules.Documents.Features.Documents;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Documents.Infrastructure;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Security;

public sealed class ListDocumentRevisionsEndpoint(
    DocumentsDbContext db,
    IProjectsApi projectsApi,
    IProjectResolver projectResolver,
    IAgentsApi agentsApi,
    ITasksApi tasksApi,
    IUserDirectory userDirectory,
    ICurrentUser currentUser)
    : EndpointWithoutRequest<List<DocumentRevisionResponse>>
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/documents/{id:ulid}/revisions");
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
        var documentExists = await db.Documents.AsNoTracking()
            .AnyAsync(d => d.Id == id && d.OrgId == currentUser.OrgId && d.ProjectId == projectId, ct);
        if (!documentExists)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Slim projection — revision Content can be large and the list never shows it.
        var revisions = await db.DocumentRevisions
            .AsNoTracking()
            .Where(r => r.DocumentId == id)
            .OrderByDescending(r => r.Version)
            .Select(r => new
            {
                r.Version, r.Title, r.Path, r.ChangedByUserId, r.ChangedByAgentId,
                r.RequestedByUserId, r.SourceConversationId, r.SourceMessageId, r.SourceTaskId,
                r.CreatedAt,
            })
            .ToListAsync(ct);

        var (agentNames, users, taskTitles) = await RevisionActorNames.ResolveAsync(
            agentsApi, tasksApi, userDirectory,
            revisions.Select(r => r.ChangedByAgentId),
            revisions.SelectMany(r => new[] { r.ChangedByUserId, r.RequestedByUserId }),
            revisions.Select(r => r.SourceTaskId),
            ct);

        var response = revisions.Select(r => new DocumentRevisionResponse(
            r.Version, r.Title, r.Path,
            r.ChangedByUserId, users.NameFor(r.ChangedByUserId),
            r.ChangedByAgentId, agentNames.NameFor(r.ChangedByAgentId),
            r.RequestedByUserId, users.NameFor(r.RequestedByUserId),
            r.SourceConversationId, r.SourceMessageId, r.CreatedAt,
            r.SourceTaskId, taskTitles.NameFor(r.SourceTaskId))).ToList();

        await Send.OkAsync(response, ct);
    }
}
