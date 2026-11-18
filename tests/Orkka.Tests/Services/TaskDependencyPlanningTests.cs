namespace Orkka.Tests.Services;

using FluentAssertions;
using Orkka.Modules.Tasks.Infrastructure.Services;

/// <summary>
/// The planner's "dependsOn" indices point into the array the model returned, but the array we
/// persist is a filtered, capped copy of it. Getting the remap wrong does not throw — it
/// attaches an ordering edge to whichever task happened to slide into that slot, which then
/// silently serialises or parallelises the wrong work.
/// </summary>
public sealed class TaskDependencyPlanningTests
{
    private static TaskDecomposerService.TaskDto Task(
        string title, params int[] dependsOn) => new()
        {
            Title = title,
            Description = $"Description for {title}",
            Priority = "Medium",
            Type = "Feature",
            DependsOn = dependsOn.Length == 0 ? null : [.. dependsOn],
        };

    [Fact]
    public void KeepsBackwardEdgesWhenNothingIsFiltered()
    {
        var result = TaskDecomposerService.ToDecomposedTasks(
            [Task("a"), Task("b", 0), Task("c", 0, 1)]);

        result.Should().HaveCount(3);
        result[0].DependsOn.Should().BeNull();
        result[1].DependsOn.Should().Equal(0);
        result[2].DependsOn.Should().Equal(0, 1);
    }

    [Fact]
    public void RemapsIndicesAcrossADroppedTask()
    {
        // The middle task is malformed and gets filtered out, so "c" moves from index 2 to 1
        // and its edge onto "a" must follow it rather than staying at the old position.
        var malformed = Task("b", 0);
        malformed.Description = "   ";

        var result = TaskDecomposerService.ToDecomposedTasks(
            [Task("a"), malformed, Task("c", 0)]);

        result.Should().HaveCount(2);
        result[1].Title.Should().Be("c");
        result[1].DependsOn.Should().Equal(0);
    }

    [Fact]
    public void DropsEdgesOntoATaskThatWasFilteredOut()
    {
        var malformed = Task("b");
        malformed.Title = "  ";

        // "c" depended on the dropped task and on nothing else, so it becomes independent
        // rather than inheriting an edge onto whatever took index 1.
        var result = TaskDecomposerService.ToDecomposedTasks(
            [Task("a"), malformed, Task("c", 1)]);

        result.Should().HaveCount(2);
        result[1].Title.Should().Be("c");
        result[1].DependsOn.Should().BeNull();
    }

    [Fact]
    public void DropsForwardAndSelfReferences()
    {
        // This is what makes the graph acyclic by construction — a forward edge is refused
        // here rather than detected later by a cycle check.
        var result = TaskDecomposerService.ToDecomposedTasks(
            [Task("a", 1), Task("b", 1), Task("c", 5)]);

        result.Should().HaveCount(3);
        result.Should().OnlyContain(t => t.DependsOn == null);
    }

    [Fact]
    public void DropsEdgesOntoTasksBeyondTheTaskCap()
    {
        // One more task than the runaway backstop allows, each depending on the one before it.
        // The cap keeps thirty, so the thirty-first disappears entirely rather than leaving a
        // dangling edge behind.
        var input = new List<TaskDecomposerService.TaskDto> { Task("t0") };
        for (var i = 1; i < 31; i++)
            input.Add(Task($"t{i}", i - 1));

        var result = TaskDecomposerService.ToDecomposedTasks(input);

        result.Should().HaveCount(30);
        result[29].Title.Should().Be("t29");
        result[29].DependsOn.Should().Equal(28);
    }

    [Fact]
    public void DeduplicatesRepeatedEdges()
    {
        var result = TaskDecomposerService.ToDecomposedTasks(
            [Task("a"), Task("b", 0, 0, 0)]);

        result[1].DependsOn.Should().Equal(0);
    }
}
