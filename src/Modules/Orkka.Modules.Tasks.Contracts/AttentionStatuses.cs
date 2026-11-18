namespace Orkka.Modules.Tasks.Contracts;

/// <summary>
/// Single source of truth for "the pipeline is parked until a human does something".
/// Both the save-time notification hook (TasksDbContext) and the Attention Inbox read from
/// here — if the two sets drifted, the inbox would silently omit an item the user was
/// already notified about (or vice versa).
/// </summary>
public static class AttentionStatuses
{
    /// <summary>
    /// Statuses where no agent can make progress until a human acts. Drives the inbox.
    /// Deliberately excludes <see cref="TaskStatus.Completed"/>: a finished task is worth a
    /// notification but is not an outstanding to-do.
    /// </summary>
    public static readonly IReadOnlySet<TaskStatus> Actionable = new HashSet<TaskStatus>
    {
        TaskStatus.WaitingPlanApproval,
        TaskStatus.WaitingAnswer,
        TaskStatus.WaitingCodeReview,
        TaskStatus.WaitingQaReview,
        TaskStatus.WaitingUserVerification,
        TaskStatus.Blocked,
        TaskStatus.ReasoningTimeout,
        TaskStatus.OutOfTokens,
        TaskStatus.WaitingForRunner,
    };

    /// <summary>
    /// <see cref="Actionable"/> plus terminal transitions worth telling the user about.
    /// Used by the save hook that publishes TaskStatusChanged.
    /// </summary>
    public static readonly IReadOnlySet<TaskStatus> Notifiable = new HashSet<TaskStatus>(Actionable)
    {
        TaskStatus.Completed,
    };
}
