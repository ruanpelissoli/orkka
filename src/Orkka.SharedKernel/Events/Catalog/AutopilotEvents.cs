namespace Orkka.SharedKernel.Events.Catalog;

/// <summary>
/// Published by the Tasks module when an autopilot session closes, however it closed.
///
/// This is the "here's what happened while you were away" moment, which is why it is addressed
/// to whoever STARTED the session rather than to each task's creator: they are the person who
/// chose to let it run unattended, and the outcome is the answer to their decision.
///
/// A manual stop publishes too. The user who clicked already knows, and the handler suppresses
/// their own notification — but a colleague watching the same project does not, and the reason
/// a session ended is worth the same row either way.
/// </summary>
public sealed record AutopilotSessionStopped(
    Ulid SessionId,
    Ulid OrgId,
    Ulid ProjectId,
    string StartedByUserId,
    /// <summary>The <c>AutopilotStopReason</c> name. Carried as a string so SharedKernel does
    /// not have to reference a module's enum — the same convention the status events use.</summary>
    string Reason,
    int TasksCompleted,
    int TasksFailed,
    /// <summary>Session tasks left waiting on a person. The number that decides whether the
    /// notification reads "all done" or "three things need you".</summary>
    int TasksWaitingOnYou,
    decimal SpentUsd) : IntegrationEvent;
