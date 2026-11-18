namespace Orkka.Modules.Tasks.Domain;

using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Domain;

/// <summary>
/// "This milestone cannot start until that one is on the default branch."
///
/// The task-level twin of this edge (<see cref="TaskDependency"/>) orders work INSIDE a
/// milestone. This one orders the milestones themselves, and it exists because a milestone
/// branch is cut FROM the default branch: until its predecessor ships, the branch a dependent
/// milestone starts from does not contain the predecessor's code at all. Every task under it
/// is then written blind against a tree missing the thing it builds on — which the merge
/// resolves textually and nobody notices until review.
///
/// Position does NOT imply an edge. A roadmap's later milestones frequently touch different
/// areas and are fine to run alongside earlier ones; serialising the whole roadmap by position
/// would cost the pipeline its parallelism for a guarantee most milestones do not need. The
/// edges say which ones do.
/// </summary>
public sealed class MilestoneDependency : TenantEntity
{
    public Ulid MilestoneId { get; set; }
    public Ulid DependsOnMilestoneId { get; set; }

    /// <summary>Why, in the planner's words — "needs the Appointment model". Shown to the user
    /// on the roadmap, and the only record of the reasoning once the plan is history.</summary>
    public string? Reason { get; set; }

    public DependencySource Source { get; set; }

    // Navigation
    public Milestone Milestone { get; set; } = null!;
    public Milestone DependsOnMilestone { get; set; } = null!;
}
