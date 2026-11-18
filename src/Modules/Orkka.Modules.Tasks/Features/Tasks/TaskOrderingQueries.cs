namespace Orkka.Modules.Tasks.Features.Tasks;

using System.Linq.Expressions;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;

internal static class TaskOrderingQueries
{
    /// <summary>
    /// Priority as a sortable rank.
    ///
    /// <see cref="TaskEntity.Priority"/> is persisted as a STRING (<c>HasConversion&lt;string&gt;</c>),
    /// so <c>OrderByDescending(t =&gt; t.Priority)</c> becomes <c>ORDER BY "Priority" DESC</c> over
    /// text and sorts alphabetically: Medium, Low, High, Critical. That puts **Critical last** —
    /// the exact opposite of what every "highest priority first" call site intends, and it is
    /// silent because the ordering still looks deterministic and plausible.
    ///
    /// Every priority ordering must go through this. It compiles to a CASE expression, so the
    /// sort still happens in the database.
    /// </summary>
    public static readonly Expression<Func<TaskEntity, int>> PriorityRank =
        t => t.Priority == Priority.Critical ? 3
            : t.Priority == Priority.High ? 2
            : t.Priority == Priority.Medium ? 1
            : 0;

    /// <summary>
    /// True when every task this one waits on has at least been submitted — i.e. none of its
    /// blockers is still sitting in Drafting.
    ///
    /// The queue defers a task whose blockers are unfinished, which is why candidate selection
    /// does not otherwise filter on dependencies. But it can only defer behind work that is
    /// actually IN the queue: a blocker still in Drafting will never complete on its own, so
    /// the dependent occupies a queue slot indefinitely.
    ///
    /// That is worse than wasteful. Queued-but-blocked tasks count as in-flight, and in-flight
    /// work consumes the submission headroom — so a few dependents can starve the very blocker
    /// they are waiting for, and the session never drains because something is always "moving".
    /// </summary>
    public static readonly Expression<Func<TaskEntity, bool>> AllBlockersSubmitted =
        t => !t.Dependencies.Any(d =>
            !d.DependsOnTask.IsArchived
            && d.DependsOnTask.CurrentLane == Lane.Drafting);
}
