namespace Orkka.Modules.Tasks.Features.Tasks;

using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;
using Orkka.Modules.Projects.Contracts;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;

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

        if (string.IsNullOrWhiteSpace(req.Q))
        {
            await Send.OkAsync([], ct);
            return;
        }

        var pattern = LikeSearch.ContainsPattern(req.Q);

        var query = db.Tasks
            .AsNoTracking()
            .Where(t => t.OrgId == currentUser.OrgId && t.ProjectId == projectId);

        if (!req.IncludeArchived)
            query = query.Where(t => !t.IsArchived);

        // 3-arg Like: the backslash escaping applied to the pattern above needs an ESCAPE
        // clause. SQLite LIKE is case-insensitive for ASCII.
        query = req.IncludeMessages
            ? query.Where(t => EF.Functions.Like(t.Title, pattern, @"\")
                || t.Messages.Any(m => EF.Functions.Like(m.Content, pattern, @"\")))
            : query.Where(t => EF.Functions.Like(t.Title, pattern, @"\"));

        var tasks = await query
            .OrderByDescending(TaskOrderingQueries.PriorityRank)
            .ThenByDescending(t => t.CreatedAt)
            .Take(100)
            .SelectBoardRows()
            .ToListAsync(ct);

        var taskIds = tasks.Select(t => t.Id).ToList();
        var blockedByTaskId = (await db.TaskDependencies
                .AsNoTracking()
                .Where(d => taskIds.Contains(d.TaskId))
                .Where(TaskDependencyQueries.IsUnmet)
                .Select(d => new { d.TaskId, d.DependsOnTaskId, d.DependsOnTask.Number, d.DependsOnTask.Status, d.DependsOnTask.IsMerged })
                .ToListAsync(ct))
            .GroupBy(d => d.TaskId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(d => new BlockingTaskRef(d.DependsOnTaskId, $"{project.Key}-{d.Number}", d.Status.ToString(), d.IsMerged)).ToList());

        var repoById = (await projectsApi.ListReposAsync(projectId, ct)).ToDictionary(r => r.Id);
        var lastAgentMessageByTaskId = await BoardQueries.GetLastAgentMessagesAsync(db, taskIds, ct);

        await Send.OkAsync(tasks.Select(t => t.ToSummary(
            project.Key,
            t.RepoId is { } rid ? repoById.GetValueOrDefault(rid) : null,
            blockedByTaskId.GetValueOrDefault(t.Id),
            lastAgentMessageByTaskId.GetValueOrDefault(t.Id))).ToList(), ct);
    }
}
