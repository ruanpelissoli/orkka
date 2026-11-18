namespace Orkka.Modules.Tasks.Contracts;

/// <summary>An open autopilot session — the Worker sweep's unit of work. The session, not a
/// project setting, is what "autopilot is running here" means: there is exactly one open row
/// per project (filtered unique index), so the two can never disagree.</summary>
public sealed record OpenAutopilotSession(
    Ulid Id,
    Ulid OrgId,
    Ulid ProjectId,
    string StartedByUserId,
    DateTimeOffset StartedAt,
    decimal? CapUsd,
    decimal SpentUsd,
    int TasksSubmitted,
    /// <summary>Bumped by every counter write. The sweep uses it as the quiet-period clock for
    /// the drained check, which is why the session needs no separate "drained since" column.</summary>
    DateTimeOffset UpdatedAt);

/// <summary>
/// Autopilot's live state for one project, as the board renders it.
/// </summary>
/// <param name="Running">False means no open session. Everything below is then last-session
/// history, which is what the board shows after an overnight run ended.</param>
/// <param name="StopReason">Null while running. This is the whole morning story — "drained"
/// and "cap reached" and "three failures in a row" are very different mornings.</param>
public sealed record AutopilotStatus(
    bool Running,
    Ulid? SessionId,
    DateTimeOffset? StartedAt,
    DateTimeOffset? StoppedAt,
    string? StopReason,
    decimal? CapUsd,
    /// <summary>The session answers every question its tasks ask (see
    /// AutopilotSession.AnswerEveryQuestion). For a closed session, the choice it ran with —
    /// shown, never carried into the next start: every session opts in again.</summary>
    bool AnswerEveryQuestion,
    decimal SpentUsd,
    int TasksSubmitted,
    int TasksCompleted,
    int TasksFailed,
    /// <summary>Session tasks queued or running right now.</summary>
    int TasksInFlight,
    /// <summary>Session tasks waiting on a person. These are what the user should look at
    /// first when they come back.</summary>
    int TasksWaitingOnYou,
    /// <summary>How many submittable tasks are left in Drafting. Zero with work still in
    /// flight is normal mid-wave, and the board should say so rather than look stalled.</summary>
    int DraftingRemaining);

/// <summary>Broadcast to board viewers whenever the session's state changes.</summary>
public sealed record AutopilotChangedEvent(
    Ulid ProjectId,
    AutopilotStatus Status,
    DateTimeOffset At);

/// <summary>
/// A cross-repo group as the orchestrator needs to see it: the contract its members code
/// against, and enough identity to say which siblings exist.
/// </summary>
/// <param name="MemberSummaries">"#12 api — Avatar upload endpoint" per member, so the prompt can
/// name the other halves. An agent that knows a sibling owns the client side stops trying to
/// write it in a repo that isn't checked out.</param>
public sealed record TaskGroupContract(
    Ulid GroupId,
    string Title,
    string? Contract,
    IReadOnlyList<string> MemberSummaries);

/// <summary>
/// The interface a completed blocker exposes, for injection into a dependent task's context.
/// </summary>
public sealed record BlockerApiContract(
    Ulid TaskId,
    int Number,
    string Title,
    /// <summary>Repo the contract lives in — named in the prompt so the agent knows the code is
    /// somewhere it cannot open, and must work from the description alone.</summary>
    Ulid? RepoId,
    string ApiSurface);

/// <summary>A Drafting task autopilot may submit. Ordered by the caller's ranking, not by id.</summary>
public sealed record AutopilotCandidate(
    Ulid Id,
    Ulid? RepoId,
    Ulid? MilestoneId,
    int Number,
    Priority Priority);

/// <summary>
/// What a session's tasks are doing right now — the input to every stop condition.
/// </summary>
/// <param name="Progressing">Session tasks that can still move without a human: Active or
/// InQueue. The drained check keys on this and NOT on "nothing to submit": a milestone whose
/// backend tasks are running and whose frontend tasks are blocked on them has nothing to submit
/// at that instant, and stopping there would end the session mid-wave.</param>
/// <param name="ParkedForHuman">Session tasks waiting on a person — an unresolved question, a
/// rejected review, a merge conflict autopilot could not resolve. Deliberately NOT counted as
/// progress: they never move on their own, so holding the session open for them holds it open
/// until morning, which is the opposite of the point.</param>
/// <param name="RecentlyTerminal">The session's tasks that reached a terminal state, newest
/// first — completed or failed. Feeds the consecutive-failure check without the sweep having to
/// carry state between polls.</param>
/// <param name="WaitingForRunner">Session tasks parked because their owner's runner is offline.
/// Counted inside <paramref name="ParkedForHuman"/> for the board, but NOT a reason to drain:
/// the runner reconnecting re-queues them with nobody lifting a finger.</param>
public sealed record AutopilotProgress(
    int Progressing,
    int ParkedForHuman,
    int Completed,
    int Failed,
    IReadOnlyList<bool> RecentlyTerminal,
    int WaitingForRunner = 0);
