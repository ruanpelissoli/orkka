namespace Orkka.Modules.Tasks.Features.Tasks;

using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;
using Orkka.Modules.Projects.Contracts;

using FastEndpoints;

/// <summary>
/// The workspace board: every task of the current workspace across all its projects, in
/// the same lanes and cards as a project board. Every task in Orkka is the local user's own work
/// (the tracker intake only takes tickets assigned to them), so this is "everything assigned to me
/// in this workspace". <c>?projectId=</c> (id or slug) narrows it to one project — 404 when that
/// project is not in the caller's workspace; <c>?includeArchived=true</c> shows archived cards.
/// </summary>
public sealed class GetWorkspaceBoardEndpoint(TasksDbContext db, IProjectsApi projectsApi, ICurrentUser currentUser, IProjectResolver projectResolver)
    : EndpointWithoutRequest<TaskBoardResponse>
{
    public override void Configure()
    {
        Get("/api/board");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var projects = await WorkspaceBoardProjects.ResolveAsync(
            projectsApi, projectResolver, currentUser.OrgId, Query<string?>("projectId", isRequired: false), ct);
        if (projects is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var includeArchived = Query<bool?>("includeArchived", isRequired: false) ?? false;
        await Send.OkAsync(
            await BoardBuilder.BuildAsync(db, projectsApi, currentUser.OrgId, projects, includeArchived, ct),
            ct);
    }
}

/// <summary>Which projects a workspace-board request covers.</summary>
internal static class WorkspaceBoardProjects
{
    /// <summary>
    /// Every project of <paramref name="orgId"/>, or only <paramref name="projectRef"/> (an id or a
    /// slug) when given. Null when that project is not one of the workspace's — the endpoints answer
    /// 404, so a project of another workspace is indistinguishable from none. Two queries whatever
    /// the number of projects.
    /// </summary>
    public static async Task<IReadOnlyCollection<ProjectSummary>?> ResolveAsync(
        IProjectsApi projectsApi, IProjectResolver projectResolver, Ulid orgId, string? projectRef,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(projectRef))
        {
            if (await projectResolver.ResolveAsync(projectRef.Trim(), ct) is not { } projectId)
                return null;
            var project = await projectsApi.GetProjectSummaryAsync(projectId, ct);
            return project is null || project.OrgId != orgId ? null : [project];
        }

        var ids = await projectsApi.ListProjectIdsAsync(orgId, ct);
        var summaries = await projectsApi.GetProjectSummariesAsync(ids, ct);
        // Explicit org check on top of the tenant filter: the board must never show another
        // workspace's project, whatever context the summaries were read in.
        return summaries.Values.Where(p => p.OrgId == orgId).OrderBy(p => p.Name).ToList();
    }
}
