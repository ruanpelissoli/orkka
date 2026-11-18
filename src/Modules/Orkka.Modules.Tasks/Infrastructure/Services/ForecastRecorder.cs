namespace Orkka.Modules.Tasks.Infrastructure.Services;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;

/// <summary>
/// What a forecast assumed, as persisted. A superset of <see cref="ForecastAssumptions"/>: the
/// scoring sweep also needs the task's reset count at the time, because a reset task re-runs its
/// lanes and is a different task in every respect that matters.
/// </summary>
public sealed record PersistedAssumptions(
    [property: JsonPropertyName("lanes")] IReadOnlyList<ForecastLaneAssumption> Lanes,
    [property: JsonPropertyName("bucketLevel")] string BucketLevel,
    [property: JsonPropertyName("minLaneSamples")] int MinLaneSamples,
    [property: JsonPropertyName("costForecastSuppressed")] bool CostForecastSuppressed,
    [property: JsonPropertyName("resetCount")] int ResetCount);

/// <summary>
/// Persists a forecast at the moment it is shown, so it can be scored against reality later.
/// Storing it is the whole point of the feature: without a record of what was predicted there
/// is no way to distinguish a working estimator from a plausible-looking one.
/// </summary>
public sealed class ForecastRecorder(TasksDbContext db)
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public async Task RecordAsync(
        TaskEntity task, TaskForecastResult forecast, ForecastSurface surface, CancellationToken ct)
    {
        // One row per (task, surface): re-opening the plan screen should refresh what was
        // predicted, not accumulate a row per page view.
        var existing = await db.TaskForecasts
            .FirstOrDefaultAsync(f => f.TaskId == task.Id && f.Surface == surface
                && f.Outcome == ForecastOutcome.Pending, ct);

        var assumptions = JsonSerializer.Serialize(new PersistedAssumptions(
            forecast.Assumptions.Lanes,
            forecast.Assumptions.BucketLevel,
            forecast.Assumptions.MinLaneSamples,
            forecast.Assumptions.CostForecastSuppressed,
            task.ResetCount), JsonOptions);

        var row = existing ?? new TaskForecast
        {
            OrgId = task.OrgId,
            TaskId = task.Id,
            ProjectId = task.ProjectId,
            Surface = surface,
        };

        row.CostP25 = forecast.CostP25;
        row.CostMedian = forecast.CostMedian;
        row.CostP75 = forecast.CostP75;
        row.AgentSecondsP25 = forecast.AgentSecondsP25;
        row.AgentSecondsMedian = forecast.AgentSecondsMedian;
        row.AgentSecondsP75 = forecast.AgentSecondsP75;
        row.AssumptionsJson = assumptions;

        if (existing is null)
            db.TaskForecasts.Add(row);

        await db.SaveChangesAsync(ct);
    }
}
