namespace Orkka.Tests.Features.Tasks;

using FluentAssertions;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure.Services;

/// <summary>
/// Where the design work sits in a plan.
///
/// Every planner here used to put the look last — "foundational features first, polish last",
/// a roadmap whose fourth milestone was "the rough edges — responsive layout". That ordering
/// meant weeks of looking at something plainly ugly while being told the polish comes later.
///
/// These are the load-bearing rules of that inversion. Rewording is fine; dropping one puts
/// the design back at the end of the roadmap, which is silent until a customer sees it.
/// </summary>
public sealed class UiFirstPlanningTests
{
    /// <summary>The trailing milestone titles this change exists to stop the planner emitting.
    /// Naming them verbatim is what makes the refusal concrete rather than a sentiment.</summary>
    private static readonly string[] BannedMilestoneTitles =
        ["\"UI polish\"", "\"Make it look good\"", "\"Visual refinements\""];

    [Fact]
    public void GoalPlanner_KeepsVisualQualityInsideEveryTask()
    {
        DefaultPrompts.TaskDecompositionFromGoal.Should()
            .Contain("UI QUALITY BELONGS IN EVERY TASK")
            .And.Contain("375px");
    }

    /// <summary>The one design-only task that IS worth planning — and only on a repository
    /// that genuinely has no design system yet.</summary>
    [Fact]
    public void GoalPlanner_AllowsAFoundationTaskFirstWhenNoneExists()
        => DefaultPrompts.TaskDecompositionFromGoal.Should()
            .Contain("foundation task")
            .And.Contain("it goes FIRST");

    // --- Milestone 1 is sized to the free grant ---------------------------------------------
    //
    // A new owner's 10,000 credits buy two or three tasks at today's per-task cost. A first
    // milestone of "the shell and the look" over several tasks burned the whole grant with the
    // design preview still on the homepage. These pin the sizing rule at every planner that
    // touches milestone 1; the design-first rules above are unchanged by it.

    [Fact]
    public void GoalPlanner_RespectsABoundedFirstMilestone()
        => DefaultPrompts.TaskDecompositionFromGoal.Should()
            .Contain("stay within that bound");

    [Fact]
    public void TaskDecompositionFromGoal_MakesTheFirstScreenSeedItsData()
        => DefaultPrompts.TaskDecompositionFromGoal.Should()
            .Contain("seeds the data it shows")
            .And.Contain("public/brand/");

}
