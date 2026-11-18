namespace Orkka.Modules.Usage.Infrastructure.Services;

using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Usage.Contracts;
using Orkka.SharedKernel.Ai;

/// <summary>
/// Every query here filters in SQL on (OrgId, ProjectId) — both non-nullable — groups, and then
/// matches the requested ids in memory.
///
/// That shape is deliberate: <c>TaskId</c> and <c>ExecutionId</c> are nullable, converter-mapped
/// Ulid columns, and a <c>Contains</c> against one is a translation EF has failed before. Grouping
/// before materializing keeps the set to one row per task/execution in the project rather than
/// one per usage record, so the in-memory step stays small.
/// </summary>
internal sealed class UsageQuery(UsageDbContext db, IModelRateProvider modelRates) : IUsageQuery
{
    public async Task<IReadOnlyDictionary<Ulid, decimal>> GetTaskEstimatedSpendAsync(
        Ulid orgId, Ulid projectId, IReadOnlyCollection<Ulid> taskIds, CancellationToken ct = default)
    {
        if (taskIds.Count == 0)
            return EmptyCosts;

        var wanted = taskIds.ToHashSet();

        // One row per (task, model, priced-by-the-vendor?) — the vendor's figure where the CLI
        // reported one, the tokens of the rest to be priced at list. The org is filtered
        // explicitly: the callers are background services (the orchestrator's phase gate, the
        // autopilot) whose scope carries no request user for the tenant filter.
        var rows = await db.UsageRecords
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(u => u.OrgId == orgId && u.ProjectId == projectId && u.TaskId != null)
            .GroupBy(u => new { TaskId = u.TaskId!.Value, u.Model, Vendor = u.VendorReportedCostUsd != null })
            .Select(g => new
            {
                g.Key.TaskId,
                g.Key.Model,
                g.Key.Vendor,
                VendorCost = g.Sum(u => u.VendorReportedCostUsd ?? 0m),
                Input = g.Sum(u => (long)u.InputTokens),
                Output = g.Sum(u => (long)u.OutputTokens),
                CacheRead = g.Sum(u => (long)u.CacheReadTokens),
                CacheWrite = g.Sum(u => (long)u.CacheWriteTokens),
            })
            .ToListAsync(ct);

        var spend = new Dictionary<Ulid, decimal>();
        foreach (var row in rows.Where(r => wanted.Contains(r.TaskId)))
        {
            var cost = row.Vendor
                ? row.VendorCost
                : CostCalculator.Calculate(
                    row.Model, Clamp(row.Input), Clamp(row.Output), Clamp(row.CacheRead), Clamp(row.CacheWrite),
                    rates: await modelRates.TryGetRatesAsync(row.Model, ct),
                    cacheRates: await modelRates.TryGetCacheRatesAsync(row.Model, ct));
            spend[row.TaskId] = spend.GetValueOrDefault(row.TaskId) + cost;
        }
        return spend;

        static int Clamp(long tokens) => (int)Math.Min(tokens, int.MaxValue);
    }

    public async Task<IReadOnlyDictionary<Ulid, decimal>> GetTaskCostsAsync(
        Ulid orgId, Ulid projectId, IReadOnlyCollection<Ulid> taskIds, CancellationToken ct = default)
    {
        if (taskIds.Count == 0)
            return EmptyCosts;

        var wanted = taskIds.ToHashSet();

        var rows = await db.UsageRecords
            .AsNoTracking()
            .Where(u => u.OrgId == orgId && u.ProjectId == projectId && u.TaskId != null)
            .GroupBy(u => u.TaskId!.Value)
            .Select(g => new { TaskId = g.Key, Cost = g.Sum(u => u.CostTotal) })
            .ToListAsync(ct);

        return rows
            .Where(r => wanted.Contains(r.TaskId))
            .ToDictionary(r => r.TaskId, r => r.Cost);
    }

    public async Task<IReadOnlyDictionary<Ulid, decimal>> GetExecutionCostsAsync(
        Ulid orgId, Ulid projectId, IReadOnlyCollection<Ulid> executionIds, CancellationToken ct = default)
    {
        if (executionIds.Count == 0)
            return EmptyCosts;

        var wanted = executionIds.ToHashSet();

        var rows = await db.UsageRecords
            .AsNoTracking()
            .Where(u => u.OrgId == orgId && u.ProjectId == projectId && u.ExecutionId != null)
            .GroupBy(u => u.ExecutionId!.Value)
            .Select(g => new { ExecutionId = g.Key, Cost = g.Sum(u => u.CostTotal) })
            .ToListAsync(ct);

        return rows
            .Where(r => wanted.Contains(r.ExecutionId))
            .ToDictionary(r => r.ExecutionId, r => r.Cost);
    }

    public Task<IReadOnlyList<ExecutionTokenSample>> GetExecutionTokenSamplesAsync(
        Ulid orgId, Ulid projectId, IReadOnlyCollection<Ulid> executionIds, CancellationToken ct = default)
        => GetExecutionTokenSamplesAsync(orgId, [projectId], executionIds, ct);

    public async Task<IReadOnlyList<ExecutionTokenSample>> GetExecutionTokenSamplesAsync(
        Ulid orgId, IReadOnlyCollection<Ulid> projectIds, IReadOnlyCollection<Ulid> executionIds,
        CancellationToken ct = default)
    {
        if (projectIds.Count == 0 || executionIds.Count == 0)
            return [];

        // A List, never an array: a list expands inline; an array needs a Ulid array converter.
        var projects = projectIds.ToList();
        var wanted = executionIds.ToHashSet();

        // Grouped by (execution, model) rather than execution alone: one execution can call more
        // than one model, and collapsing that would force an arbitrary choice of which model to
        // report. The forecaster sums tokens per execution and ignores the model anyway.
        var rows = await db.UsageRecords
            .AsNoTracking()
            .Where(u => u.OrgId == orgId && projects.Contains(u.ProjectId) && u.ExecutionId != null)
            .GroupBy(u => new { ExecutionId = u.ExecutionId!.Value, u.Model })
            .Select(g => new ExecutionTokenSample(
                g.Key.ExecutionId,
                g.Key.Model,
                g.Sum(u => u.InputTokens),
                g.Sum(u => u.OutputTokens),
                g.Sum(u => u.CacheReadTokens),
                g.Sum(u => u.CacheWriteTokens)))
            .ToListAsync(ct);

        return rows.Where(r => wanted.Contains(r.ExecutionId)).ToList();
    }

    public async Task<IReadOnlyList<ExecutionTokenSample>> GetExecutionTokenSamplesAcrossOrgsAsync(
        IReadOnlyCollection<Ulid> projectIds, IReadOnlyCollection<Ulid> executionIds,
        CancellationToken ct = default)
    {
        if (projectIds.Count == 0 || executionIds.Count == 0)
            return [];

        // Same shape as the org-scoped twin: the SQL filter is on ProjectId (non-nullable, so a
        // List `Contains` translates), and the execution ids — a nullable column never matched
        // with Contains — are matched in memory after grouping to one row per execution/model.
        var projects = projectIds.ToList();
        var wanted = executionIds.ToHashSet();

        var rows = await db.UsageRecords
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(u => projects.Contains(u.ProjectId) && u.ExecutionId != null)
            .GroupBy(u => new { ExecutionId = u.ExecutionId!.Value, u.Model })
            .Select(g => new ExecutionTokenSample(
                g.Key.ExecutionId,
                g.Key.Model,
                g.Sum(u => u.InputTokens),
                g.Sum(u => u.OutputTokens),
                g.Sum(u => u.CacheReadTokens),
                g.Sum(u => u.CacheWriteTokens)))
            .ToListAsync(ct);

        return rows.Where(r => wanted.Contains(r.ExecutionId)).ToList();
    }

    public async Task<IReadOnlyDictionary<Ulid, AgentTokenTotals>> GetAgentTokenTotalsAsync(
        Ulid orgId, Ulid projectId, IReadOnlyCollection<Ulid> agentIds, CancellationToken ct = default)
    {
        if (agentIds.Count == 0)
            return EmptyAgentTotals;

        var wanted = agentIds.ToHashSet();

        var rows = await db.UsageRecords
            .AsNoTracking()
            .Where(u => u.OrgId == orgId && u.ProjectId == projectId && u.AgentId != null)
            .GroupBy(u => u.AgentId!.Value)
            .Select(g => new
            {
                AgentId = g.Key,
                Input = g.Sum(u => (long)u.InputTokens),
                Output = g.Sum(u => (long)u.OutputTokens),
                CacheRead = g.Sum(u => (long)u.CacheReadTokens),
                CacheWrite = g.Sum(u => (long)u.CacheWriteTokens),
            })
            .ToListAsync(ct);

        return rows
            .Where(r => wanted.Contains(r.AgentId))
            .ToDictionary(
                r => r.AgentId,
                r => new AgentTokenTotals(r.Input, r.Output, r.CacheRead, r.CacheWrite));
    }

    public async Task<ProjectSpendWindow> GetProjectSpendAsync(
        Ulid orgId, Ulid projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        // No id list to match against, so unlike its neighbours this one filters entirely in SQL.
        // CreatedAt is when the usage was recorded; see the contract for why that, not run start.
        var scoped = db.UsageRecords
            .AsNoTracking()
            .Where(u => u.OrgId == orgId
                        && u.ProjectId == projectId
                        && u.ExecutionId != null
                        && u.CreatedAt >= from
                        && u.CreatedAt < to);

        var totals = await scoped
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Cost = g.Sum(u => u.CostTotal),
                Tokens = g.Sum(u => (long)u.InputTokens + u.OutputTokens + u.CacheReadTokens + u.CacheWriteTokens),
            })
            .FirstOrDefaultAsync(ct);

        if (totals is null)
            return ProjectSpendWindow.Empty;

        var byAgent = await scoped
            .Where(u => u.AgentId != null)
            .GroupBy(u => u.AgentId!.Value)
            .Select(g => new
            {
                AgentId = g.Key,
                Cost = g.Sum(u => u.CostTotal),
                Tokens = g.Sum(u => (long)u.InputTokens + u.OutputTokens + u.CacheReadTokens + u.CacheWriteTokens),
            })
            .ToListAsync(ct);

        return new ProjectSpendWindow(
            totals.Cost,
            totals.Tokens,
            byAgent.ToDictionary(r => r.AgentId, r => new AgentSpend(r.Cost, r.Tokens)));
    }

    private static readonly IReadOnlyDictionary<Ulid, decimal> EmptyCosts =
        new Dictionary<Ulid, decimal>();

    private static readonly IReadOnlyDictionary<Ulid, AgentTokenTotals> EmptyAgentTotals =
        new Dictionary<Ulid, AgentTokenTotals>();
}
