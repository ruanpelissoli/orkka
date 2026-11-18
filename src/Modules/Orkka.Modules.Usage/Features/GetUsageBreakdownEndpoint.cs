namespace Orkka.Modules.Usage.Features;

using Orkka.SharedKernel.Security;
using Orkka.Modules.Usage.Infrastructure;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;

public sealed class GetUsageBreakdownEndpoint(
    UsageDbContext db,
    Orkka.Modules.Projects.Contracts.IProjectsApi projectsApi,
    Orkka.Modules.Agents.Contracts.IAgentsApi agentsApi,
    Orkka.Modules.Tasks.Contracts.ITasksApi tasksApi,
    ICurrentUser currentUser)
    : EndpointWithoutRequest<UsageBreakdownResponse>
{
    public override void Configure()
    {
        Get("/api/usage/breakdown");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var groupBy = Query<string?>("groupBy", isRequired: false) ?? "project";
        var from = Query<DateTimeOffset?>("from", isRequired: false);
        var to = Query<DateTimeOffset?>("to", isRequired: false);

        var query = db.UsageRecords.AsNoTracking()
            .Where(u => u.OrgId == currentUser.OrgId);

        if (from.HasValue) query = query.Where(u => u.CreatedAt >= from.Value);
        if (to.HasValue) query = query.Where(u => u.CreatedAt <= to.Value);

        List<UsageBreakdownItem> items = groupBy switch
        {
            "agent" => await BuildAgentBreakdownAsync(query, ct),

            "task" => await BuildTaskBreakdownAsync(query, ct),

            "model" => await BuildModelBreakdownAsync(query, ct),

            _ => await BuildProjectBreakdownAsync(query, ct),
        };

        await Send.OkAsync(new UsageBreakdownResponse(items), ct);
    }

    // Same shape as the other branches: aggregate into an anonymous type, then map. Building
    // the response record inside the GroupBy projection defeats EF's translation when the
    // projection also carries the Distinct().Count() subquery.
    private static async Task<List<UsageBreakdownItem>> BuildModelBreakdownAsync(
        IQueryable<Orkka.Modules.Usage.Domain.UsageRecord> query, CancellationToken ct)
    {
        var groups = await query
            .GroupBy(u => u.Model)
            .Select(g => new
            {
                Model = g.Key,
                Cost = g.Sum(u => u.CostTotal),
                Input = g.Sum(u => (long)u.InputTokens),
                Output = g.Sum(u => (long)u.OutputTokens),
                CacheRead = g.Sum(u => (long)u.CacheReadTokens),
                CacheWrite = g.Sum(u => (long)u.CacheWriteTokens),
                Tasks = g.Select(u => u.TaskId).Distinct().Count(),
            })
            .ToListAsync(ct);

        return groups
            .Select(g => new UsageBreakdownItem(
                g.Model, g.Model, g.Cost, g.Input, g.Output, g.CacheRead, g.CacheWrite, g.Tasks))
            .OrderByDescending(x => x.CostUsd)
            .ToList();
    }

    /// <summary>
    /// Usage per task, each row a link to the task. Rows with no TaskId (chat
    /// turns, briefs, design work, platform calls) collapse into one bucket rather than being
    /// dropped, so the rows still add up to the org total on the summary card.
    /// </summary>
    private async Task<List<UsageBreakdownItem>> BuildTaskBreakdownAsync(
        IQueryable<Orkka.Modules.Usage.Domain.UsageRecord> query, CancellationToken ct)
    {
        var groups = await query
            .GroupBy(u => u.TaskId)
            .Select(g => new
            {
                TaskId = g.Key,
                Cost = g.Sum(u => u.CostTotal),
                Input = g.Sum(u => (long)u.InputTokens),
                Output = g.Sum(u => (long)u.OutputTokens),
                CacheRead = g.Sum(u => (long)u.CacheReadTokens),
                CacheWrite = g.Sum(u => (long)u.CacheWriteTokens),
            })
            .ToListAsync(ct);

        var ids = groups.Where(g => g.TaskId.HasValue).Select(g => g.TaskId!.Value).ToList();
        var refs = await tasksApi.GetTaskRefsAsync(ids, ct);

        return
        [
            .. groups
                .Select(g =>
                {
                    // A deleted task's spend still happened — keep the row, lose the link.
                    var found = g.TaskId.HasValue
                        && refs.TryGetValue(g.TaskId.Value, out var r) ? r : null;
                    return new UsageBreakdownItem(
                        g.TaskId?.ToString() ?? "none",
                        g.TaskId is null
                            ? "Chat & other work"
                            : found?.Title ?? "Deleted task",
                        g.Cost, g.Input, g.Output, g.CacheRead, g.CacheWrite,
                        TaskCount: g.TaskId is null ? 0 : 1,
                        LinkProjectId: found?.ProjectId.ToString(),
                        LinkTaskNumber: found?.Number);
                })
                .OrderByDescending(x => x.CostUsd)
        ];
    }

    // Cross-module names (agents/projects schemas) are resolved via contracts after the
    // aggregation, with no cross-schema joins.
    private async Task<List<UsageBreakdownItem>> BuildAgentBreakdownAsync(
        IQueryable<Orkka.Modules.Usage.Domain.UsageRecord> query, CancellationToken ct)
    {
        var groups = await query
            .GroupBy(u => u.AgentId)
            .Select(g => new
            {
                AgentId = g.Key,
                Cost = g.Sum(u => u.CostTotal),
                Input = g.Sum(u => (long)u.InputTokens),
                Output = g.Sum(u => (long)u.OutputTokens),
                CacheRead = g.Sum(u => (long)u.CacheReadTokens),
                CacheWrite = g.Sum(u => (long)u.CacheWriteTokens),
                Tasks = g.Select(u => u.TaskId).Distinct().Count()
            })
            .ToListAsync(ct);

        var ids = groups.Where(g => g.AgentId.HasValue).Select(g => g.AgentId!.Value).ToList();
        var names = await agentsApi.GetAgentNamesAsync(ids, ct);

        return
        [
            .. groups
                .Select(g => new UsageBreakdownItem(
                    g.AgentId?.ToString() ?? "unknown",
                    g.AgentId.HasValue && names.TryGetValue(g.AgentId.Value, out var name) ? name : "Unknown",
                    g.Cost, g.Input, g.Output, g.CacheRead, g.CacheWrite, g.Tasks))
                .OrderByDescending(x => x.CostUsd)
        ];
    }

    private async Task<List<UsageBreakdownItem>> BuildProjectBreakdownAsync(
        IQueryable<Orkka.Modules.Usage.Domain.UsageRecord> query, CancellationToken ct)
    {
        var groups = await query
            .GroupBy(u => u.ProjectId)
            .Select(g => new
            {
                ProjectId = g.Key,
                Cost = g.Sum(u => u.CostTotal),
                Input = g.Sum(u => (long)u.InputTokens),
                Output = g.Sum(u => (long)u.OutputTokens),
                CacheRead = g.Sum(u => (long)u.CacheReadTokens),
                CacheWrite = g.Sum(u => (long)u.CacheWriteTokens),
                Tasks = g.Select(u => u.TaskId).Distinct().Count()
            })
            .ToListAsync(ct);

        var names = await projectsApi.GetProjectNamesAsync(groups.Select(g => g.ProjectId).ToList(), ct);

        return
        [
            .. groups
                .Select(g => new UsageBreakdownItem(
                    g.ProjectId.ToString(),
                    names.TryGetValue(g.ProjectId, out var name) ? name : "Unknown",
                    g.Cost, g.Input, g.Output, g.CacheRead, g.CacheWrite, g.Tasks))
                .OrderByDescending(x => x.CostUsd)
        ];
    }
}
