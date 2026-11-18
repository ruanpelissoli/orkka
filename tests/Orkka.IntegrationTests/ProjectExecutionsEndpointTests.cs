namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Usage.Contracts;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Features.AgentStats;
using Orkka.Modules.Tasks.Features.Executions;
using Orkka.Modules.Tasks.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// The Activity timeline (project-wide executions) and the Team page's windowed agent
/// stats. Both read <c>tasks.task_executions</c> through the task's project, cost through
/// Billing, and must not leak across projects.
/// </summary>
[Collection("Integration")]
public sealed class ProjectExecutionsEndpointTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();
    private static int _nextTaskNumber = 7000;

    private async Task<Ulid> CreateProjectAsync(string name)
    {
        var resp = await _client.PostAsJsonAsync("/api/projects", new CreateProjectRequest(name, "desc"));
        var project = await resp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);
        return project!.Id;
    }

    private static async Task<(Ulid TaskId, Ulid ExecutionId)> SeedExecutionAsync(
        TasksDbContext db, Ulid orgId, Ulid projectId, Ulid agentId,
        ExecutionStatus status, DateTimeOffset startedAt, int durationSeconds,
        string? error = null, Lane lane = Lane.Developing, Action<TaskEntity>? configureTask = null)
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

        var execution = new TaskExecution
        {
            Id = Ulid.NewUlid(),
            OrgId = orgId,
            TaskId = task.Id,
            AgentId = agentId,
            Lane = lane,
            Status = status,
            StartedAt = startedAt,
            CompletedAt = status == ExecutionStatus.Running ? null : startedAt.AddSeconds(durationSeconds),
            ErrorMessage = error,
        };
        db.TaskExecutions.Add(execution);
        await db.SaveChangesAsync();
        return (task.Id, execution.Id);
    }

    [Fact]
    public async Task ListExecutions_ReturnsBarsInWindow_WithKeysAndCost()
    {
        var projectId = await CreateProjectAsync("Activity List");
        var otherProject = await CreateProjectAsync("Activity Other");
        var orgId = TestCurrentUser.TestOrgId;
        var agentId = Ulid.NewUlid();
        var now = DateTimeOffset.UtcNow;

        Ulid inWindowExecution;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
            var usage = scope.ServiceProvider.GetRequiredService<IUsageRecorder>();

            (_, inWindowExecution) = await SeedExecutionAsync(db, orgId, projectId, agentId,
                ExecutionStatus.Completed, now.AddHours(-2), 120);
            await usage.RecordUsageAsync(new RecordUsageCommand(
                orgId, projectId, null, agentId, inWindowExecution, "claude-sonnet-5", 10_000, 1_000));

            // Two days old: outside the default 24h window.
            await SeedExecutionAsync(db, orgId, projectId, agentId,
                ExecutionStatus.Failed, now.AddDays(-2), 60, error: "old failure");
            // Another project, same org: must never appear.
            await SeedExecutionAsync(db, orgId, otherProject, agentId,
                ExecutionStatus.Completed, now.AddHours(-1), 30);
        }

        var body = await _client.GetFromJsonAsync<ProjectExecutionsResponse>(
            $"/api/projects/{projectId}/executions", TestJsonOptions.Default);

        body.Should().NotBeNull();
        body!.Truncated.Should().BeFalse();
        body.Executions.Should().ContainSingle();
        var row = body.Executions[0];
        row.ExecutionId.Should().Be(inWindowExecution);
        row.TaskKey.Should().MatchRegex(@"^[A-Z0-9]+-\d+$");
        row.Status.Should().Be("Completed");
        row.Lane.Should().Be("Developing");
        row.DurationSeconds.Should().Be(120);
        row.Cost.Should().BeGreaterThan(0, "usage rows were recorded against this execution");
    }

    [Fact]
    public async Task ListExecutions_HonoursWindowAndStatusFilter()
    {
        var projectId = await CreateProjectAsync("Activity Filters");
        var orgId = TestCurrentUser.TestOrgId;
        var agentId = Ulid.NewUlid();
        var now = DateTimeOffset.UtcNow;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
            await SeedExecutionAsync(db, orgId, projectId, agentId, ExecutionStatus.Completed, now.AddHours(-3), 10);
            await SeedExecutionAsync(db, orgId, projectId, agentId, ExecutionStatus.Failed, now.AddDays(-3), 10, "boom");
            await SeedExecutionAsync(db, orgId, projectId, agentId, ExecutionStatus.Running, now.AddMinutes(-5), 0);
        }

        var from = Uri.EscapeDataString(now.AddDays(-7).ToString("O"));

        var all = await _client.GetFromJsonAsync<ProjectExecutionsResponse>(
            $"/api/projects/{projectId}/executions?from={from}", TestJsonOptions.Default);
        all!.Executions.Should().HaveCount(3);
        all.Executions.Should().BeInDescendingOrder(e => e.StartedAt);
        all.Executions.Single(e => e.Status == "Running").CompletedAt.Should().BeNull();

        var failedOnly = await _client.GetFromJsonAsync<ProjectExecutionsResponse>(
            $"/api/projects/{projectId}/executions?from={from}&status=failed", TestJsonOptions.Default);
        failedOnly!.Executions.Should().ContainSingle(e => e.ErrorMessage == "boom");

        var bad = await _client.GetAsync($"/api/projects/{projectId}/executions?status=nope");
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ListExecutions_FlagsTruncationAtTheLimit()
    {
        var projectId = await CreateProjectAsync("Activity Truncate");
        var orgId = TestCurrentUser.TestOrgId;
        var agentId = Ulid.NewUlid();
        var now = DateTimeOffset.UtcNow;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
            for (var i = 0; i < 3; i++)
                await SeedExecutionAsync(db, orgId, projectId, agentId, ExecutionStatus.Completed, now.AddMinutes(-i - 1), 5);
        }

        var body = await _client.GetFromJsonAsync<ProjectExecutionsResponse>(
            $"/api/projects/{projectId}/executions?limit=2", TestJsonOptions.Default);

        body!.Executions.Should().HaveCount(2);
        body.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task ListExecutions_SummaryCountsTheWholeWindow_NotTheReturnedPage()
    {
        var projectId = await CreateProjectAsync("Activity Summary Truncated");
        var orgId = TestCurrentUser.TestOrgId;
        var agentId = Ulid.NewUlid();
        var now = DateTimeOffset.UtcNow;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
            await SeedExecutionAsync(db, orgId, projectId, agentId, ExecutionStatus.Completed, now.AddMinutes(-10), 60);
            await SeedExecutionAsync(db, orgId, projectId, agentId, ExecutionStatus.Completed, now.AddMinutes(-20), 40);
            await SeedExecutionAsync(db, orgId, projectId, agentId, ExecutionStatus.Failed, now.AddMinutes(-30), 20, "boom");
            await SeedExecutionAsync(db, orgId, projectId, agentId, ExecutionStatus.Running, now.AddMinutes(-1), 0);
        }

        var body = await _client.GetFromJsonAsync<ProjectExecutionsResponse>(
            $"/api/projects/{projectId}/executions?limit=1", TestJsonOptions.Default);

        // The page is capped; the headline is not. This is the whole reason Summary exists.
        body!.Executions.Should().ContainSingle();
        body.Truncated.Should().BeTrue();
        body.Summary.Runs.Should().Be(4);
        body.Summary.Completed.Should().Be(2);
        body.Summary.Failed.Should().Be(1);
        body.Summary.Running.Should().Be(1);
        body.Summary.TasksTouched.Should().Be(4, "each seeded execution gets its own task");
        // Only finished runs contribute: 60 + 40 + 20.
        body.Summary.DurationSeconds.Should().BeApproximately(120, 0.5);
    }

    [Fact]
    public async Task ListExecutions_SummaryIgnoresStatusFilter_ButFollowsAgentFilter()
    {
        var projectId = await CreateProjectAsync("Activity Summary Filters");
        var orgId = TestCurrentUser.TestOrgId;
        var mine = Ulid.NewUlid();
        var theirs = Ulid.NewUlid();
        var now = DateTimeOffset.UtcNow;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
            var usage = scope.ServiceProvider.GetRequiredService<IUsageRecorder>();

            var (_, a) = await SeedExecutionAsync(db, orgId, projectId, mine, ExecutionStatus.Completed, now.AddMinutes(-10), 30);
            await SeedExecutionAsync(db, orgId, projectId, mine, ExecutionStatus.Failed, now.AddMinutes(-20), 15, "boom");
            var (_, c) = await SeedExecutionAsync(db, orgId, projectId, theirs, ExecutionStatus.Completed, now.AddMinutes(-30), 45);

            await usage.RecordUsageAsync(new RecordUsageCommand(
                orgId, projectId, null, mine, a, "claude-sonnet-5", 5_000, 500));
            await usage.RecordUsageAsync(new RecordUsageCommand(
                orgId, projectId, null, theirs, c, "claude-sonnet-5", 7_000, 700));
        }

        // Narrowing to one status must not hide the fact that something else failed.
        var completedOnly = await _client.GetFromJsonAsync<ProjectExecutionsResponse>(
            $"/api/projects/{projectId}/executions?status=completed", TestJsonOptions.Default);
        completedOnly!.Executions.Should().HaveCount(2);
        completedOnly.Summary.Runs.Should().Be(3);
        completedOnly.Summary.Failed.Should().Be(1);

        // Narrowing to one agent is a question about that agent, so the totals follow.
        var oneAgent = await _client.GetFromJsonAsync<ProjectExecutionsResponse>(
            $"/api/projects/{projectId}/executions?agentId={mine}", TestJsonOptions.Default);
        oneAgent!.Summary.Runs.Should().Be(2);
        oneAgent.Summary.Failed.Should().Be(1);
        // Money must describe the same population as the counts, so the other agent's spend goes
        // too — otherwise the panel reads as one agent's runs at the whole team's cost.
        oneAgent.Summary.Agents.Should().ContainSingle(a => a.AgentId == mine);
        oneAgent.Summary.Tokens.Should().Be(5_500);
        completedOnly.Summary.Tokens.Should().Be(13_200, "the status filter never narrows the totals");
    }

    [Fact]
    public async Task ListExecutions_SeparatesWastedSpendFromTotalSpend()
    {
        var projectId = await CreateProjectAsync("Activity Spend");
        var orgId = TestCurrentUser.TestOrgId;
        var agentId = Ulid.NewUlid();
        var now = DateTimeOffset.UtcNow;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
            var usage = scope.ServiceProvider.GetRequiredService<IUsageRecorder>();

            var (_, good) = await SeedExecutionAsync(db, orgId, projectId, agentId,
                ExecutionStatus.Completed, now.AddMinutes(-10), 60);
            await usage.RecordUsageAsync(new RecordUsageCommand(
                orgId, projectId, null, agentId, good, "claude-sonnet-5", 20_000, 2_000));

            var (_, burned) = await SeedExecutionAsync(db, orgId, projectId, agentId,
                ExecutionStatus.Failed, now.AddMinutes(-20), 30, "boom");
            await usage.RecordUsageAsync(new RecordUsageCommand(
                orgId, projectId, null, agentId, burned, "claude-sonnet-5", 10_000, 1_000));
        }

        var body = await _client.GetFromJsonAsync<ProjectExecutionsResponse>(
            $"/api/projects/{projectId}/executions", TestJsonOptions.Default);

        body!.Summary.Cost.Should().BeGreaterThan(0);
        body.Summary.Tokens.Should().Be(33_000, "billed tokens, not the execution row's counter");
        body.Summary.WastedCost.Should().BeGreaterThan(0, "the failed run still cost money");
        body.Summary.WastedCost.Should().BeLessThan(body.Summary.Cost, "the completed run is not waste");
        body.Summary.Agents.Should().ContainSingle(a => a.AgentId == agentId && a.Tokens == 33_000);
    }

    [Fact]
    public async Task ListExecutions_ReturnsHowEachTaskTurnedOut()
    {
        var projectId = await CreateProjectAsync("Activity Outcomes");
        var orgId = TestCurrentUser.TestOrgId;
        var agentId = Ulid.NewUlid();
        var now = DateTimeOffset.UtcNow;

        Ulid mergedTask, stuckTask;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

            mergedTask = (await SeedExecutionAsync(db, orgId, projectId, agentId,
                ExecutionStatus.Completed, now.AddMinutes(-30), 60, configureTask: t =>
                {
                    t.PrUrl = "https://github.com/acme/app/pull/12";
                    t.IsMerged = true;
                    t.ReviewCycleCount = 2;
                })).TaskId;

            stuckTask = (await SeedExecutionAsync(db, orgId, projectId, agentId,
                ExecutionStatus.Failed, now.AddMinutes(-10), 15, "boom", configureTask: t =>
                {
                    t.CurrentLane = Lane.CodeReview;
                    t.Status = TaskStatus.WaitingCodeReview;
                    t.MergeFailureReason = "conflicts in src/app.ts";
                    t.MergeFailureKind = MergeFailureKind.ResolutionExhausted;
                    t.HasAgentWorking = true;
                })).TaskId;
        }

        var body = await _client.GetFromJsonAsync<ProjectExecutionsResponse>(
            $"/api/projects/{projectId}/executions", TestJsonOptions.Default);

        body!.Tasks.Should().HaveCount(2);
        // Most recent activity first, so the cap drops the stalest tasks rather than arbitrary ones.
        body.Tasks[0].TaskId.Should().Be(stuckTask);

        var merged = body.Tasks.Single(t => t.TaskId == mergedTask);
        merged.IsMerged.Should().BeTrue();
        merged.PrUrl.Should().Be("https://github.com/acme/app/pull/12");
        merged.ReviewCycleCount.Should().Be(2);
        merged.CurrentLane.Should().Be("Done");

        var stuck = body.Tasks.Single(t => t.TaskId == stuckTask);
        stuck.MergeFailureReason.Should().Be("conflicts in src/app.ts");
        stuck.MergeFailureKind.Should().Be("ResolutionExhausted");
        stuck.HasAgentWorking.Should().BeTrue();
        stuck.CurrentLane.Should().Be("CodeReview");
        stuck.TaskKey.Should().MatchRegex(@"^[A-Z0-9]+-\d+$");
    }

    [Fact]
    public async Task AgentStats_AggregatesPerAgentOverTheWindow()
    {
        var projectId = await CreateProjectAsync("Agent Stats");
        var orgId = TestCurrentUser.TestOrgId;
        var busy = Ulid.NewUlid();
        var idle = Ulid.NewUlid();
        var now = DateTimeOffset.UtcNow;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
            await SeedExecutionAsync(db, orgId, projectId, busy, ExecutionStatus.Completed, now.AddHours(-1), 100);
            await SeedExecutionAsync(db, orgId, projectId, busy, ExecutionStatus.Completed, now.AddHours(-2), 300);
            await SeedExecutionAsync(db, orgId, projectId, busy, ExecutionStatus.Failed, now.AddHours(-3), 50, "tests red");
            await SeedExecutionAsync(db, orgId, projectId, busy, ExecutionStatus.Running, now.AddMinutes(-1), 0);
            // Ten days ago: outside 7d, inside 30d.
            await SeedExecutionAsync(db, orgId, projectId, idle, ExecutionStatus.Completed, now.AddDays(-10), 10);
        }

        var week = await _client.GetFromJsonAsync<AgentStatsEnvelope>(
            $"/api/projects/{projectId}/agent-stats", TestJsonOptions.Default);

        week!.Window.Should().Be("7d");
        week.Agents.Should().ContainSingle(a => a.AgentId == busy);
        week.Agents.Should().NotContain(a => a.AgentId == idle);
        var stats = week.Agents.Single(a => a.AgentId == busy);
        stats.Runs.Should().Be(4);
        stats.Succeeded.Should().Be(2);
        stats.Failed.Should().Be(1);
        stats.Running.Should().Be(1);
        // (100 + 300 + 50) / 3 finished runs.
        stats.AvgDurationSeconds.Should().BeApproximately(150, 0.5);
        stats.LastError.Should().Be("tests red");

        var month = await _client.GetFromJsonAsync<AgentStatsEnvelope>(
            $"/api/projects/{projectId}/agent-stats?window=30d", TestJsonOptions.Default);
        month!.Window.Should().Be("30d");
        month.Agents.Should().Contain(a => a.AgentId == idle);
    }

    [Fact]
    public async Task UnknownProject_Is404()
    {
        var resp = await _client.GetAsync($"/api/projects/{Ulid.NewUlid()}/executions");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var stats = await _client.GetAsync($"/api/projects/{Ulid.NewUlid()}/agent-stats");
        stats.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
