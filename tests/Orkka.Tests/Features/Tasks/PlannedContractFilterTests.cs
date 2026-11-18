namespace Orkka.Tests.Features.Tasks;

using FluentAssertions;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Infrastructure.Services;

/// <summary>
/// Which of the planner's contracts survive to become cross-repo groups.
///
/// Both filters exist to stop a group of one. A group's merge gate holds every member's pull
/// request until all of them can land, so a "group" with a single task waits forever on a
/// sibling that does not exist — the failure looks exactly like a hung pipeline, and nothing in
/// the product would explain it.
/// </summary>
public sealed class PlannedContractFilterTests
{
    private static DecomposedTask Task(string repo, int? contractKey) =>
        new("t", "d", "Medium", "Feature", null, repo, contractKey);

    private static List<TaskDecomposerService.ContractDto> Contracts(params int[] keys) =>
        [.. keys.Select(k => new TaskDecomposerService.ContractDto { Key = k, Contract = "### Endpoints\nPOST /a" })];

    [Fact]
    public void KeepsAContractSpanningTwoRepos()
    {
        var tasks = new List<DecomposedTask> { Task("api", 1), Task("web", 1) };

        TaskDecomposerService.ToPlannedContracts(Contracts(1), tasks)
            .Should().ContainSingle().Which.Key.Should().Be(1);
    }

    [Fact]
    public void DropsAContractOnlyOneTaskReferences()
    {
        var tasks = new List<DecomposedTask> { Task("api", 1), Task("web", null) };

        // Nothing crosses a boundary, and grouping the one task would gate it on a sibling that
        // will never exist.
        TaskDecomposerService.ToPlannedContracts(Contracts(1), tasks).Should().BeEmpty();
    }

    [Fact]
    public void DropsAContractWhoseTasksShareOneRepo()
    {
        var tasks = new List<DecomposedTask> { Task("api", 1), Task("api", 1) };

        // Two tasks in one repo share a worktree and a branch — ordinary dependent tasks the
        // model mislabelled, not two halves of a cross-repo change.
        TaskDecomposerService.ToPlannedContracts(Contracts(1), tasks).Should().BeEmpty();
    }

    [Fact]
    public void DropsAContractNoTaskReferences()
    {
        var tasks = new List<DecomposedTask> { Task("api", null), Task("web", null) };

        TaskDecomposerService.ToPlannedContracts(Contracts(7), tasks).Should().BeEmpty();
    }

    [Fact]
    public void KeepsOnlyTheUsableKeys_WhenThePlanMixesBoth()
    {
        var tasks = new List<DecomposedTask>
        {
            Task("api", 1), Task("web", 1),
            Task("api", 2),
        };

        TaskDecomposerService.ToPlannedContracts(Contracts(1, 2), tasks)
            .Select(c => c.Key).Should().Equal([1]);
    }

    [Fact]
    public void NoContracts_IsTheOrdinaryPlan()
    {
        var tasks = new List<DecomposedTask> { Task("api", null) };

        TaskDecomposerService.ToPlannedContracts(null, tasks).Should().BeEmpty();
    }
}
