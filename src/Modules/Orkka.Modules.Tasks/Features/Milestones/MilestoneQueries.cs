namespace Orkka.Modules.Tasks.Features.Milestones;

using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

internal static class MilestoneQueries
{
    // Materialized for EF: an array translates to `= ANY(...)`, a HashSet does not.
    private static readonly TaskStatus[] AttentionStatusValues = [.. AttentionStatuses.Actionable];

    /// <summary>Every milestone's counts plus the backlog's, from one pass over the project.</summary>
    internal sealed record ProjectCounts(
        Dictionary<Ulid, MilestoneCountsResponse> ByMilestone,
        MilestoneCountsResponse Backlog)
    {
        public MilestoneCountsResponse For(Ulid milestoneId) =>
            ByMilestone.GetValueOrDefault(milestoneId, EmptyCounts);
    }

    /// <summary>
    /// Counts every task in the project, grouped by milestone, in one pass. Tasks with no
    /// milestone land in <see cref="ProjectCounts.Backlog"/>.
    ///
    /// Archived tasks ARE counted, deliberately. Archiving hides a card from the board and the
    /// task list; it does not un-finish the work. Excluding them here meant a milestone whose
    /// tasks were all archived — which is what auto-archive does to every milestone eventually —
    /// counted 0 of 0 and reported itself as Planned, having actually shipped.
    ///
    /// Projected in SQL rather than materialized: a project's task rows carry a 10k-char
    /// description plus four JSON columns each, and none of that is needed to count them.
    /// </summary>
    public static async Task<ProjectCounts> GetCountsByMilestoneAsync(
        TasksDbContext db, Ulid orgId, Ulid projectId, CancellationToken ct)
    {
        var rows = await db.Tasks
            .AsNoTracking()
            .Where(t => t.OrgId == orgId && t.ProjectId == projectId)
            .GroupBy(t => t.MilestoneId)
            .Select(g => new
            {
                MilestoneId = g.Key,
                Total = g.Count(),
                Done = g.Count(t => t.CurrentLane == Lane.Done),
                NeedsAttention = g.Count(t => AttentionStatusValues.Contains(t.Status)),
                // Drafts are excluded from the attention count already (Draft is not an
                // actionable status), so these four buckets never overlap.
                NotStarted = g.Count(t => t.CurrentLane == Lane.Drafting
                    && !AttentionStatusValues.Contains(t.Status)),
            })
            .ToListAsync(ct);

        // In flight is the remainder: neither finished, nor waiting on a human, nor an
        // unsubmitted draft. Folding drafts in here reported six untouched tasks as "in
        // flight" while the same card's badge correctly read "Planned".
        static MilestoneCountsResponse Counts(int total, int done, int needsAttention, int notStarted) =>
            new(total, done, total - done - needsAttention - notStarted, needsAttention, notStarted);

        return new ProjectCounts(
            rows.Where(r => r.MilestoneId is not null)
                .ToDictionary(
                    r => r.MilestoneId!.Value,
                    r => Counts(r.Total, r.Done, r.NeedsAttention, r.NotStarted)),
            rows.Where(r => r.MilestoneId is null)
                .Select(r => Counts(r.Total, r.Done, r.NeedsAttention, r.NotStarted))
                .FirstOrDefault() ?? EmptyCounts);
    }

    public static readonly MilestoneCountsResponse EmptyCounts = new(0, 0, 0, 0, 0);

    /// <param name="repoNames">Repo id → name, so a multi-repo roadmap can label its cards
    /// without the client joining anything. Omit on single-repo projects, where the label
    /// would be the same on every card and therefore noise.</param>
    public static MilestoneResponse ToResponse(
        this Milestone m,
        MilestoneCountsResponse counts,
        IReadOnlyDictionary<Ulid, string>? repoNames = null) =>
        new(
            m.Id,
            m.ProjectId,
            m.Title,
            m.Outcome,
            m.Status.ToString(),
            m.Position,
            m.StartedAt,
            m.CompletedAt,
            counts,
            m.CreatedAt,
            m.RepoId,
            m.RepoId is { } repoId && repoNames is not null
                && repoNames.TryGetValue(repoId, out var name) ? name : null,
            m.BranchName,
            m.ShipState.ToString(),
            m.ShipFailureReason,
            m.ShippedAt,
            m.IntegrationMode.ToString());

    /// <summary>Next free position, so a new milestone lands at the end of the roadmap.</summary>
    public static async Task<int> NextPositionAsync(
        TasksDbContext db, Ulid orgId, Ulid projectId, CancellationToken ct) =>
        (await db.Milestones
            .Where(m => m.OrgId == orgId && m.ProjectId == projectId)
            .MaxAsync(m => (int?)m.Position, ct) ?? 0) + 1;
}
