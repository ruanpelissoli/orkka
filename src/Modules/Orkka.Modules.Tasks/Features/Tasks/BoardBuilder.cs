namespace Orkka.Modules.Tasks.Features.Tasks;

using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Infrastructure;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Builds the board — lanes of cards — over one project (<c>GET /api/projects/{id}/board</c>) or
/// every project of a workspace (<c>GET /api/board</c>), and the cards of the board searches. One
/// implementation so the two boards can never disagree on what a card says: each card names its
/// project (id, key, name, slug), its code and its blockers' codes use their own project's key, and
/// the lookups around the task query are batched (one repo query, one blocker query, one
/// last-message query, one project query for blockers in a project outside the set) whatever the
/// number of projects.
/// </summary>
internal static class BoardBuilder
{
    /// <summary>The Testing lane exists when the board shows a project that tests — automated QA
    /// or manual verification (both park/run tasks in that lane).</summary>
    public static bool HasTestingLane(ProjectSummary project) =>
        project.Settings?.QaEnabled == true || project.Settings?.ManualVerificationEnabled == true;

    private static List<(string Key, string Label, int Order)> BuildLanes(bool testing)
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
        if (testing)
            lanes.Add(("testing", "Testing", lanes.Count));
        lanes.Add(("code_review", "Code Review", lanes.Count));
        lanes.Add(("done", "Done", lanes.Count));
        return lanes;
    }

    /// <summary>
    /// The board over <paramref name="projects"/> (all of one org — the caller checked that).
    /// Archived cards are left out unless <paramref name="includeArchived"/>. Lanes: Testing when
    /// ANY of the projects tests.
    /// </summary>
    public static async Task<TaskBoardResponse> BuildAsync(
        TasksDbContext db, IProjectsApi projectsApi, Ulid orgId,
        IReadOnlyCollection<ProjectSummary> projects, bool includeArchived, CancellationToken ct)
    {
        var projectIds = projects.Select(p => p.Id).ToList();
        var rows = projectIds.Count == 0
            ? []
            : await db.Tasks
                .AsNoTracking()
                .Where(t => t.OrgId == orgId && projectIds.Contains(t.ProjectId))
                .Where(t => includeArchived || !t.IsArchived)
                .OrderByDescending(TaskOrderingQueries.PriorityRank)
                .ThenBy(t => t.QueuePosition)
                .ThenBy(t => t.CreatedAt)
                .SelectBoardRows()
                .ToListAsync(ct);

        var cards = await ToCardsAsync(db, projectsApi, rows, projects, ct);

        var cardsByLane = cards
            .GroupBy(c => c.CurrentLane)
            .ToDictionary(g => g.Key, g => g.ToList());

        var lanes = BuildLanes(projects.Any(HasTestingLane))
            .Select(l =>
            {
                cardsByLane.TryGetValue(LaneKeys.ToEnumName(l.Key), out var laneCards);
                return new BoardLaneResponse(l.Key, l.Label, l.Order, laneCards ?? []);
            })
            .ToList();

        return new TaskBoardResponse(lanes);
    }

    /// <summary>
    /// Cards for board rows, in the rows' order. <paramref name="projects"/> must hold every row's
    /// project; a blocker in a project outside it is looked up (in one query) for its key.
    /// </summary>
    public static async Task<List<TaskSummaryResponse>> ToCardsAsync(
        TasksDbContext db, IProjectsApi projectsApi, IReadOnlyList<BoardTaskRow> rows,
        IReadOnlyCollection<ProjectSummary> projects, CancellationToken ct)
    {
        if (rows.Count == 0)
            return [];

        var projectById = projects.ToDictionary(p => p.Id);
        var taskIds = rows.Select(t => t.Id).ToList();

        // One grouped query for every card's unmet blockers — the alternative (Include on the
        // task query) would drag full dependency entities in per task.
        var blockers = await db.TaskDependencies
            .AsNoTracking()
            .Where(d => taskIds.Contains(d.TaskId))
            .Where(TaskDependencyQueries.IsUnmet)
            .Select(d => new
            {
                d.TaskId,
                d.DependsOnTaskId,
                d.DependsOnTask.ProjectId,
                d.DependsOnTask.Number,
                d.DependsOnTask.Status,
                d.DependsOnTask.IsMerged,
            })
            .ToListAsync(ct);

        var missingProjectIds = blockers
            .Select(b => b.ProjectId)
            .Where(id => !projectById.ContainsKey(id))
            .Distinct()
            .ToList();
        var keyByProjectId = projectById.ToDictionary(kv => kv.Key, kv => kv.Value.Key);
        if (missingProjectIds.Count > 0)
        {
            foreach (var (id, summary) in await projectsApi.GetProjectSummariesAsync(missingProjectIds, ct))
                keyByProjectId[id] = summary.Key;
        }

        var blockedByTaskId = blockers
            .GroupBy(d => d.TaskId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(d => new BlockingTaskRef(
                    d.DependsOnTaskId,
                    $"{keyByProjectId.GetValueOrDefault(d.ProjectId, "?")}-{d.Number}",
                    d.Status.ToString(),
                    d.IsMerged)).ToList());

        var repoById = await projectsApi.ListReposForProjectsAsync(
            rows.Select(r => r.ProjectId).Distinct().ToList(), ct);
        var lastAgentMessageByTaskId = await BoardQueries.GetLastAgentMessagesAsync(db, taskIds, ct);

        return rows
            .Where(r => projectById.ContainsKey(r.ProjectId))
            .Select(r =>
            {
                var project = projectById[r.ProjectId];
                return r.ToSummary(
                    project.Key,
                    r.RepoId is { } rid ? repoById.GetValueOrDefault(rid) : null,
                    blockedByTaskId.GetValueOrDefault(r.Id),
                    lastAgentMessageByTaskId.GetValueOrDefault(r.Id),
                    new BoardProjectRef(project.Id, project.Key, project.Name, project.Slug, HasTestingLane(project)));
            })
            .ToList();
    }
}
