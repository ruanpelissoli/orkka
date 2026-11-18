namespace Orkka.Tests.Features.Projects;

using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using FluentValidation.TestHelper;
using Orkka.Modules.Execution.Orchestration;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Projects.Features.Projects;

/// <summary>
/// The task spend limit is in US dollars of ESTIMATED LIST-PRICE spend (Orkka bills nothing):
/// <c>taskSpendLimitUsd</c> on the wire and in the stored settings, bounds in dollars, and the
/// checkpoint text in culture-invariant dollars whatever the machine's locale.
/// </summary>
public sealed class TaskSpendLimitUsdTests
{
    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void TheSettingsCarryUsd_NotCredits()
    {
        var json = JsonSerializer.Serialize(new ProjectSettingsDto(MaxParallelTasks: 3, SameRepoParallelism: false, TaskSpendLimitEnabled: true, TaskSpendLimitUsd: 2.5m), Camel);

        json.Should().Contain("\"taskSpendLimitUsd\":2.5").And.NotContainEquivalentOf("credits");
        new ProjectSettingsDto(MaxParallelTasks: 3, SameRepoParallelism: false).TaskSpendLimitUsd.Should().Be(ProjectSettingsDto.DefaultTaskSpendLimitUsd).And.Be(5m);
    }

    [Fact]
    public void TheEffectiveLimit_IsNullWhenOff_AndClampedInDollarsWhenOn()
    {
        new ProjectSettingsDto(MaxParallelTasks: 3, SameRepoParallelism: false, TaskSpendLimitUsd: 3m).TaskSpendLimit.Should().BeNull();
        new ProjectSettingsDto(MaxParallelTasks: 3, SameRepoParallelism: false, TaskSpendLimitEnabled: true, TaskSpendLimitUsd: 3m).TaskSpendLimit.Should().Be(3m);
        new ProjectSettingsDto(MaxParallelTasks: 3, SameRepoParallelism: false, TaskSpendLimitEnabled: true, TaskSpendLimitUsd: 0.01m).TaskSpendLimit.Should().Be(0.10m);
        new ProjectSettingsDto(MaxParallelTasks: 3, SameRepoParallelism: false, TaskSpendLimitEnabled: true, TaskSpendLimitUsd: 1_000_000m).TaskSpendLimit.Should().Be(10_000m);
    }

    [Theory]
    [InlineData(0.05, false)]
    [InlineData(0.10, true)]
    [InlineData(25, true)]
    [InlineData(10_001, false)]
    public void TheValidator_BoundsTheLimitInDollars(double usd, bool valid)
    {
        var request = new UpdateProjectRequest(null, null,
            Settings: new ProjectSettingsDto(MaxParallelTasks: 3, SameRepoParallelism: false, TaskSpendLimitEnabled: true, TaskSpendLimitUsd: (decimal)usd));

        var result = new UpdateProjectValidator().TestValidate(request);

        if (valid)
            result.ShouldNotHaveAnyValidationErrors();
        else
            result.ShouldHaveValidationErrorFor(x => x.Settings!.TaskSpendLimitUsd).WithErrorMessage(
                "Task spend limit (estimated list-price spend) must be between US$0.10 and US$10,000.");
    }

    [Fact]
    public void TheCheckpointText_IsInvariantDollars_AndSaysItIsAnEstimate()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("pt-BR");

            var text = TaskOrchestrator.SpendCheckpointText(5.1234m, 5m, 5m, "feature/SHOP-1", beforePhase: true);

            text.Should().Contain("US$5.12").And.Contain("US$5.00 limit").And.Contain("approves another US$5.00")
                .And.Contain("estimated list-price spend").And.Contain("Orkka bills nothing")
                .And.Contain("The next step has not started.").And.NotContainEquivalentOf("credit");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
