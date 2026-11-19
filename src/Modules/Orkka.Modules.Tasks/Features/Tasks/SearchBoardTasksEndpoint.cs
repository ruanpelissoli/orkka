namespace Orkka.Modules.Tasks.Features.Tasks;

using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;
using Orkka.Modules.Projects.Contracts;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;

/// <summary>Searches one project's board (title, optionally messages).</summary>
public sealed class SearchBoardTasksEndpoint(TasksDbContext db, IProjectsApi projectsApi, ICurrentUser currentUser, IProjectResolver projectResolver)
    : Endpoint<SearchBoardTasksRequest, List<TaskSummaryResponse>>
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/board/search");
    }

    public override async Task HandleAsync(SearchBoardTasksRequest req, CancellationToken ct)
    {
        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var project = await projectsApi.GetProjectSummaryAsync(projectId, ct);
        if (project is null || project.OrgId != currentUser.OrgId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(await BoardSearch.SearchAsync(db, projectsApi, currentUser.OrgId, [project], req, ct), ct);
    }
}

/// <summary>
/// Searches the workspace board — every project of the current workspace, or <c>?projectId=</c>
/// (404 when not the workspace's). Same parameters and cards as the project board search.
/// </summary>
public sealed class SearchWorkspaceBoardTasksEndpoint(TasksDbContext db, IProjectsApi projectsApi, ICurrentUser currentUser, IProjectResolver projectResolver)
    : Endpoint<SearchWorkspaceBoardTasksRequest, List<TaskSummaryResponse>>
{
    public override void Configure()
    {
        Get("/api/board/search");
    }

    public override async Task HandleAsync(SearchWorkspaceBoardTasksRequest req, CancellationToken ct)
    {
        var projects = await WorkspaceBoardProjects.ResolveAsync(projectsApi, projectResolver, currentUser.OrgId, req.ProjectId, ct);
        if (projects is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(await BoardSearch.SearchAsync(db, projectsApi, currentUser.OrgId, projects,
            new SearchBoardTasksRequest { Q = req.Q, IncludeMessages = req.IncludeMessages, IncludeArchived = req.IncludeArchived }, ct), ct);
    }
}

internal static class BoardSearch
{
    /// <summary>The newest 100 matches (title, or title and messages) over the projects, as board cards.</summary>
    public static async Task<List<TaskSummaryResponse>> SearchAsync(
        TasksDbContext db, IProjectsApi projectsApi, Ulid orgId, IReadOnlyCollection<ProjectSummary> projects,
        SearchBoardTasksRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Q) || projects.Count == 0)
            return [];

        var pattern = LikeSearch.ContainsPattern(req.Q);
        var projectIds = projects.Select(p => p.Id).ToList();

        var query = db.Tasks
            .AsNoTracking()
            .Where(t => t.OrgId == orgId && projectIds.Contains(t.ProjectId));

        if (!req.IncludeArchived)
            query = query.Where(t => !t.IsArchived);

        // 3-arg Like: the backslash escaping applied to the pattern above needs an ESCAPE
        // clause. SQLite LIKE is case-insensitive for ASCII.
        query = req.IncludeMessages
            ? query.Where(t => EF.Functions.Like(t.Title, pattern, @"\")
                || t.Messages.Any(m => EF.Functions.Like(m.Content, pattern, @"\")))
            : query.Where(t => EF.Functions.Like(t.Title, pattern, @"\"));

        var rows = await query
            .OrderByDescending(TaskOrderingQueries.PriorityRank)
            .ThenByDescending(t => t.CreatedAt)
            .Take(100)
            .SelectBoardRows()
            .ToListAsync(ct);

        return await BoardBuilder.ToCardsAsync(db, projectsApi, rows, projects, ct);
    }
}
