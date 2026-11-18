namespace Orkka.IntegrationTests;

using System.Net.Http.Json;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Usage.Contracts;
using Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Features.Forecast;
using Orkka.Modules.Tasks.Features.Milestones;
using Orkka.Modules.Tasks.Features.Tasks;
using Orkka.Modules.Tasks.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// The rules that decide whether the forecast is trustworthy, exercised against the real
/// estimator over seeded history.
/// </summary>
[Collection("Integration")]
public sealed class TaskForecastTests(OrkkaApiFactory factory)
{
    private const int TokensPerExecution = 100_000;
    private const int SecondsPerExecution = 300;

    /// <summary>Lanes a default task passes through when QA is off: PM, Planning, Developing,
    /// Code Review.</summary>
    private static readonly Lane[] DefaultLanes =
        [Lane.Ready, Lane.Planning, Lane.Developing, Lane.CodeReview];

    /// <summary>
    /// Seeds completed tasks with one execution per lane plus matching usage rows; the
    /// estimator samples the executions' durations.
    /// </summary>
    // Task numbers are unique per (project, number), and a test may seed twice into one
    // project (the hybrid case). A process-wide counter keeps them distinct.
    private static int _nextTaskNumber = 9000;

    private static async Task SeedHistoryAsync(
        TasksDbContext db, IUsageRecorder usage,
        Ulid orgId, Ulid projectId, int taskCount,
        Lane[]? lanes = null,
        TaskType type = TaskType.Feature)
    {
        var seededAt = DateTimeOffset.UtcNow.AddDays(-1);

        for (var i = 0; i < taskCount; i++)
        {
            var task = new TaskEntity
            {
                Id = Ulid.NewUlid(),
                OrgId = orgId,
                ProjectId = projectId,
                Number = Interlocked.Increment(ref _nextTaskNumber),
                Title = $"History {i}",
                Type = type,
                Status = TaskStatus.Completed,
                CurrentLane = Lane.Done,
                CompletedAt = seededAt,
            };
            db.Tasks.Add(task);

            foreach (var lane in lanes ?? DefaultLanes)
            {
                var execution = new TaskExecution
                {
                    Id = Ulid.NewUlid(),
                    OrgId = orgId,
                    TaskId = task.Id,
                    Lane = lane,
                    Status = ExecutionStatus.Completed,
                    StartedAt = seededAt,
                    CompletedAt = seededAt.AddSeconds(SecondsPerExecution),
                };
                db.TaskExecutions.Add(execution);

                await usage.RecordUsageAsync(new RecordUsageCommand(
                    orgId, projectId, task.Id, null, execution.Id,
                    "claude-sonnet-5", TokensPerExecution, TokensPerExecution / 10));
            }
        }

        await db.SaveChangesAsync();
    }

    private static TaskForecastInput Input(
        Ulid orgId, Ulid projectId,
        string? model = null,
        bool skipTesting = false,
        bool skipCodeReview = false,
        TaskType type = TaskType.Feature) =>
        new(orgId, projectId, type, model, null,
            SkipPmReview: false, SkipPlanning: false, skipTesting, skipCodeReview);

    [Fact]
    public async Task BelowSampleFloor_SuppressesTheWholeForecast()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var usage = scope.ServiceProvider.GetRequiredService<IUsageRecorder>();
        var forecaster = scope.ServiceProvider.GetRequiredService<ITaskForecaster>();
        var (orgId, projectId) = (TestCurrentUser.TestOrgId, Ulid.NewUlid());

        // Four is one short of the floor. A partial sum reads as a full one, so nothing is
        // returned rather than a smaller number. Sampled per task type, and the platform-wide
        // fallback sees every test's history in this shared database — so this uses a type no
        // other test seeds, which makes "the whole platform has four" literally true.
        await SeedHistoryAsync(db, usage, orgId, projectId, taskCount: 4, type: TaskType.Refactor);

        var forecast = await forecaster.ForecastDraftAsync(Input(orgId, projectId, type: TaskType.Refactor));

        forecast.Should().BeNull();
    }

    [Fact]
    public async Task AtSampleFloor_ProducesARangeNotAPoint()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var usage = scope.ServiceProvider.GetRequiredService<IUsageRecorder>();
        var forecaster = scope.ServiceProvider.GetRequiredService<ITaskForecaster>();
        var (orgId, projectId) = (TestCurrentUser.TestOrgId, Ulid.NewUlid());

        await SeedHistoryAsync(db, usage, orgId, projectId, taskCount: 6);

        var forecast = await forecaster.ForecastDraftAsync(Input(orgId, projectId));

        forecast.Should().NotBeNull();
        forecast!.AgentSecondsP25.Should().BeLessThanOrEqualTo(forecast.AgentSecondsMedian);
        forecast.AgentSecondsP75.Should().BeGreaterThanOrEqualTo(forecast.AgentSecondsMedian);

        // Four lanes × 300s each.
        forecast.AgentSecondsMedian.Should().Be(SecondsPerExecution * DefaultLanes.Length);
        forecast.Assumptions.Lanes.Should().HaveCount(DefaultLanes.Length);
    }

    [Fact]
    public async Task NoHistoryInTheOrg_FallsBackToPlatformSamples()
    {
        // A brand-new org must still see a number on its first task (user rule, 2026-09-04).
        // The samples are another tenant's durations — labelled "platform" so the UI can say
        // whose history it is.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var usage = scope.ServiceProvider.GetRequiredService<IUsageRecorder>();
        var forecaster = scope.ServiceProvider.GetRequiredService<ITaskForecaster>();

        // The "other tenant" is the test org (usage rows need its billing account); the org
        // being forecast is brand new and owns nothing.
        await SeedHistoryAsync(db, usage, TestCurrentUser.TestOrgId, Ulid.NewUlid(), taskCount: 6);

        var (newOrg, newProject) = (Ulid.NewUlid(), Ulid.NewUlid());
        var forecast = await forecaster.ForecastDraftAsync(Input(newOrg, newProject));

        forecast.Should().NotBeNull("the platform has history even though this org has none");
        forecast!.Assumptions.BucketLevel.Should().Be("platform");
        forecast.AgentSecondsMedian.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task EveryTask_GetsTimeButNoCost()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var usage = scope.ServiceProvider.GetRequiredService<IUsageRecorder>();
        var forecaster = scope.ServiceProvider.GetRequiredService<ITaskForecaster>();
        var (orgId, projectId) = (TestCurrentUser.TestOrgId, Ulid.NewUlid());

        await SeedHistoryAsync(db, usage, orgId, projectId, taskCount: 6);

        var forecast = await forecaster.ForecastDraftAsync(Input(orgId, projectId));

        forecast.Should().NotBeNull();
        // Every run is on the developer's own subscription, so a cost figure would be invented.
        // Time is still real.
        forecast!.CostMedian.Should().BeNull();
        forecast.AgentSecondsMedian.Should().BeGreaterThan(0);
        forecast.Assumptions.CostForecastSuppressed.Should().BeTrue();
    }

    [Fact]
    public async Task SkippingCodeReview_RemovesThatLaneFromTheEstimate()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var usage = scope.ServiceProvider.GetRequiredService<IUsageRecorder>();
        var forecaster = scope.ServiceProvider.GetRequiredService<ITaskForecaster>();
        var (orgId, projectId) = (TestCurrentUser.TestOrgId, Ulid.NewUlid());

        await SeedHistoryAsync(db, usage, orgId, projectId, taskCount: 6);

        var full = await forecaster.ForecastDraftAsync(Input(orgId, projectId));
        var trimmed = await forecaster.ForecastDraftAsync(Input(orgId, projectId, skipCodeReview: true));

        trimmed!.Assumptions.Lanes.Should().HaveCount(full!.Assumptions.Lanes.Count - 1);
        trimmed.Assumptions.Lanes.Should().NotContain(l => l.Lane == nameof(Lane.CodeReview));
        trimmed.AgentSecondsMedian.Should().BeLessThan(full.AgentSecondsMedian);
    }

    [Fact]
    public async Task AgentTime_ExcludesTimeParkedWaitingOnAHuman()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var usage = scope.ServiceProvider.GetRequiredService<IUsageRecorder>();
        var forecaster = scope.ServiceProvider.GetRequiredService<ITaskForecaster>();
        var (orgId, projectId) = (TestCurrentUser.TestOrgId, Ulid.NewUlid());

        await SeedHistoryAsync(db, usage, orgId, projectId, taskCount: 6);

        // Each seeded task spans a full day wall-clock (CompletedAt is a day after creation)
        // but only 4 × 300s of actual execution. The forecast must report the latter.
        var forecast = await forecaster.ForecastDraftAsync(Input(orgId, projectId));

        forecast!.AgentSecondsMedian.Should().Be(SecondsPerExecution * DefaultLanes.Length);
        forecast.AgentSecondsMedian.Should().BeLessThan((int)TimeSpan.FromDays(1).TotalSeconds);
    }

    [Fact]
    public async Task NoHistoryAtAll_ReturnsNull()
    {
        using var scope = factory.Services.CreateScope();
        var forecaster = scope.ServiceProvider.GetRequiredService<ITaskForecaster>();

        // Sampled per task type: Refactor is seeded by no test past the floor, so even the
        // platform-wide fallback has nothing usable. (A Feature task in a new org DOES get a
        // forecast now — see NoHistoryInTheOrg_FallsBackToPlatformSamples.)
        var forecast = await forecaster.ForecastDraftAsync(
            Input(TestCurrentUser.TestOrgId, Ulid.NewUlid(), type: TaskType.Refactor));

        // The empty state a brand-new platform starts in.
        forecast.Should().BeNull();
    }

    /// <summary>
    /// The roadmap's workload line needs to say whose history the number comes from, so the
    /// milestone rollup carries the loosest bucket any of its tasks needed. Here the project is
    /// new but the org has history elsewhere: "org".
    /// </summary>
    [Fact]
    public async Task MilestoneForecast_ReportsTheLoosestBucketItsTasksNeeded()
    {
        using (var seed = factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<TasksDbContext>();
            var usage = seed.ServiceProvider.GetRequiredService<IUsageRecorder>();
            await SeedHistoryAsync(db, usage, TestCurrentUser.TestOrgId, Ulid.NewUlid(), taskCount: 6);
        }

        var client = factory.CreateClient();
        var projectResp = await client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("Forecast Rollup", "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);
        var repoResp = await client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
            new AddProjectRepoRequest("rollup-repo", "https://github.com/test/rollup.git", "main", null, null));
        var repo = await repoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        var milestoneResp = await client.PostAsJsonAsync($"/api/projects/{project.Id}/milestones",
            new CreateMilestoneRequest("First live version", "A real page."));
        var milestone = await milestoneResp.Content.ReadFromJsonAsync<MilestoneResponse>(TestJsonOptions.Default);

        await client.PostAsJsonAsync($"/api/projects/{project.Id}/tasks",
            new CreateTaskRequest(repo!.Id, "The one real screen", "Description", "Medium",
                MilestoneId: milestone!.Id));

        var forecast = await client.GetFromJsonAsync<MilestoneForecastResponse>(
            $"/api/projects/{project.Id}/milestones/{milestone.Id}/forecast", TestJsonOptions.Default);

        forecast.Should().NotBeNull();
        forecast!.TasksCovered.Should().Be(1);
        forecast.CostMedian.Should().BeNull("every task runs on the developer's own subscription");
        forecast.BucketLevel.Should().Be("org", "this project has no history, the org does");
    }
}

