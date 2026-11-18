namespace Orkka.IntegrationTests;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Orkka.IntegrationTests.Fixtures;
using Orkka.Modules.Usage.Contracts;
using Orkka.Modules.Usage.Domain;
using Orkka.Modules.Usage.Infrastructure;
using Orkka.SharedKernel.Ai;

/// <summary>
/// What the task spend limit and the autopilot cap measure. Runner work is recorded at
/// CostTotal = 0 (Orkka bills nothing), so the recorded cost could never reach a limit; the
/// ESTIMATED list-price spend prices each row at the vendor-reported cost, else at list price.
/// </summary>
[Collection("Integration")]
public sealed class TaskEstimatedSpendTests(OrkkaApiFactory factory)
{
    [Fact]
    public async Task EstimatedSpend_UsesTheVendorsFigure_ElseListPrice_WhileRecordedCostStaysZero()
    {
        var orgId = Ulid.NewUlid();
        var projectId = Ulid.NewUlid();
        var taskId = Ulid.NewUlid();
        var other = Ulid.NewUlid();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
        db.UsageRecords.AddRange(
            Row(orgId, projectId, taskId, "claude-sonnet-5", 10_000, 2_000, vendor: 1.25m),
            Row(orgId, projectId, taskId, "claude-sonnet-5", 100_000, 10_000, vendor: null),
            Row(orgId, projectId, other, "claude-sonnet-5", 5_000, 500, vendor: 0.5m));
        await db.SaveChangesAsync();

        var query = scope.ServiceProvider.GetRequiredService<IUsageQuery>();
        var spend = await query.GetTaskEstimatedSpendAsync(orgId, projectId, [taskId]);
        var recorded = await query.GetTaskCostsAsync(orgId, projectId, [taskId]);

        var rates = scope.ServiceProvider.GetRequiredService<IModelRateProvider>();
        var listPrice = CostCalculator.Calculate("claude-sonnet-5", 100_000, 10_000,
            rates: await rates.TryGetRatesAsync("claude-sonnet-5"),
            cacheRates: await rates.TryGetCacheRatesAsync("claude-sonnet-5"));
        listPrice.Should().BeGreaterThan(0m);
        spend.Should().ContainSingle().Which.Value.Should().Be(1.25m + listPrice);
        recorded.GetValueOrDefault(taskId).Should().Be(0m, "runner work is recorded at $0 — which is why the limit cannot use it");
    }

    private static UsageRecord Row(Ulid org, Ulid project, Ulid task, string model, int input, int output, decimal? vendor) => new()
    {
        Id = Ulid.NewUlid(),
        OrgId = org,
        ProjectId = project,
        TaskId = task,
        Model = model,
        InputTokens = input,
        OutputTokens = output,
        CostTotal = 0m,
        VendorReportedCostUsd = vendor,
    };
}
