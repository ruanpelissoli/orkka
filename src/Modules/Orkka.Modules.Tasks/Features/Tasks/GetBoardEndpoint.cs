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

public sealed class GetBoardEndpoint(TasksDbContext db, Orkka.Modules.Projects.Contracts.IProjectsApi projectsApi, ICurrentUser currentUser, IProjectResolver projectResolver)
    : EndpointWithoutRequest<TaskBoardResponse>
{
    // The Testing lane only exists on the board when the project tests — automated QA
    // or manual verification (both park/run tasks in that lane).
    private static List<(string Key, string Label, int Order)> BuildLanes(bool qaEnabled)
    {
        List<(string Key, string Label, int Order)> lanes =
        [
            ("drafting", "Drafting", 0),
            ("ready", "Ready", 1),
            ("planning", "Planning", 2),
            ("plan_ready", "Plan Ready", 3),
            ("pending_questions", "Pending Questions", 4),
            ("developing", "Developing", 5)
        ];
        if (qaEnabled)
            lanes.Add(("testing", "Testing", lanes.Count));
        lanes.Add(("code_review", "Code Review", lanes.Count));
        lanes.Add(("done", "Done", lanes.Count));
        return lanes;
    }

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

        // Both of these are cached inside IProjectsApi now (org-keyed, same 30s window this
        // endpoint used to apply itself), so the bespoke board:meta entry that used to live
        // here is gone — one cache instead of two, shared with every other endpoint that
        // reads project metadata rather than benefiting the board alone.
        var projectSummary = await projectsApi.GetProjectSummaryAsync(projectId, ct);
        if (projectSummary is null || projectSummary.OrgId != currentUser.OrgId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var repoById = (await projectsApi.ListReposAsync(projectId, ct)).ToDictionary(r => r.Id);

        var includeArchived = IncludeArchived;
        var tasks = await db.Tasks
            .AsNoTracking()
            .Where(t => t.OrgId == currentUser.OrgId && t.ProjectId == projectId)
            .Where(t => includeArchived || !t.IsArchived)
            .OrderByDescending(TaskOrderingQueries.PriorityRank)
            .ThenBy(t => t.QueuePosition)
            .ThenBy(t => t.CreatedAt)
            .SelectBoardRows()
            .ToListAsync(ct);

        // One grouped query for every card's unmet blockers — the alternative (Include on the
        // task query) would drag full dependency entities in per task.
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
                g => g.Select(d => new BlockingTaskRef(d.DependsOnTaskId, $"{projectSummary.Key}-{d.Number}", d.Status.ToString(), d.IsMerged)).ToList());

        var lastAgentMessageByTaskId = await BoardQueries.GetLastAgentMessagesAsync(db, taskIds, ct);

        var tasksByLane = tasks
            .GroupBy(t => t.CurrentLane.ToString())
            .ToDictionary(g => g.Key, g => g.Select(t => t.ToSummary(
                projectSummary.Key,
                t.RepoId is { } rid ? repoById.GetValueOrDefault(rid) : null,
                blockedByTaskId.GetValueOrDefault(t.Id),
                lastAgentMessageByTaskId.GetValueOrDefault(t.Id))).ToList());

        var lanes = BuildLanes(projectSummary.Settings?.QaEnabled == true
                || projectSummary.Settings?.ManualVerificationEnabled == true)
            .Select(l =>
            {
                var enumName = LaneKeys.ToEnumName(l.Key);
                tasksByLane.TryGetValue(enumName, out var laneTasks);
                return new BoardLaneResponse(l.Key, l.Label, l.Order, laneTasks ?? []);
            })
            .ToList();

        await Send.OkAsync(new TaskBoardResponse(lanes), ct);
    }
}
