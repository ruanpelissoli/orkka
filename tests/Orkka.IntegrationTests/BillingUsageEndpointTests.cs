namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Usage.Contracts;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The usage dashboard's three reads, exercised with the date range the UI actually sends
/// (local-day boundaries converted to UTC, e.g. ...T02:59:59.000Z for UTC-3).
/// </summary>
[Collection("Integration")]
public sealed class BillingUsageEndpointTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private static string Range()
    {
        var to = new DateTimeOffset(2026, 8, 1, 2, 59, 59, TimeSpan.Zero);
        var from = to.AddDays(-30);
        return $"?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";
    }

    /// <summary>A window around the wall clock, so rows seeded by the test are actually in it.</summary>
    private static string SeededRange()
    {
        var now = DateTimeOffset.UtcNow;
        var from = now.AddDays(-1);
        var to = now.AddDays(1);
        return $"?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";
    }

    private async Task SeedUsageAsync()
    {
        using var scope = factory.Services.CreateScope();
        var usage = scope.ServiceProvider.GetRequiredService<IUsageRecorder>();

        // One row attributed to a task and one without — the null TaskId is what the
        // distinct-task count has to cope with.
        await usage.RecordUsageAsync(new RecordUsageCommand(
            TestCurrentUser.TestOrgId, Ulid.NewUlid(), Ulid.NewUlid(), null, null,
            "claude-sonnet-5", 1000, 100));
        await usage.RecordUsageAsync(new RecordUsageCommand(
            TestCurrentUser.TestOrgId, Ulid.NewUlid(), null, null, null,
            "claude-sonnet-5", 1000, 100));
    }

    [Fact]
    public async Task UsageSummary_WithDateRange_DoesNotFail()
    {
        await SeedUsageAsync();

        var response = await _client.GetAsync($"/api/usage/summary{Range()}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DailyUsage_WithDateRange_DoesNotFail()
    {
        await SeedUsageAsync();

        var response = await _client.GetAsync($"/api/usage/daily{Range()}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UsageBreakdown_ByTask_LinksRealTasksAndBucketsTheRest()
    {
        // A real task the row must link to, and taskless spend (chat, briefs) that must
        // still appear — dropped rows would make the tab disagree with the summary card.
        Ulid taskId;
        Ulid projectId;
        int taskNumber;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider
                .GetRequiredService<Orkka.Modules.Tasks.Infrastructure.TasksDbContext>();
            projectId = Ulid.NewUlid();
            var task = new Orkka.Modules.Tasks.Domain.TaskEntity
            {
                Id = Ulid.NewUlid(),
                OrgId = TestCurrentUser.TestOrgId,
                ProjectId = projectId,
                Number = 7,
                Title = "Clinic & staff data model",
            };
            db.Tasks.Add(task);
            await db.SaveChangesAsync();
            taskId = task.Id;
            taskNumber = task.Number;

            var usage = scope.ServiceProvider.GetRequiredService<IUsageRecorder>();
            await usage.RecordUsageAsync(new RecordUsageCommand(
                TestCurrentUser.TestOrgId, projectId, taskId, null, null,
                "claude-sonnet-5", 5000, 500));
            await usage.RecordUsageAsync(new RecordUsageCommand(
                TestCurrentUser.TestOrgId, projectId, null, null, null,
                "claude-sonnet-5", 1000, 100));
        }

        var response = await _client.GetAsync($"/api/usage/breakdown{SeededRange()}&groupBy=task");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<UsageBreakdownProbe>(TestJsonOptions.Default);
        var taskRow = body!.Items.SingleOrDefault(i => i.GroupKey == taskId.ToString());
        taskRow.Should().NotBeNull("the task's spend must be a row the reader can open");
        taskRow!.GroupName.Should().Be("Clinic & staff data model");
        taskRow.LinkProjectId.Should().Be(projectId.ToString());
        taskRow.LinkTaskNumber.Should().Be(taskNumber);

        // Taskless spend collapses into a bucket rather than vanishing.
        body.Items.Should().Contain(i => i.GroupKey == "none" && i.LinkProjectId == null);
    }

    private sealed record UsageBreakdownProbe(List<UsageBreakdownProbeItem> Items);

    private sealed record UsageBreakdownProbeItem(
        string GroupKey, string GroupName, decimal Credits,
        string? LinkProjectId, int? LinkTaskNumber);

    [Theory]
    [InlineData("project")]
    [InlineData("agent")]
    [InlineData("model")]
    [InlineData("member")]
    [InlineData("task")]
    public async Task UsageBreakdown_WithDateRange_DoesNotFail(string groupBy)
    {
        await SeedUsageAsync();

        var response = await _client.GetAsync(
            $"/api/usage/breakdown?groupBy={groupBy}&{Range()[1..]}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Every grouping must report the cache buckets, not just input/output. This shipped broken:
    /// the endpoint summed input+output only, so a run whose cost was ~90% cached prefix showed a
    /// token count near zero beside a real dollar figure and read as an overcharge. A 200-only
    /// assertion cannot catch that, which is why this one reads the numbers.
    /// </summary>
    [Theory]
    [InlineData("project")]
    [InlineData("agent")]
    [InlineData("model")]
    [InlineData("member")]
    public async Task UsageBreakdown_ReportsCacheTokens(string groupBy)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var usage = scope.ServiceProvider.GetRequiredService<IUsageRecorder>();
            // Shaped like a real agentic run: the cached prefix dwarfs fresh input.
            await usage.RecordUsageAsync(new RecordUsageCommand(
                TestCurrentUser.TestOrgId, Ulid.NewUlid(), Ulid.NewUlid(), null, null,
                "claude-sonnet-5", InputTokens: 800, OutputTokens: 70_000,
                CacheReadTokens: 5_900_000, CacheWriteTokens: 100_000));
        }

        // Deliberately NOT Range(): that one ends at a fixed 2026-08-01 to pin the UI's boundary
        // format, and rows are stamped with the wall clock, so anything seeded today falls
        // outside it. Harmless for a 200-only assertion, fatal for one that reads the numbers.
        var response = await _client.GetAsync(
            $"/api/usage/breakdown?groupBy={groupBy}&{SeededRange()[1..]}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<BreakdownBody>();
        body.Should().NotBeNull();

        var totals = body!.Items.Aggregate(
            (Read: 0L, Write: 0L, Total: 0L),
            (acc, i) => (acc.Read + i.CacheReadTokens, acc.Write + i.CacheWriteTokens, acc.Total + i.TotalTokens));

        totals.Read.Should().BeGreaterThanOrEqualTo(5_900_000);
        totals.Write.Should().BeGreaterThanOrEqualTo(100_000);

        // The headline number has to include the cache, or the UI is back to showing ~1%.
        totals.Total.Should().BeGreaterThanOrEqualTo(6_000_000);
    }

    /// <summary>The summary card's token figure sits beside its cost figure, so it has to be
    /// built from the same buckets the cost was.</summary>
    [Fact]
    public async Task UsageSummary_ReportsCacheTokens()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var usage = scope.ServiceProvider.GetRequiredService<IUsageRecorder>();
            await usage.RecordUsageAsync(new RecordUsageCommand(
                TestCurrentUser.TestOrgId, Ulid.NewUlid(), Ulid.NewUlid(), null, null,
                "claude-sonnet-5", InputTokens: 800, OutputTokens: 70_000,
                CacheReadTokens: 5_900_000, CacheWriteTokens: 100_000));
        }

        var response = await _client.GetAsync($"/api/usage/summary{SeededRange()}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<SummaryBody>();
        body.Should().NotBeNull();
        body!.TotalCacheReadTokens.Should().BeGreaterThanOrEqualTo(5_900_000);
        body.TotalCacheWriteTokens.Should().BeGreaterThanOrEqualTo(100_000);
        body.TotalTokens.Should().BeGreaterThanOrEqualTo(6_000_000);
    }

    private sealed record SummaryBody(
        decimal TotalCost,
        long TotalInputTokens,
        long TotalOutputTokens,
        long TotalCacheReadTokens,
        long TotalCacheWriteTokens,
        long TotalTokens);

    private sealed record BreakdownBody(List<BreakdownRow> Items);

    private sealed record BreakdownRow(
        decimal Cost,
        long InputTokens,
        long OutputTokens,
        long CacheReadTokens,
        long CacheWriteTokens,
        long TotalTokens);
}
