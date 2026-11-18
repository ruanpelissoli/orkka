namespace Orkka.Modules.Tasks.Features.Executions;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Usage.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;

/// <summary>
/// Every execution in a project inside a time window — the data behind the Activity page
/// ("what did my team do overnight"). Read-only, open to org members.
///
/// Three things come back, and only the first is capped. The executions are the raw runs, page-
/// limited because a busy month has more than anyone can render. The summary counts the whole
/// window regardless, so the headline never shrinks just because the page filled up. The task rows
/// say how each of those runs turned out — the runs describe effort, the tasks describe whether it
/// worked, and a page showing only the former is a chart of nothing anyone can act on.
/// </summary>
public sealed class ListProjectExecutionsEndpoint(
    TasksDbContext db,
    IProjectsApi projectsApi,
    IAgentsApi agentsApi,
    IUsageQuery usageQuery,
    ICurrentUser currentUser,
    IProjectResolver projectResolver)
    : Endpoint<ListProjectExecutionsRequest, ProjectExecutionsResponse>
{
    private const int DefaultLimit = 300;
    private const int MaxLimit = 500;

    /// <summary>Outcome rows returned per window. Tasks are far fewer than runs, so this only
    /// binds on a genuinely enormous window.</summary>
    private const int TaskRowCap = 200;

    public override void Configure()
    {
        Get("/api/projects/{projectId}/executions");
    }

    public override async Task HandleAsync(ListProjectExecutionsRequest req, CancellationToken ct)
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

        var to = req.To ?? DateTimeOffset.UtcNow;
        var from = req.From ?? to.AddHours(-24);
        if (from >= to)
        {
            AddError(r => r.From, "'from' must be earlier than 'to'.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var limit = Math.Clamp(req.Limit ?? DefaultLimit, 1, MaxLimit);

        ExecutionStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(req.Status))
        {
            if (!Enum.TryParse<ExecutionStatus>(req.Status, ignoreCase: true, out var parsed))
            {
                AddError(r => r.Status, "Unknown execution status.");
                await Send.ErrorsAsync(cancellation: ct);
                return;
            }
            statusFilter = parsed;
        }

        // Everything in the response describes this window. Narrowing to one agent is a question
        // about that agent, so it applies throughout; the status filter applies only to the row
        // page, because a status breakdown filtered to one status says nothing.
        var windowQuery = db.TaskExecutions.AsNoTracking()
            .Where(e => e.OrgId == currentUser.OrgId
                        && e.Task.ProjectId == projectId
                        && e.StartedAt != null
                        && e.StartedAt >= from
                        && e.StartedAt < to);

        if (req.AgentId is { } agentId)
            windowQuery = windowQuery.Where(e => e.AgentId == agentId);

        var query = statusFilter is { } status ? windowQuery.Where(e => e.Status == status) : windowQuery;

        // Fetch one extra row so the client can tell "you are seeing everything" from "the
        // window has more than the cap" without a second count query.
        var rows = await query
            .OrderByDescending(e => e.StartedAt)
            .Take(limit + 1)
            .Select(e => new
            {
                e.Id,
                e.TaskId,
                TaskNumber = e.Task.Number,
                TaskTitle = e.Task.Title,
                e.AgentId,
                e.Lane,
                e.Status,
                e.StartedAt,
                e.CompletedAt,
                e.TokensUsed,
                e.ErrorMessage,
            })
            .ToListAsync(ct);

        var truncated = rows.Count > limit;
        if (truncated) rows.RemoveAt(rows.Count - 1);

        var agentIds = rows.Where(r => r.AgentId.HasValue).Select(r => r.AgentId!.Value).Distinct().ToList();
        var agents = agentIds.Count == 0
            ? new Dictionary<Ulid, AgentSummary>()
            : (await agentsApi.GetAgentsAsync(agentIds, ct)).ToDictionary(a => a.Id);

        // Cost lives in the Usage module keyed by ExecutionId (see TaskExecution — no cost column by
        // design). The query filters on (org, project) in SQL and matches ids in memory.
        var executionIds = rows.Select(r => r.Id).ToList();
        var costByExecution = executionIds.Count == 0
            ? new Dictionary<Ulid, decimal>()
            : await usageQuery.GetExecutionCostsAsync(currentUser.OrgId, projectId, executionIds, ct);

        var result = rows.Select(r =>
        {
            var agent = r.AgentId.HasValue ? agents.GetValueOrDefault(r.AgentId.Value) : null;
            var started = r.StartedAt!.Value;
            return new ProjectExecutionRow(
                r.Id,
                r.TaskId,
                $"{project.Key}-{r.TaskNumber}",
                r.TaskTitle,
                r.AgentId,
                agent?.Name,
                agent?.Role.ToString(),
                r.Lane.ToString(),
                r.Status.ToString(),
                started,
                r.CompletedAt,
                r.CompletedAt is { } done ? Math.Round((done - started).TotalSeconds, 1) : null,
                r.TokensUsed,
                costByExecution.GetValueOrDefault(r.Id),
                r.ErrorMessage);
        }).ToList();

        var summary = await BuildSummaryAsync(windowQuery, projectId, req.AgentId, from, to, ct);
        var tasks = await BuildTaskRowsAsync(windowQuery, project.Key, ct);

        await Send.OkAsync(new ProjectExecutionsResponse(from, to, truncated, result, summary, tasks), ct);
    }

    /// <summary>
    /// Counts the whole window, not the page returned beside it. Aggregated in memory over a
    /// five-column projection, the same trade <see cref="AgentStats.GetAgentStatsEndpoint"/> makes:
    /// per-status counts, distinct tasks and summed duration together are several window functions
    /// in SQL and one fold here, over a set the window already bounds.
    /// </summary>
    private async Task<ActivitySummary> BuildSummaryAsync(
        IQueryable<TaskExecution> windowQuery,
        Ulid projectId,
        Ulid? agentFilter,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct)
    {
        var rows = await windowQuery
            .Select(e => new { e.Id, e.TaskId, e.Status, e.StartedAt, e.CompletedAt })
            .ToListAsync(ct);

        if (rows.Count == 0)
            return ActivitySummary.Empty;

        // Failures are the minority of any healthy window, so pricing them by id stays bounded and
        // gives an exact figure — worth it, because "you spent this on nothing" is the number that
        // makes someone change how they run their team.
        var wastedIds = rows
            .Where(r => ExecutionOutcome.IsWasted(r.Status))
            .Select(r => r.Id)
            .ToList();
        var wastedCost = wastedIds.Count == 0
            ? 0m
            : (await usageQuery.GetExecutionCostsAsync(currentUser.OrgId, projectId, wastedIds, ct)).Values.Sum();

        var spend = await usageQuery.GetProjectSpendAsync(currentUser.OrgId, projectId, from, to, ct);

        // GetProjectSpendAsync takes no agent parameter, so narrow its result the same way the
        // counts are narrowed rather than letting the money describe a wider population.
        var agents = (agentFilter is { } only
                ? spend.ByAgent.Where(kv => kv.Key == only)
                : spend.ByAgent)
            .Select(kv => new ActivityAgentSpend(kv.Key, kv.Value.Cost, kv.Value.Tokens))
            .OrderByDescending(a => a.Cost)
            .ThenByDescending(a => a.Tokens)
            .ToList();

        return new ActivitySummary(
            Runs: rows.Count,
            Running: rows.Count(r => r.Status == ExecutionStatus.Running),
            Completed: rows.Count(r => r.Status == ExecutionStatus.Completed),
            Failed: rows.Count(r => r.Status == ExecutionStatus.Failed),
            Cancelled: rows.Count(r => r.Status == ExecutionStatus.Cancelled),
            TasksTouched: rows.Select(r => r.TaskId).Distinct().Count(),
            DurationSeconds: Math.Round(
                rows.Where(r => r.CompletedAt is not null)
                    .Sum(r => (r.CompletedAt!.Value - r.StartedAt!.Value).TotalSeconds),
                1),
            Cost: agentFilter is null ? spend.TotalCost : agents.Sum(a => a.Cost),
            WastedCost: wastedCost,
            Tokens: agentFilter is null ? spend.TotalTokens : agents.Sum(a => a.Tokens),
            Agents: agents);
    }

    /// <summary>
    /// How the tasks touched in this window ended up. Capped like the row page, but ordered by
    /// most recent activity first so the cap drops the stalest tasks rather than arbitrary ones.
    /// </summary>
    private async Task<IReadOnlyList<ActivityTaskRow>> BuildTaskRowsAsync(
        IQueryable<TaskExecution> windowQuery,
        string projectKey,
        CancellationToken ct)
    {
        var lastActivity = await windowQuery
            .GroupBy(e => e.TaskId)
            .Select(g => new { TaskId = g.Key, At = g.Max(e => e.CompletedAt ?? e.StartedAt!.Value) })
            .OrderByDescending(x => x.At)
            .Take(TaskRowCap)
            .ToDictionaryAsync(x => x.TaskId, x => x.At, ct);

        if (lastActivity.Count == 0)
            return [];

        var ids = lastActivity.Keys.ToList();
        var tasks = await db.Tasks.AsNoTracking()
            .Where(t => t.OrgId == currentUser.OrgId && ids.Contains(t.Id))
            .Select(t => new
            {
                t.Id,
                t.Number,
                t.Title,
                t.CurrentLane,
                t.Status,
                t.PrUrl,
                t.IsMerged,
                t.PrClosedAt,
                t.MergeFailureReason,
                t.MergeFailureKind,
                t.SetupFailure,
                t.ReviewCycleCount,
                t.QaCycleCount,
                t.CheckCycleCount,
                t.BaseSyncCycleCount,
                t.HasAgentWorking,
            })
            .ToListAsync(ct);

        return tasks
            .Select(t => new ActivityTaskRow(
                t.Id,
                $"{projectKey}-{t.Number}",
                t.Title,
                t.CurrentLane.ToString(),
                t.Status.ToString(),
                t.PrUrl,
                t.IsMerged,
                t.PrClosedAt,
                t.MergeFailureReason,
                t.MergeFailureKind?.ToString(),
                t.SetupFailure,
                t.ReviewCycleCount,
                t.QaCycleCount,
                t.CheckCycleCount,
                t.BaseSyncCycleCount,
                t.HasAgentWorking,
                lastActivity[t.Id]))
            .OrderByDescending(t => t.LastActivityAt)
            .ToList();
    }
}
