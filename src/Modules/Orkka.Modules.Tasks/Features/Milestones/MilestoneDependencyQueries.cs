namespace Orkka.Modules.Tasks.Features.Milestones;

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;

internal static class MilestoneDependencyQueries
{
    /// <summary>
    /// True when a milestone has gone as far as its dependents need it to go: its code is on
    /// the default branch, or it never had any to put there.
    ///
    /// The bar is SHIPPED, not Completed, and the distinction is the whole point. Completed
    /// means every task is Done — which the progress updater sets while arming
    /// <see cref="MilestoneShipState.Pending"/>, BEFORE the merge into the default branch. A
    /// dependent released at Completed would cut its branch from a default branch that still
    /// lacks the work it was told to build on, which is exactly the failure these edges exist
    /// to prevent.
    ///
    /// The states that are NOT Shipped but still satisfy:
    /// <list type="bullet">
    /// <item>Cancelled — the work is not coming; blocking dependents forever is not a plan.</item>
    /// <item>Completed with ShipState back at NotReady — a milestone with no branch (no repo,
    /// imported, closed by hand) or one whose repos are all local-path, which
    /// MarkMilestoneNothingToShipAsync returns to NotReady. Nothing will ever ship it, so
    /// requiring Shipped would deadlock the roadmap behind it.</item>
    /// </list>
    ///
    /// Pending, Shipping and Failed all mean "the merge has not landed" and hold dependents.
    /// Failed deliberately included: it needs a human, and starting work against a base that
    /// was supposed to have it is worse than waiting for them.
    /// </summary>
    public static readonly Expression<Func<Milestone, bool>> Satisfied =
        m => (m.Status == MilestoneStatus.Completed || m.Status == MilestoneStatus.Cancelled)
            && m.ShipState != MilestoneShipState.Pending
            && m.ShipState != MilestoneShipState.Shipping
            && m.ShipState != MilestoneShipState.Failed;

    /// <summary>In-memory twin of <see cref="Satisfied"/>, for callers holding a loaded row.
    /// Compiled from the same expression so the two can never drift.</summary>
    public static readonly Func<Milestone, bool> SatisfiedFn = Satisfied.Compile();

    /// <summary>
    /// True when every milestone this one waits on is <see cref="Satisfied"/>. A milestone with
    /// no edges is always ready — position in the roadmap implies nothing on its own.
    ///
    /// Inlined rather than composed from <see cref="Satisfied"/>: EF cannot translate an
    /// invoked expression inside <c>Any</c> without a rewriter, and the parity test
    /// (MilestoneDependencyGateTests) pins the two forms to the same truth table so this copy
    /// cannot drift from the one above.
    /// </summary>
    public static readonly Expression<Func<Milestone, bool>> IsUnblocked =
        m => !m.Dependencies.Any(d =>
            !((d.DependsOnMilestone.Status == MilestoneStatus.Completed
                    || d.DependsOnMilestone.Status == MilestoneStatus.Cancelled)
                && d.DependsOnMilestone.ShipState != MilestoneShipState.Pending
                && d.DependsOnMilestone.ShipState != MilestoneShipState.Shipping
                && d.DependsOnMilestone.ShipState != MilestoneShipState.Failed));

    /// <summary>
    /// The same gate expressed over a TASK: its milestone is unblocked, or it has none.
    /// Backlog tasks (no milestone) are never gated — there is nothing to gate them on.
    /// </summary>
    public static readonly Expression<Func<TaskEntity, bool>> MilestoneUnblocked =
        t => t.Milestone == null
            || !t.Milestone.Dependencies.Any(d =>
                !((d.DependsOnMilestone.Status == MilestoneStatus.Completed
                        || d.DependsOnMilestone.Status == MilestoneStatus.Cancelled)
                    && d.DependsOnMilestone.ShipState != MilestoneShipState.Pending
                    && d.DependsOnMilestone.ShipState != MilestoneShipState.Shipping
                    && d.DependsOnMilestone.ShipState != MilestoneShipState.Failed));

    /// <summary>
    /// A gated milestone's task may be PLANNED — never built — once every milestone it waits
    /// on is past planning: each is either <see cref="Satisfied"/>, or has tasks and none of
    /// them is still in Drafting, Ready, Planning or PlanReady (everything left is being
    /// built, reviewed or merged), and its ship has not failed.
    ///
    /// Planning reads the codebase and writes a plan; it touches no branch. Holding it until
    /// the previous milestone ships left autopilot idle through the whole last task of every
    /// milestone (prod, 2026-09-17: the owner watched ten drafted tasks sit untouched while one
    /// task finished). What stays gated is the BUILD: the plan is banked, and the task starts
    /// Developing only once <see cref="MilestoneUnblocked"/> holds — from a base that has the
    /// previous milestone in it. A plan written slightly ahead of that base is reconciled by
    /// the base sync at Developing entry, the same as any plan written while siblings merge.
    ///
    /// Not before the blocker is past planning: a plan for milestone 4 written while milestone
    /// 3 has not been designed is a plan against code nobody has decided on. SkipPlanning tasks
    /// never qualify — their first phase is the build.
    ///
    /// A Story member never plans ahead: it plans against its predecessor's code on the shared
    /// branch, which does not exist until that member is Completed (its dependency edge holds
    /// every phase until then).
    /// </summary>
    public static readonly Expression<Func<TaskEntity, bool>> PlanAheadAcrossGate =
        t => !t.SkipPlanning
            && (t.Group == null || t.Group.Kind != TaskGroupKind.Story)
            && t.Milestone != null
            && !t.Milestone.Dependencies.Any(d =>
                !(((d.DependsOnMilestone.Status == MilestoneStatus.Completed
                            || d.DependsOnMilestone.Status == MilestoneStatus.Cancelled)
                        && d.DependsOnMilestone.ShipState != MilestoneShipState.Pending
                        && d.DependsOnMilestone.ShipState != MilestoneShipState.Shipping
                        && d.DependsOnMilestone.ShipState != MilestoneShipState.Failed)
                    || (d.DependsOnMilestone.ShipState != MilestoneShipState.Failed
                        && d.DependsOnMilestone.Tasks.Any(x => !x.IsArchived)
                        && !d.DependsOnMilestone.Tasks.Any(x => !x.IsArchived
                            && (x.CurrentLane == Lane.Drafting
                                || x.CurrentLane == Lane.Ready
                                || x.CurrentLane == Lane.Planning
                                || x.CurrentLane == Lane.PlanReady)))));

    /// <summary>An autopilot task whose next run is prep (PM review or planning) — the only
    /// runs <see cref="PlanAheadAcrossGate"/> lets past a milestone gate. Hand-submitted tasks
    /// never plan ahead: their plan parks for a human, so planning early buys nothing.</summary>
    private static readonly Expression<Func<TaskEntity, bool>> InAutopilotPrep =
        t => t.AutopilotSessionId != null
            && (t.CurrentLane == Lane.Planning || (t.CurrentLane == Lane.Ready && !t.SkipPlanning));

    /// <summary>What autopilot may SUBMIT: a task whose milestone is unblocked, or one that may
    /// plan ahead of its gate.</summary>
    public static readonly Expression<Func<TaskEntity, bool>> AutopilotMaySubmit =
        Combine(MilestoneUnblocked, PlanAheadAcrossGate, Expression.OrElse);

    /// <summary>What the queue may PICK UP past the milestone gate: anything whose milestone is
    /// unblocked, and — while it is not — an autopilot task's prep run that may plan ahead.
    /// The same task queued at Developing with its plan approved is held here until the
    /// milestone it waits on ships.</summary>
    public static readonly Expression<Func<TaskEntity, bool>> MayBePickedUp =
        Combine(MilestoneUnblocked, Combine(PlanAheadAcrossGate, InAutopilotPrep, Expression.AndAlso), Expression.OrElse);

    /// <summary>One lambda from two, both reading the same parameter — the form EF translates.
    /// (An <c>Invoke</c> of one expression inside another does not.)</summary>
    private static Expression<Func<TaskEntity, bool>> Combine(
        Expression<Func<TaskEntity, bool>> left, Expression<Func<TaskEntity, bool>> right,
        Func<Expression, Expression, BinaryExpression> join)
    {
        var parameter = left.Parameters[0];
        var rightBody = new ReplacingExpressionVisitor([right.Parameters[0]], [parameter]).Visit(right.Body);
        return Expression.Lambda<Func<TaskEntity, bool>>(join(left.Body, rightBody), parameter);
    }
}
