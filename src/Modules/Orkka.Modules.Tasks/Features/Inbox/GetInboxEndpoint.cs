namespace Orkka.Modules.Tasks.Features.Inbox;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Features.Tasks;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// The Attention Inbox: every item across every project that is parked waiting on a human,
/// ranked so the top of the list is genuinely the next thing to do.
/// </summary>
public sealed class GetInboxEndpoint(
    TasksDbContext db,
    IProjectsApi projectsApi,
    ICurrentUser currentUser)
    : Endpoint<InboxFilterRequest, InboxResponse>
{
    // Materialized for EF: an array translates to `= ANY(...)`, a HashSet does not.
    private static readonly TaskStatus[] ActionableStatuses = [.. AttentionStatuses.Actionable];

    public override void Configure()
    {
        Get("/api/inbox");
    }

    public override async Task HandleAsync(InboxFilterRequest req, CancellationToken ct)
    {
        var query = db.Tasks
            .AsNoTracking()
            .Where(t => t.OrgId == currentUser.OrgId && !t.IsArchived)
            // Merge trouble is not a Status — a conflicted or rejected merge leaves the task
            // sitting in Done while its branch is stuck, so it needs its own predicate.
            .Where(t => ActionableStatuses.Contains(t.Status)
                || t.MergeFailureReason != null
                || (t.MergeConflictsJson != null && t.MergeResolutionStrategy == null));

        if (req.ProjectId is { } projectId)
            query = query.Where(t => t.ProjectId == projectId);

        if (req.Scope.Equals("mine", StringComparison.OrdinalIgnoreCase))
            query = query.Where(t => t.CreatedByUserId == currentUser.UserId);

        // Projected, not materialized: a TaskEntity drags Description (10k chars) plus four
        // JSON columns per row, and this query is unbounded across the whole org. The two
        // merge predicates collapse to booleans in SQL so those columns never leave the database.
        var rows = await query
            .Select(t => new InboxTaskRow(
                t.Id,
                t.ProjectId,
                t.RepoId,
                t.Number,
                t.Title,
                t.CurrentLane,
                t.Status,
                t.Priority,
                t.PrUrl,
                t.CreatedByUserId,
                t.UpdatedAt,
                t.MergeConflictsJson != null && t.MergeResolutionStrategy == null,
                t.MergeFailureReason != null))
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            await Send.OkAsync(new InboxResponse([], [], 0), ct);
            return;
        }

        var taskIds = rows.Select(t => t.Id).ToList();

        // Cost of delay: how many not-yet-finished tasks are waiting on each of these.
        var blockedDependents = (await db.TaskDependencies
                .AsNoTracking()
                .Where(d => taskIds.Contains(d.DependsOnTaskId))
                .Where(d => d.Task.Status != TaskStatus.Completed && !d.Task.IsArchived)
                .Select(d => d.DependsOnTaskId)
                .ToListAsync(ct))
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());

        var projectById = await projectsApi.GetProjectSummariesAsync(
            rows.Select(t => t.ProjectId).Distinct().ToList(), ct);

        // Score everything first: Total and the per-kind counts are defined over the whole
        // filtered set, and the ordering key is computed here rather than in SQL, so the
        // limit cannot move into the query. What it can do is bound the enrichment below.
        var now = DateTimeOffset.UtcNow;
        var scored = new List<ScoredTask>(rows.Count);

        foreach (var t in rows)
        {
            // A project deleted mid-flight leaves orphan tasks; skip rather than 500 the inbox.
            if (!projectById.TryGetValue(t.ProjectId, out var project))
                continue;

            var kind = InboxRanking.ClassifyKind(t.HasUnresolvedMergeConflict, t.HasMergeFailure, t.Status);
            if (req.Kind is { Length: > 0 } wanted && !kind.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                continue;

            var dependents = blockedDependents.GetValueOrDefault(t.Id);
            // UpdatedAt is stamped by the save that parked the task, so it is the moment the
            // human became the blocker — not when the task was first created.
            var waitingSince = t.UpdatedAt;

            scored.Add(new ScoredTask(
                t, project, kind, dependents, waitingSince,
                InboxRanking.Score(kind, t.Priority, waitingSince, dependents, now)));
        }

        var page = scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.WaitingSince)
            .Take(Math.Clamp(req.Limit, 1, 500))
            .ToList();

        // Previews and repo names are display-only, so they are fetched for the page rather
        // than for every actionable task in the org — the default page is 100 of them.
        var lastAgentMessages = await BoardQueries.GetLastAgentMessagesAsync(
            db, page.Select(s => s.Task.Id).ToList(), ct);

        var repoById = page.Any(s => s.Task.RepoId is not null)
            ? await projectsApi.ListReposForProjectsAsync(
                page.Where(s => s.Task.RepoId is not null).Select(s => s.Task.ProjectId).Distinct().ToList(), ct)
            : new Dictionary<Ulid, RepoSummary>();

        var items = page.ConvertAll(s =>
        {
            var t = s.Task;
            var lastMessage = lastAgentMessages.GetValueOrDefault(t.Id);

            return new InboxItemResponse(
                TaskId: t.Id,
                ProjectId: t.ProjectId,
                ProjectName: s.Project.Name,
                ProjectKey: s.Project.Key,
                Code: $"{s.Project.Key}-{t.Number}",
                Number: t.Number,
                Title: t.Title,
                Kind: s.Kind,
                Lane: t.CurrentLane.ToString(),
                Status: t.Status.ToString(),
                Priority: t.Priority.ToString(),
                RepoName: t.RepoId is { } rid ? repoById.GetValueOrDefault(rid)?.Name : null,
                PrUrl: t.PrUrl,
                CreatedByUserId: t.CreatedByUserId,
                AgentName: lastMessage?.AgentName,
                Preview: lastMessage?.Preview,
                WaitingSince: s.WaitingSince,
                BlockedDependents: s.BlockedDependents,
                Score: s.Score);
        });

        var counts = scored
            .GroupBy(s => s.Kind)
            .Select(g => new InboxCountResponse(g.Key, g.Count()))
            .OrderByDescending(c => c.Count)
            .ToList();

        await Send.OkAsync(new InboxResponse(items, counts, scored.Count), ct);
    }

    /// <summary>Only the columns the inbox actually renders or ranks on.</summary>
    private sealed record InboxTaskRow(
        Ulid Id,
        Ulid ProjectId,
        Ulid? RepoId,
        int Number,
        string Title,
        Lane CurrentLane,
        TaskStatus Status,
        Priority Priority,
        string? PrUrl,
        string? CreatedByUserId,
        DateTimeOffset UpdatedAt,
        bool HasUnresolvedMergeConflict,
        bool HasMergeFailure);

    private sealed record ScoredTask(
        InboxTaskRow Task,
        ProjectSummary Project,
        string Kind,
        int BlockedDependents,
        DateTimeOffset WaitingSince,
        int Score);
}
