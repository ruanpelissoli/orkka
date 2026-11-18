namespace Orkka.Modules.Tasks.Features.Inbox;

using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// Decides what the human should look at next. The ordering exists because a parallel agent
/// fleet parks work faster than anyone can drain it: a flat list sorted by date buries the
/// item that is stalling five other tasks under a week-old low-priority question.
/// </summary>
internal static class InboxRanking
{
    /// <summary>
    /// Which action the task is waiting on. Merge trouble outranks the stored status because a
    /// conflicted task sits in Done/Completed — its status says "finished" while the branch is
    /// in fact stuck.
    /// </summary>
    /// <remarks>
    /// Takes the two merge predicates as booleans rather than the entity: both are pure null
    /// checks over JSON/text columns that the inbox never reads, so they are evaluated in SQL
    /// and the columns stay out of the result set.
    /// </remarks>
    public static string ClassifyKind(bool hasUnresolvedMergeConflict, bool hasMergeFailure, TaskStatus status)
    {
        if (hasUnresolvedMergeConflict)
            return InboxItemKind.MergeConflict;
        if (hasMergeFailure)
            return InboxItemKind.MergeFailed;

        return status switch
        {
            TaskStatus.WaitingPlanApproval => InboxItemKind.PlanApproval,
            TaskStatus.WaitingAnswer => InboxItemKind.Question,
            TaskStatus.WaitingCodeReview => InboxItemKind.CodeReview,
            TaskStatus.WaitingQaReview => InboxItemKind.QaReview,
            TaskStatus.WaitingUserVerification => InboxItemKind.Verification,
            TaskStatus.WaitingForRunner => InboxItemKind.RunnerOffline,
            TaskStatus.OutOfTokens => InboxItemKind.OutOfTokens,
            TaskStatus.Blocked => InboxItemKind.Blocked,
            TaskStatus.ReasoningTimeout => InboxItemKind.Error,
            _ => InboxItemKind.Blocked
        };
    }

    /// <summary>
    /// Base weight per kind, ordered by how much is stalled behind the human. Out-of-tokens
    /// tops the list because it halts every task in the org, not just this one; questions and
    /// plan approvals come next because an agent is idling mid-task on the answer.
    /// </summary>
    private static int KindWeight(string kind) => kind switch
    {
        InboxItemKind.OutOfTokens => 120,
        InboxItemKind.Question => 100,
        InboxItemKind.PlanApproval => 90,
        InboxItemKind.MergeConflict => 85,
        InboxItemKind.MergeFailed => 80,
        InboxItemKind.CodeReview => 70,
        InboxItemKind.QaReview => 70,
        InboxItemKind.Verification => 60,
        InboxItemKind.RunnerOffline => 50,
        InboxItemKind.Error => 45,
        InboxItemKind.Blocked => 30,
        _ => 20
    };

    private static int PriorityWeight(Contracts.Priority p) => p switch
    {
        Contracts.Priority.Critical => 60,
        Contracts.Priority.High => 35,
        Contracts.Priority.Medium => 15,
        _ => 0
    };

    /// <summary>
    /// Score = kind + priority + cost-of-delay + staleness. Staleness is capped at a week so a
    /// forgotten item can never permanently outrank a fresh blocker, and each blocked dependent
    /// is worth roughly a day of waiting.
    /// </summary>
    public static int Score(string kind, Contracts.Priority priority, DateTimeOffset waitingSince, int blockedDependents, DateTimeOffset now)
    {
        var ageHours = Math.Max(0, (now - waitingSince).TotalHours);
        var staleness = (int)Math.Min(ageHours, 168);
        return KindWeight(kind) + PriorityWeight(priority) + (blockedDependents * 24) + staleness;
    }
}
