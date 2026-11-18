namespace Orkka.Modules.Tasks.Domain;

using Orkka.Modules.Tasks.Contracts;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// Done means merged.
///
/// Review approval moves a task to Done and the merge sweep merges it from there, so between
/// those two steps a task that can never merge — a push that fails, a PR that cannot be
/// opened, conflicts, branch protection, a PR closed without merging — sat in Done anyway,
/// counted toward its milestone, and fed the forecast as delivered work.
///
/// This is the one rule that undoes that: an unmerged task with a recorded merge failure goes
/// back to code review, the lane whose UI points at the PR, in a status a human can act on and
/// that no sweep re-runs an agent for. <c>MarkTaskMergedAsync</c> carries it forward again the
/// moment the merge lands, so nothing is stranded — and the merge sweep keeps retrying it
/// there (see <c>ListTasksAwaitingMergeAsync</c>, which includes tasks with a failure reason
/// precisely so this demotion does not also stop the retries).
/// </summary>
public static class MergeFailureLane
{
    /// <summary>
    /// Applies the rule to <paramref name="task"/>. Returns the lane it left (always
    /// <see cref="Lane.Done"/>) so the caller can broadcast the move, or null when the task
    /// was already merged or not in Done — in which case nothing is changed.
    /// </summary>
    public static Lane? DemoteFromDone(TaskEntity task)
    {
        if (task.IsMerged || task.CurrentLane != Lane.Done) return null;

        task.CurrentLane = Lane.CodeReview;
        task.Status = TaskStatus.WaitingCodeReview;
        task.HasAgentWorking = false;
        // CompletedAt deliberately survives. It records when the WORK finished, not when it
        // shipped, and it is the only durable mark that this task once passed review — which
        // is what lets the merge sweep keep retrying a demoted task (a failure reason does
        // not survive, because attaching a fresh PR clears it). Nothing counts the task as
        // delivered off this field alone: velocity and doc sync require Status == Completed,
        // milestone progress and auto-archive require the Done lane, and a human reopening
        // the task through the board clears the stamp on the way out of Done.
        return Lane.Done;
    }

    /// <summary>The inverse, once the failure is cleared: only a task this class demoted —
    /// finished (CompletedAt set), not merged, parked in CodeReview/WaitingCodeReview — goes
    /// back to Done. A task genuinely in review is left alone.</summary>
    public static Lane? RestoreToDone(TaskEntity task)
    {
        if (task.IsMerged || task.CompletedAt is null
            || task.CurrentLane != Lane.CodeReview || task.Status != TaskStatus.WaitingCodeReview)
            return null;

        task.CurrentLane = Lane.Done;
        task.Status = TaskStatus.Completed;
        return Lane.CodeReview;
    }
}
