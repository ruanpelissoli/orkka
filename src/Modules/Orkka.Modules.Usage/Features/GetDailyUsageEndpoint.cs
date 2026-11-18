namespace Orkka.Modules.Usage.Features;

using Orkka.SharedKernel.Security;
using Orkka.Modules.Usage.Infrastructure;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;

public sealed class GetDailyUsageEndpoint(UsageDbContext db, ICurrentUser currentUser)
    : EndpointWithoutRequest<DailyUsageResponse>
{
    public override void Configure()
    {
        Get("/api/usage/daily");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var from = Query<DateTimeOffset?>("from", isRequired: false);
        var to = Query<DateTimeOffset?>("to", isRequired: false);

        var query = db.UsageRecords.AsNoTracking()
            .Where(u => u.OrgId == currentUser.OrgId);

        if (from.HasValue) query = query.Where(u => u.CreatedAt >= from.Value);
        if (to.HasValue) query = query.Where(u => u.CreatedAt <= to.Value);

        // The day is derived here, not in SQL: CreatedAt is stored as UTC ticks and SQLite has
        // no function that turns them into a date. Only the summed columns are read.
        var rows = await query
            .Select(u => new
            {
                u.CreatedAt, u.CostTotal, u.InputTokens, u.OutputTokens,
                u.CacheReadTokens, u.CacheWriteTokens, u.TaskId,
            })
            .ToListAsync(ct);

        var items = rows
            .GroupBy(u => DateOnly.FromDateTime(u.CreatedAt.UtcDateTime))
            .OrderBy(g => g.Key)
            .Select(g => new DailyUsageItem(
                g.Key,
                g.Sum(u => u.CostTotal),
                g.Sum(u => (long)u.InputTokens),
                g.Sum(u => (long)u.OutputTokens),
                g.Sum(u => (long)u.CacheReadTokens),
                g.Sum(u => (long)u.CacheWriteTokens),
                g.Where(u => u.TaskId != null).Select(u => u.TaskId).Distinct().Count()))
            .ToList();

        await Send.OkAsync(new DailyUsageResponse(items), ct);
    }
}
