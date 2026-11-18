namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Usage.Contracts;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Features.BoardMetrics;
using Orkka.Modules.Tasks.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// The numbers above the board. These pin the decisions the strip depends on being true —
/// that a lane average is per (task, lane) rather than per run, that human lanes are absent
/// rather than zero, and that a trend needs enough history to mean anything.
///
/// The seed helpers are deliberately a copy of the ones in
/// <see cref="ProjectExecutionsEndpointTests"/> rather than a shared fixture: a change made for
/// the Activity page must not be able to move these assertions silently.
/// </summary>
[Collection("Integration")]
public sealed class BoardMetricsEndpointTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();
    private static int _nextTaskNumber = 9000;

    private async Task<Ulid> CreateProjectAsync(string name)
    {
        var resp = await _client.PostAsJsonAsync("/api/projects", new CreateProjectRequest(name, "desc"));
        var project = await resp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);
        return project!.Id;
    }

    private static async Task<Ulid> SeedTaskAsync(
        TasksDbContext db, Ulid orgId, Ulid projectId, Action<TaskEntity>? configureTask = null)
    {
        var task = new TaskEntity
        {
            Id = Ulid.NewUlid(),
            OrgId = orgId,
            ProjectId = projectId,
            Number = Interlocked.Increment(ref _nextTaskNumber),
            Title = "Seeded",
            Status = TaskStatus.Completed,
            CurrentLane = Lane.Done,
        };
        configureTask?.Invoke(task);
        db.Tasks.Add(task);
        await db.SaveChangesAsync();
        return task.Id;
    }

    private static async Task<Ulid> SeedRunAsync(
        TasksDbContext db, Ulid orgId, Ulid taskId, Ulid agentId,
        ExecutionStatus status, DateTimeOffset startedAt, int durationSeconds,
        Lane lane = Lane.Developing)
    {
        var execution = new TaskExecution
        {
            Id = Ulid.NewUlid(),
            OrgId = orgId,
            TaskId = taskId,
            AgentId = agentId,
            Lane = lane,
            Status = status,
            StartedAt = startedAt,
            CompletedAt = status == ExecutionStatus.Running ? null : startedAt.AddSeconds(durationSeconds),
        };
        db.TaskExecutions.Add(execution);
        await db.SaveChangesAsync();
        return execution.Id;
    }

    /// <summary>A task that merged inside the window — what "shipped" counts.</summary>
    private static Action<TaskEntity> Merged(DateTimeOffset completedAt) => t =>
    {
        t.IsMerged = true;
        t.CompletedAt = completedAt;
    };

    private Task<BoardMetricsResponse?> GetAsync(Ulid projectId, string? window = null) =>
        _client.GetFromJsonAsync<BoardMetricsResponse>(
            $"/api/projects/{projectId}/board-metrics{(window is null ? "" : $"?window={window}")}",
            TestJsonOptions.Default);

    [Fact]
    public async Task Strip_CountsShippedAndSpendInsideTheWindowOnly()
    {
        var projectId = await CreateProjectAsync("Board Metrics Shipped");
        var otherProject = await CreateProjectAsync("Board Metrics Other");
        var orgId = TestCurrentUser.TestOrgId;
        var agentId = Ulid.NewUlid();
        var now = DateTimeOffset.UtcNow;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
            var usage = scope.ServiceProvider.GetRequiredService<IUsageRecorder>();

            var inWindow = await SeedTaskAsync(db, orgId, projectId, Merged(now.AddHours(-2)));
            var run = await SeedRunAsync(db, orgId, inWindow, agentId, ExecutionStatus.Completed, now.AddHours(-3), 120);
            await usage.RecordUsageAsync(new RecordUsageCommand(
                orgId, projectId, null, agentId, run, "claude-sonnet-5", 10_000, 1_000));

            // Merged 20 days ago: outside the default 7d window, and outside its predecessor too.
            await SeedTaskAsync(db, orgId, projectId, Merged(now.AddDays(-20)));

            // Another project in the same org must never bleed in.
            var elsewhere = await SeedTaskAsync(db, orgId, otherProject, Merged(now.AddHours(-1)));
            await SeedRunAsync(db, orgId, elsewhere, agentId, ExecutionStatus.Completed, now.AddHours(-1), 500);
        }

        var body = await GetAsync(projectId);

        body.Should().NotBeNull();
        body!.Window.Should().Be("7d");
        body.Current.Shipped.Should().Be(1);
        body.Current.Runs.Should().Be(1);
        body.Current.Tokens.Should().Be(11_000);
        body.Current.AgentSpend.Should().BeGreaterThan(0);
        // The shipped task's one run took 120s, and agent time is summed over its whole life.
        body.Current.AgentSecondsPerShippedTask.Should().BeApproximately(120, 0.5);
    }

    [Fact]
    public async Task Strip_SeparatesWastedSpendFromAgentSpend()
    {
        var projectId = await CreateProjectAsync("Board Metrics Waste");
        var orgId = TestCurrentUser.TestOrgId;
        var agentId = Ulid.NewUlid();
        var now = DateTimeOffset.UtcNow;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
            var usage = scope.ServiceProvider.GetRequiredService<IUsageRecorder>();

            var taskId = await SeedTaskAsync(db, orgId, projectId);
            var good = await SeedRunAsync(db, orgId, taskId, agentId, ExecutionStatus.Completed, now.AddMinutes(-30), 60);
            var bad = await SeedRunAsync(db, orgId, taskId, agentId, ExecutionStatus.Failed, now.AddMinutes(-20), 30);
            var abandoned = await SeedRunAsync(db, orgId, taskId, agentId, ExecutionStatus.Cancelled, now.AddMinutes(-10), 10);

            await usage.RecordUsageAsync(new RecordUsageCommand(
                orgId, projectId, null, agentId, good, "claude-sonnet-5", 20_000, 2_000));
            await usage.RecordUsageAsync(new RecordUsageCommand(
                orgId, projectId, null, agentId, bad, "claude-sonnet-5", 5_000, 500));
            await usage.RecordUsageAsync(new RecordUsageCommand(
                orgId, projectId, null, agentId, abandoned, "claude-sonnet-5", 1_000, 100));
        }

        var body = await GetAsync(projectId);

        body!.Current.WastedSpend.Should().BeGreaterThan(0);
        body.Current.WastedSpend.Should().BeLessThan(body.Current.AgentSpend,
            "the successful run's spend is not waste");
    }

    [Fact]
    public async Task Strip_FailureRateExcludesRunsStillInFlight()
    {
        var projectId = await CreateProjectAsync("Board Metrics Failure Rate");
        var orgId = TestCurrentUser.TestOrgId;
        var agentId = Ulid.NewUlid();
        var now = DateTimeOffset.UtcNow;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
            var taskId = await SeedTaskAsync(db, orgId, projectId);
            await SeedRunAsync(db, orgId, taskId, agentId, ExecutionStatus.Completed, now.AddMinutes(-40), 60);
            await SeedRunAsync(db, orgId, taskId, agentId, ExecutionStatus.Completed, now.AddMinutes(-30), 60);
            await SeedRunAsync(db, orgId, taskId, agentId, ExecutionStatus.Failed, now.AddMinutes(-20), 30);
            await SeedRunAsync(db, orgId, taskId, agentId, ExecutionStatus.Running, now.AddMinutes(-1), 0);
        }

        var body = await GetAsync(projectId);

        body!.Current.Runs.Should().Be(4);
        body.Current.Running.Should().Be(1);
        // 1 failure over 3 finished runs — the in-flight one is neither a success nor a failure.
        body.Current.FailureRate.Should().BeApproximately(1d / 3d, 0.001);
    }

    [Fact]
    public async Task Lanes_AverageOverTaskLaneGroups_NotOverRuns()
    {
        var projectId = await CreateProjectAsync("Board Metrics Lane Grouping");
        var orgId = TestCurrentUser.TestOrgId;
        var agentId = Ulid.NewUlid();
        var now = DateTimeOffset.UtcNow;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
            // One task bounced through review three times. That is one experience of the lane
            // costing 60s, not three experiences averaging 20s.
            var taskId = await SeedTaskAsync(db, orgId, projectId);
            await SeedRunAsync(db, orgId, taskId, agentId, ExecutionStatus.Completed, now.AddHours(-3), 10, Lane.CodeReview);
            await SeedRunAsync(db, orgId, taskId, agentId, ExecutionStatus.Completed, now.AddHours(-2), 20, Lane.CodeReview);
            await SeedRunAsync(db, orgId, taskId, agentId, ExecutionStatus.Completed, now.AddHours(-1), 30, Lane.CodeReview);
        }

        var body = await GetAsync(projectId);

        body!.Lanes.Should().ContainKey("code_review", "lane keys are the board's own, not enum names");
        var lane = body.Lanes["code_review"];
        lane.AvgAgentSeconds.Should().BeApproximately(60, 0.5);
        lane.Runs.Should().Be(3, "the denominator behind the average is never hidden");
        lane.Tasks.Should().Be(1);
    }

    [Fact]
    public async Task Lanes_OmitLanesNoAgentEverRunsIn()
    {
        var projectId = await CreateProjectAsync("Board Metrics Human Lanes");
        var orgId = TestCurrentUser.TestOrgId;
        var agentId = Ulid.NewUlid();
        var now = DateTimeOffset.UtcNow;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
            var taskId = await SeedTaskAsync(db, orgId, projectId);
            await SeedRunAsync(db, orgId, taskId, agentId, ExecutionStatus.Completed, now.AddHours(-1), 45, Lane.Developing);
        }

        var body = await GetAsync(projectId);

        body!.Lanes.Should().ContainKey("developing");
        // Absent rather than zero: a human lane has no agent time, and showing 0m would read as
        // "instant" instead of "not a thing agents do".
        body.Lanes.Should().NotContainKey("drafting");
        body.Lanes.Should().NotContainKey("plan_ready");
        body.Lanes.Should().NotContainKey("pending_questions");
    }

    [Fact]
    public async Task Lanes_CarryTokensAndFailureRateForTheirOwnRuns()
    {
        var projectId = await CreateProjectAsync("Board Metrics Lane Cost");
        var orgId = TestCurrentUser.TestOrgId;
        var agentId = Ulid.NewUlid();
        var now = DateTimeOffset.UtcNow;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
            var usage = scope.ServiceProvider.GetRequiredService<IUsageRecorder>();

            var reviewed = await SeedTaskAsync(db, orgId, projectId);
            var ok = await SeedRunAsync(db, orgId, reviewed, agentId, ExecutionStatus.Completed, now.AddHours(-3), 60, Lane.CodeReview);
            var bad = await SeedRunAsync(db, orgId, reviewed, agentId, ExecutionStatus.Failed, now.AddHours(-2), 30, Lane.CodeReview);
            await usage.RecordUsageAsync(new RecordUsageCommand(
                orgId, projectId, null, agentId, ok, "claude-sonnet-5", 5_000, 500));
            await usage.RecordUsageAsync(new RecordUsageCommand(
                orgId, projectId, null, agentId, bad, "claude-sonnet-5", 2_000, 100));

            // A different lane's spend must not bleed across — each column reports its own.
            var built = await SeedTaskAsync(db, orgId, projectId);
            var dev = await SeedRunAsync(db, orgId, built, agentId, ExecutionStatus.Completed, now.AddHours(-1), 90, Lane.Developing);
            await usage.RecordUsageAsync(new RecordUsageCommand(
                orgId, projectId, null, agentId, dev, "claude-sonnet-5", 1_000, 100));
        }

        var body = await GetAsync(projectId);

        var review = body!.Lanes["code_review"];
        review.Tokens.Should().Be(7_600, "the lane's own runs, both of them, failed included");
        review.FinishedRuns.Should().Be(2);
        review.FailureRate.Should().BeApproximately(0.5, 0.001);
        review.TotalAgentSeconds.Should().BeApproximately(90, 0.5, "one task, two spans");

        var developing = body.Lanes["developing"];
        developing.Tokens.Should().Be(1_100);
        developing.FailureRate.Should().Be(0, "nothing failed here — 0% is a claim, not an absence");
    }

    [Fact]
    public async Task Lanes_ReportNoFailureRateUntilARunFinishes()
    {
        var projectId = await CreateProjectAsync("Board Metrics Lane In Flight");
        var orgId = TestCurrentUser.TestOrgId;
        var agentId = Ulid.NewUlid();
        var now = DateTimeOffset.UtcNow;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
            var taskId = await SeedTaskAsync(db, orgId, projectId);
            await SeedRunAsync(db, orgId, taskId, agentId, ExecutionStatus.Running, now.AddMinutes(-5), 0, Lane.Testing);
        }

        var body = await GetAsync(projectId);

        // Null, not zero. A lane whose only run is still going has no rate yet, and printing 0%
        // would claim it is clean.
        body!.Lanes["testing"].FailureRate.Should().BeNull();
        body.Lanes["testing"].Runs.Should().Be(0, "nothing has completed, so nothing is averaged");
    }

    [Fact]
    public async Task Lanes_ExcludeInteractiveSessions()
    {
        var projectId = await CreateProjectAsync("Board Metrics Interactive");
        var orgId = TestCurrentUser.TestOrgId;
        var agentId = Ulid.NewUlid();
        var now = DateTimeOffset.UtcNow;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
            // An interactive session stamps Developing on hours of human-driven work. Counting it
            // would swamp the lane average with something that is not agent time at all.
            var interactive = await SeedTaskAsync(db, orgId, projectId,
                t => t.InteractiveSessionStartedAt = now.AddHours(-5));
            await SeedRunAsync(db, orgId, interactive, agentId, ExecutionStatus.Completed, now.AddHours(-5), 14_400);

            var normal = await SeedTaskAsync(db, orgId, projectId);
            await SeedRunAsync(db, orgId, normal, agentId, ExecutionStatus.Completed, now.AddHours(-1), 60);
        }

        var body = await GetAsync(projectId);

        body!.Lanes["developing"].AvgAgentSeconds.Should().BeApproximately(60, 0.5);
        body.Lanes["developing"].Tasks.Should().Be(1);
    }

    [Fact]
    public async Task Deltas_CompareAgainstThePreviousWindowOfEqualLength()
    {
        var projectId = await CreateProjectAsync("Board Metrics Deltas");
        var orgId = TestCurrentUser.TestOrgId;
        var agentId = Ulid.NewUlid();
        var now = DateTimeOffset.UtcNow;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

            // Five shipped in the last 24h, three in the 24h before it.
            for (var i = 0; i < 5; i++)
            {
                var t = await SeedTaskAsync(db, orgId, projectId, Merged(now.AddHours(-2)));
                await SeedRunAsync(db, orgId, t, agentId, ExecutionStatus.Completed, now.AddHours(-3), 60);
            }

            for (var i = 0; i < 3; i++)
            {
                var t = await SeedTaskAsync(db, orgId, projectId, Merged(now.AddHours(-30)));
                await SeedRunAsync(db, orgId, t, agentId, ExecutionStatus.Completed, now.AddHours(-31), 60);
            }
        }

        var body = await GetAsync(projectId, "24h");

        body!.PreviousFrom.Should().BeCloseTo(body.From.AddHours(-24), TimeSpan.FromSeconds(5));
        body.Current.Shipped.Should().Be(5);
        body.Deltas.Shipped.Previous.Should().Be(3);
        body.Deltas.Shipped.ChangePct.Should().BeApproximately(66.7, 0.5);
        body.Deltas.Shipped.Trend.Should().Be("Improving", "shipping more is better; the API decides that, not the client");
    }

    [Fact]
    public async Task Deltas_AreUnknownBelowTheSampleFloor()
    {
        var projectId = await CreateProjectAsync("Board Metrics Thin History");
        var orgId = TestCurrentUser.TestOrgId;
        var agentId = Ulid.NewUlid();
        var now = DateTimeOffset.UtcNow;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
            var current = await SeedTaskAsync(db, orgId, projectId, Merged(now.AddHours(-2)));
            await SeedRunAsync(db, orgId, current, agentId, ExecutionStatus.Completed, now.AddHours(-3), 60);

            // A single run in the previous window: not enough to call anything a trend.
            var before = await SeedTaskAsync(db, orgId, projectId);
            await SeedRunAsync(db, orgId, before, agentId, ExecutionStatus.Failed, now.AddHours(-30), 30);
        }

        var body = await GetAsync(projectId, "24h");

        body!.Deltas.FailureRate.Trend.Should().Be("Unknown");
        body.Deltas.FailureRate.ChangePct.Should().BeNull();
        body.Deltas.Shipped.Trend.Should().Be("Unknown", "one run of history is noise, not a baseline");
        // The baseline still travels below the floor: "vs 0" is honest, "+100%" would not be.
        body.Deltas.FailureRate.Previous.Should().Be(1, "the one previous run failed");
        body.Deltas.Shipped.Previous.Should().Be(0);
    }

    [Fact]
    public async Task EmptyProject_ReturnsAWellFormedEnvelope()
    {
        var projectId = await CreateProjectAsync("Board Metrics Empty");

        var body = await GetAsync(projectId);

        body.Should().NotBeNull();
        body!.Current.Runs.Should().Be(0);
        body.Current.Shipped.Should().Be(0);
        body.Current.FailureRate.Should().BeNull();
        body.Current.AgentSecondsPerShippedTask.Should().BeNull();
        body.Lanes.Should().BeEmpty();
        body.Deltas.Shipped.Trend.Should().Be("Unknown");
    }

    [Fact]
    public async Task Window_HonoursTheParameter_AndFallsBackOnGarbage()
    {
        var projectId = await CreateProjectAsync("Board Metrics Window");
        var orgId = TestCurrentUser.TestOrgId;
        var agentId = Ulid.NewUlid();
        var now = DateTimeOffset.UtcNow;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
            var old = await SeedTaskAsync(db, orgId, projectId, Merged(now.AddDays(-10)));
            await SeedRunAsync(db, orgId, old, agentId, ExecutionStatus.Completed, now.AddDays(-10), 60);
        }

        var week = await GetAsync(projectId);
        week!.Window.Should().Be("7d");
        week.Current.Shipped.Should().Be(0);

        var month = await GetAsync(projectId, "30d");
        month!.Window.Should().Be("30d");
        month.Current.Shipped.Should().Be(1);

        var garbage = await GetAsync(projectId, "next-tuesday");
        garbage!.Window.Should().Be("7d", "a bad query string must not take the board down");
    }

    [Fact]
    public async Task UnknownProject_Is404()
    {
        var resp = await _client.GetAsync($"/api/projects/{Ulid.NewUlid()}/board-metrics");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
