namespace Orkka.Modules.Usage.Features;

using Orkka.SharedKernel.Security;
using Orkka.Modules.Usage.Infrastructure;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;

public sealed class GetUsageSummaryEndpoint(UsageDbContext db, ICurrentUser currentUser)
    : EndpointWithoutRequest<UsageSummaryResponse>
{
    public override void Configure()
    {
        Get("/api/usage/summary");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var from = Query<DateTimeOffset?>("from", isRequired: false);
        var to = Query<DateTimeOffset?>("to", isRequired: false);

        var query = db.UsageRecords.AsNoTracking()
            .Where(u => u.OrgId == currentUser.OrgId);

        if (from.HasValue) query = query.Where(u => u.CreatedAt >= from.Value);
        if (to.HasValue) query = query.Where(u => u.CreatedAt <= to.Value);

        var summary = await query
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalCost = g.Sum(u => u.CostTotal),
                TotalInputTokens = g.Sum(u => (long)u.InputTokens),
                TotalOutputTokens = g.Sum(u => (long)u.OutputTokens),
                TotalCacheReadTokens = g.Sum(u => (long)u.CacheReadTokens),
                TotalCacheWriteTokens = g.Sum(u => (long)u.CacheWriteTokens),
                TaskCount = g.Select(u => u.TaskId).Distinct().Count()
            })
            .FirstOrDefaultAsync(ct);

        await Send.OkAsync(new UsageSummaryResponse(
            TotalCostUsd: summary?.TotalCost ?? 0,
            TotalInputTokens: summary?.TotalInputTokens ?? 0,
            TotalOutputTokens: summary?.TotalOutputTokens ?? 0,
            TotalCacheReadTokens: summary?.TotalCacheReadTokens ?? 0,
            TotalCacheWriteTokens: summary?.TotalCacheWriteTokens ?? 0,
            TaskCount: summary?.TaskCount ?? 0), ct);
    }
}
