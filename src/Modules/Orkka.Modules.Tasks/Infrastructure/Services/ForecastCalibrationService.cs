namespace Orkka.Modules.Tasks.Infrastructure.Services;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Usage.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;

/// <summary>
/// Scores forecasts against what actually happened.
///
/// The classification is the load-bearing part. A forecast that assumed Testing would run,
/// against a task the user then set SkipTesting on, is a STALE forecast — not an estimator
/// error. Scoring the two together silently corrupts the accuracy metric, which is the only
/// thing that can tell a working estimator from a plausible-looking one.
/// </summary>
public sealed class ForecastCalibrationService(
    TasksDbContext db,
    IUsageQuery usageQuery,
    ILogger<ForecastCalibrationService> logger)
{
    /// <summary>Scores every pending forecast whose task has reached a terminal state. Returns
    /// how many rows it settled.</summary>
    public async Task<int> ScorePendingAsync(int batchSize, CancellationToken ct)
    {
        var pending = await db.TaskForecasts
            .Where(f => f.Outcome == ForecastOutcome.Pending)
            .OrderBy(f => f.CreatedAt)
            .Take(batchSize)
            .ToListAsync(ct);

        if (pending.Count == 0)
            return 0;

        // A List, never a Ulid[]: an array parameter needs an array converter EF has not been
        // able to build for Ulid, while a list expands inline. Every other Ulid `Contains` in
        // this codebase is a List for the same reason.
        var taskIds = pending.Select(f => f.TaskId).Distinct().ToList();

        var tasks = await db.Tasks
            .AsNoTracking()
            .Where(t => taskIds.Contains(t.Id))
            .Select(t => new { t.Id, t.OrgId, t.Status, t.IsArchived, t.ResetCount })
            .ToDictionaryAsync(t => t.Id, ct);

        var executions = (await db.TaskExecutions
                .AsNoTracking()
                .Where(e => taskIds.Contains(e.TaskId) && e.StartedAt != null && e.CompletedAt != null)
                .Select(e => new { e.Id, e.TaskId, e.Lane, e.StartedAt, e.CompletedAt })
                .ToListAsync(ct))
            .GroupBy(e => e.TaskId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var settled = 0;

        foreach (var forecast in pending)
        {
            if (!tasks.TryGetValue(forecast.TaskId, out var task) || task.IsArchived)
            {
                Invalidate(forecast, "task archived or deleted");
                settled++;
                continue;
            }

            var assumptions = ParseAssumptions(forecast.AssumptionsJson);

            // A reset re-runs the lanes from scratch. The prediction was about a different run.
            if (assumptions is not null && task.ResetCount != assumptions.ResetCount)
            {
                Invalidate(forecast, "task was reset");
                settled++;
                continue;
            }

            // Not finished yet — leave it pending and pick it up on a later sweep.
            if (task.Status != Contracts.TaskStatus.Completed)
                continue;

            var runs = executions.GetValueOrDefault(forecast.TaskId, []);
            var actualLanes = runs.Select(e => e.Lane.ToString()).Distinct().OrderBy(l => l).ToList();
            var assumedLanes = (assumptions?.Lanes ?? [])
                .Select(l => l.Lane).Distinct().OrderBy(l => l).ToList();

            if (!actualLanes.SequenceEqual(assumedLanes))
            {
                Invalidate(forecast, $"lanes ran [{string.Join(",", actualLanes)}], " +
                    $"forecast assumed [{string.Join(",", assumedLanes)}]");
                settled++;
                continue;
            }

            var costs = await usageQuery.GetExecutionCostsAsync(
                task.OrgId, forecast.ProjectId, runs.Select(r => r.Id).ToList(), ct);

            forecast.ActualCost = costs.Values.Sum();
            forecast.ActualAgentSeconds = (int)runs.Sum(
                r => (r.CompletedAt!.Value - r.StartedAt!.Value).TotalSeconds);
            forecast.ActualLanesJson = JsonSerializer.Serialize(actualLanes, ForecastRecorder.JsonOptions);
            forecast.Outcome = ForecastOutcome.Scored;
            forecast.ScoredAt = DateTimeOffset.UtcNow;
            settled++;
        }

        await db.SaveChangesAsync(ct);

        if (settled > 0)
            logger.LogInformation("Scored {Count} forecast(s)", settled);

        return settled;
    }

    private void Invalidate(TaskForecast forecast, string reason)
    {
        logger.LogDebug("Forecast {Id} invalidated: {Reason}", forecast.Id, reason);
        forecast.Outcome = ForecastOutcome.Invalidated;
        forecast.ScoredAt = DateTimeOffset.UtcNow;
    }

    private PersistedAssumptions? ParseAssumptions(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<PersistedAssumptions>(json, ForecastRecorder.JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Unreadable forecast assumptions — treating as no assumptions");
            return null;
        }
    }
}
