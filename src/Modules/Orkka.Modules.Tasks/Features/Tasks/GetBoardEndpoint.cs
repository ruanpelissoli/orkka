namespace Orkka.Modules.Tasks.Features.Tasks;

using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;
using Orkka.Modules.Projects.Contracts;

using FastEndpoints;

/// <summary>
/// One project's board. The workspace board (<see cref="GetWorkspaceBoardEndpoint"/>) is the same
/// board over every project of the workspace; both are built by <see cref="BoardBuilder"/>.
/// </summary>
public sealed class GetBoardEndpoint(TasksDbContext db, IProjectsApi projectsApi, ICurrentUser currentUser, IProjectResolver projectResolver)
    : EndpointWithoutRequest<TaskBoardResponse>
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/board");
    }

    /// <summary>
    /// Archived cards are hidden by default and shown with <c>?includeArchived=true</c> — the
    /// board's half of "archiving hides a task, it does not unmake it". Same parameter name and
    /// default as the board search endpoint, so the two filters read alike.
    /// </summary>
    private bool IncludeArchived => Query<bool?>("includeArchived", isRequired: false) ?? false;

    public override async Task HandleAsync(CancellationToken ct)
    {
        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Cached inside IProjectsApi (org-keyed, 30s window), shared with every other endpoint
        // that reads project metadata.
        var projectSummary = await projectsApi.GetProjectSummaryAsync(projectId, ct);
        if (projectSummary is null || projectSummary.OrgId != currentUser.OrgId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(
            await BoardBuilder.BuildAsync(db, projectsApi, currentUser.OrgId, [projectSummary], IncludeArchived, ct),
            ct);
    }
}
