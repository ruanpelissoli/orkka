namespace Orkka.Modules.Tasks.Features.Forecast;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.Modules.Tasks.Infrastructure.Services;
using Orkka.SharedKernel.Security;

/// <param name="AgentSecondsMedian">Agent time, not wall-clock-to-done. The field is named for
/// what it measures so a caller can't render it as a delivery time by accident.</param>
/// <param name="CostMedian">Always null: every task runs on the developer's own subscription,
/// so no money changes hands.</param>
public sealed record TaskForecastResponse(
    decimal? CostP25,
    decimal? CostMedian,
    decimal? CostP75,
    int AgentSecondsP25,
    int AgentSecondsMedian,
    int AgentSecondsP75,
    IReadOnlyList<string> LanesAssumed,
    string BucketLevel,
    int MinLaneSamples);

/// <summary>
/// The create form's estimate, for a task that doesn't exist yet. Every field is something the
/// form already knows, which is what lets the number move when the user switches to Opus or
/// turns a lane off — a whole-task historical average could not react to either.
/// </summary>
public sealed record DraftForecastRequest(
    string Type = "Feature",
    string? Model = null,
    Ulid? AssignedAgentId = null,
    bool SkipPmReview = false,
    bool SkipPlanning = false,
    bool SkipTesting = false,
    bool SkipCodeReview = false);

/// <summary>Workload left in a milestone. Deliberately not a completion date.</summary>
public sealed record MilestoneForecastResponse(
    decimal? CostP25,
    decimal? CostMedian,
    decimal? CostP75,
    int AgentSecondsP25,
    int AgentSecondsMedian,
    int AgentSecondsP75,
    /// <summary>Open tasks the forecast covers. Tasks with too little history are excluded
    /// rather than guessed at, so this can be lower than the milestone's open count.</summary>
    int TasksCovered,
    int TasksOpen,
    /// <summary>The loosest fallback any covered task needed: "project" | "org" | "platform".
    /// A new org's whole roadmap is "platform", and the UI says so next to the number.</summary>
    string? BucketLevel = null);

/// <summary>
/// What a task is likely to cost and how much agent time it needs, from measured history.
/// Answers 204 when there isn't enough history — an empty state, not an error, and the common
/// case for a new org.
/// </summary>
public sealed class GetTaskForecastEndpoint(
    TasksDbContext db,
    ITaskForecaster forecaster,
    ForecastRecorder recorder,
    ICurrentUser currentUser)
    : EndpointWithoutRequest<TaskForecastResponse>
{
    public override void Configure()
    {
        Get("/api/tasks/{taskId:ulid}/forecast");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var taskId = Ulid.Parse(Route<string>("taskId")!);

        var task = await db.Tasks
            .FirstOrDefaultAsync(t => t.Id == taskId && t.OrgId == currentUser.OrgId, ct);

        if (task is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var forecast = await forecaster.ForecastAsync(taskId, ct);
        if (forecast is null)
        {
            await Send.NoContentAsync(ct);
            return;
        }

        // Which screen asked matters: an estimate made at creation and one made after a plan
        // exists are different predictions and are scored separately.
        var surface = Enum.TryParse<ForecastSurface>(Query<string>("surface", isRequired: false),
            ignoreCase: true, out var parsed)
            ? parsed
            : ForecastSurface.TaskCreate;

        await recorder.RecordAsync(task, forecast, surface, ct);

        await Send.OkAsync(new TaskForecastResponse(
            forecast.CostP25, forecast.CostMedian, forecast.CostP75,
            forecast.AgentSecondsP25, forecast.AgentSecondsMedian, forecast.AgentSecondsP75,
            forecast.Assumptions.Lanes.Select(l => l.Lane).ToList(),
            forecast.Assumptions.BucketLevel,
            forecast.Assumptions.MinLaneSamples), ct);
    }
}

/// <summary>
/// Forecast for a task the user is still typing. POST because the parameters are a body, not
/// because it changes anything — nothing is persisted, and there is no forecast row to score
/// until a real task exists.
/// </summary>
public sealed class PostDraftForecastEndpoint(
    ITaskForecaster forecaster,
    IProjectResolver projectResolver,
    ICurrentUser currentUser)
    : Endpoint<DraftForecastRequest, TaskForecastResponse>
{
    public override void Configure()
    {
        Post("/api/projects/{projectId}/tasks/forecast");
    }

    public override async Task HandleAsync(DraftForecastRequest req, CancellationToken ct)
    {
        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var model = string.IsNullOrWhiteSpace(req.Model) ? null : req.Model.Trim();

        var forecast = await forecaster.ForecastDraftAsync(new TaskForecastInput(
            currentUser.OrgId,
            projectId,
            Enum.TryParse<TaskType>(req.Type, ignoreCase: true, out var type) ? type : TaskType.Feature,
            model,
            req.AssignedAgentId,
            req.SkipPmReview, req.SkipPlanning, req.SkipTesting, req.SkipCodeReview), ct);

        if (forecast is null)
        {
            await Send.NoContentAsync(ct);
            return;
        }

        await Send.OkAsync(new TaskForecastResponse(
            forecast.CostP25, forecast.CostMedian, forecast.CostP75,
            forecast.AgentSecondsP25, forecast.AgentSecondsMedian, forecast.AgentSecondsP75,
            forecast.Assumptions.Lanes.Select(l => l.Lane).ToList(),
            forecast.Assumptions.BucketLevel,
            forecast.Assumptions.MinLaneSamples), ct);
    }
}

/// <summary>
/// Remaining workload for a milestone: the sum of forecasts over its open tasks.
///
/// Separate from the milestone list so the roadmap loads at the speed of a count query. This one
/// forecasts every open task and is meant to be fetched after the page renders.
/// </summary>
public sealed class GetMilestoneForecastEndpoint(
    TasksDbContext db,
    ITaskForecaster forecaster,
    IProjectResolver projectResolver,
    ICurrentUser currentUser)
    : EndpointWithoutRequest<MilestoneForecastResponse>
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/milestones/{id:ulid}/forecast");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var milestoneId = Ulid.Parse(Route<string>("id")!);

        var openTaskIds = await db.Tasks
            .AsNoTracking()
            .Where(t => t.OrgId == currentUser.OrgId && t.ProjectId == projectId
                && t.MilestoneId == milestoneId && !t.IsArchived
                && t.CurrentLane != Lane.Done)
            .Select(t => t.Id)
            .ToListAsync(ct);

        if (openTaskIds.Count == 0)
        {
            await Send.NoContentAsync(ct);
            return;
        }

        decimal? costP25 = 0, costMedian = 0, costP75 = 0;
        int timeP25 = 0, timeMedian = 0, timeP75 = 0, covered = 0;
        string? bucketLevel = null;

        foreach (var id in openTaskIds)
        {
            var forecast = await forecaster.ForecastAsync(id, ct);
            if (forecast is null)
                continue;

            covered++;
            if (BucketRank(forecast.Assumptions.BucketLevel) > BucketRank(bucketLevel))
                bucketLevel = forecast.Assumptions.BucketLevel;
            timeP25 += forecast.AgentSecondsP25;
            timeMedian += forecast.AgentSecondsMedian;
            timeP75 += forecast.AgentSecondsP75;

            // One runner-executed task in the milestone doesn't zero the total, but it does
            // mean the money figure no longer covers every task — so it stops being reported
            // rather than quietly under-stating the rest.
            if (forecast.CostMedian is null)
            {
                costP25 = costMedian = costP75 = null;
            }
            else if (costMedian is not null)
            {
                costP25 += forecast.CostP25;
                costMedian += forecast.CostMedian;
                costP75 += forecast.CostP75;
            }
        }

        if (covered == 0)
        {
            await Send.NoContentAsync(ct);
            return;
        }

        await Send.OkAsync(new MilestoneForecastResponse(
            costP25, costMedian, costP75,
            timeP25, timeMedian, timeP75,
            covered, openTaskIds.Count, bucketLevel), ct);
    }

    /// <summary>Looser is higher: one platform-sampled task makes the whole total "platform".</summary>
    private static int BucketRank(string? level) => level switch
    {
        "platform" => 3,
        "org" => 2,
        "project" => 1,
        _ => 0,
    };
}
