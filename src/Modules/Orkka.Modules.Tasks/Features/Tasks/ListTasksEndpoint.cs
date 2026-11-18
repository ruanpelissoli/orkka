namespace Orkka.Modules.Tasks.Features.Tasks;

using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.Modules.Agents.Contracts;
using Orkka.SharedKernel.Storage;
using Orkka.SharedKernel.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Orkka.Modules.Tasks.Features.Tasks;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

public sealed class ListTasksEndpoint(TasksDbContext db, Orkka.Modules.Projects.Contracts.IProjectsApi projectsApi, ICurrentUser currentUser, IProjectResolver projectResolver)
    : Endpoint<TaskFilterRequest, List<TaskResponse>>
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/tasks");
    }

    public override async Task HandleAsync(TaskFilterRequest req, CancellationToken ct)
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

        var repoById = (await projectsApi.ListReposAsync(projectId, ct)).ToDictionary(r => r.Id);

        var query = db.Tasks
            .AsNoTracking()
            .Where(t => t.OrgId == currentUser.OrgId && t.ProjectId == projectId);

        // Filter by archived status (default: show non-archived)
        query = req.IsArchived switch
        {
            true => query.Where(t => t.IsArchived),
            _ => query.Where(t => !t.IsArchived)
        };

        if (req.RepoId is not null)
            query = query.Where(t => t.RepoId == req.RepoId);

        if (req.Status is not null && Enum.TryParse<TaskStatus>(req.Status, true, out var status))
            query = query.Where(t => t.Status == status);

        if (!string.IsNullOrWhiteSpace(req.Search))
        {
            var pattern = LikeSearch.ContainsPattern(req.Search);
            query = query.Where(t => EF.Functions.Like(t.Title, pattern, @"\"));
        }

        var tasks = await query
            .OrderByDescending(TaskOrderingQueries.PriorityRank)
            .ThenByDescending(t => t.CreatedAt)
            .Skip((req.Page - 1) * req.PageSize)
            .Take(req.PageSize)
            .ToListAsync(ct);

        await Send.OkAsync(tasks.Select(t => t.ToResponse(project.Key, t.RepoId is { } rid ? repoById.GetValueOrDefault(rid) : null)).ToList(), ct);
    }
}
