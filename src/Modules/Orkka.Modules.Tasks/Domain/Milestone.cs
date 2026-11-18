namespace Orkka.Modules.Tasks.Domain;

using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Domain;

/// <summary>
/// An outcome a group of tasks is working toward — the planning layer above the board.
///
/// Lives in the Tasks module rather than Projects because a milestone is defined entirely by the
/// tasks under it: every progress query is a group-by over <c>tasks</c>, and <c>MilestoneId</c>
/// gets to be a real foreign key instead of a cross-module hop.
/// </summary>
public sealed class Milestone : TenantEntity
{
    public Ulid ProjectId { get; set; }

    /// <summary>
    /// The repository this milestone's work lands in. Null means it spans repos, or the
    /// project only has one and the distinction is noise.
    ///
    /// Plain ULID — the repo lives in the Projects module. It exists because a multi-repo
    /// project's roadmap is not one sequence: "Checkout flow" in the storefront and "Payment
    /// webhooks" in the API are different bodies of work, and planning tasks for either
    /// against the wrong tree produces confident nonsense.
    /// </summary>
    public Ulid? RepoId { get; set; }

    public required string Title { get; set; }

    /// <summary>What is true when this is done. Free text, and the thing the PM agent plans
    /// against — a milestone without one is just a label.</summary>
    public string? Outcome { get; set; }

    public MilestoneStatus Status { get; set; } = MilestoneStatus.Planned;

    /// <summary>Roadmap order. Deliberately the only sequencing signal there is: agents move
    /// fast enough that human-set target dates are noise in both directions, so there is no
    /// target-date column and the forecast answers "how long" from measured runs instead.</summary>
    public int Position { get; set; }

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? CreatedByUserId { get; set; }

    /// <summary>
    /// The integration branch this milestone's tasks fork from and merge into — the whole point
    /// of milestone branching. Tasks land here instead of on the repo's default branch, and the
    /// branch merges to the default branch once, when the milestone completes. One deploy per
    /// milestone instead of one per task.
    ///
    /// Assigned lazily, the first time a task under this milestone needs a workspace, so
    /// milestones that predate this (and milestones nobody has started) carry null and cost
    /// nothing. A NAME rather than a ref: <see cref="RepoId"/> is nullable, so a cross-repo
    /// milestone materializes the same branch name in every repo its tasks touch.
    ///
    /// Never deleted after shipping. A straggler task added to a completed milestone re-derives
    /// it to Active, and the branch has to still be there to fork from.
    /// </summary>
    public string? BranchName { get; set; }

    /// <summary>
    /// Every milestone is <see cref="MilestoneIntegrationMode.Trunk"/>: its tasks land one by one
    /// and it reads Shipped the moment it completes. <see cref="MilestoneIntegrationMode.Branch"/>
    /// (batching on an integration branch Orkka merged once) is gone with Orkka's forge merges;
    /// the value survives only in history.
    /// </summary>
    public MilestoneIntegrationMode IntegrationMode { get; set; } = MilestoneIntegrationMode.Trunk;

    /// <summary>How far the completed milestone has got toward landing on the default branch.
    /// Set to Pending by the progress updater and claimed by the worker's ship sweep — the
    /// DB-flag handoff, not the in-process event bus.</summary>
    public MilestoneShipState ShipState { get; set; } = MilestoneShipState.NotReady;

    /// <summary>Why the last ship attempt failed, for the roadmap's retry affordance. Cleared
    /// when a retry succeeds.</summary>
    public string? ShipFailureReason { get; set; }

    /// <summary>When the milestone branch reached the default branch. Distinct from
    /// <see cref="CompletedAt"/>, which records when the last task finished — the gap between
    /// them is how long the merge took.</summary>
    public DateTimeOffset? ShippedAt { get; set; }

    /// <summary>The PRD or brief this was derived from, when it came out of decomposition.
    /// Plain ULID — Documents is another module and there are no cross-schema FKs.</summary>
    public Ulid? SourceDocumentId { get; set; }

    // Navigation
    public ICollection<TaskEntity> Tasks { get; set; } = [];

    /// <summary>Milestones this one waits on. See <see cref="MilestoneDependency"/>.</summary>
    public ICollection<MilestoneDependency> Dependencies { get; set; } = [];

    /// <summary>Milestones waiting on this one.</summary>
    public ICollection<MilestoneDependency> Dependents { get; set; } = [];
}
