namespace Orkka.Modules.Trackers.Domain;

using Orkka.LocalRunner.Contracts;
using Orkka.SharedKernel.Domain;

/// <summary>
/// One tracker ticket and the task it became. Created when a poll first reports the ticket; the
/// key Orkka matches every later report on, the record of what Orkka last stored about it (so an
/// unchanged ticket is not re-sent), and where the task's repository folder came from.
/// </summary>
public sealed class TicketLink : TenantEntity
{
    public Ulid ConfigId { get; set; }

    public Ulid ProjectId { get; set; }

    public Ulid TaskId { get; set; }

    /// <summary>"jira", "ado" or "github".</summary>
    public required string Kind { get; set; }

    public required string SiteUrl { get; set; }

    /// <summary>SHOP-123 / 4567 — what the tracker and the developer call it.</summary>
    public required string TicketKey { get; set; }

    /// <summary>The tracker's internal id.</summary>
    public required string TicketId { get; set; }

    public required string TicketUrl { get; set; }

    /// <summary>The configured epic the ticket sits under.</summary>
    public string? EpicKey { get; set; }

    /// <summary>The repository routing picked at import. Null when nobody could say — the task
    /// asked which one (the answer goes on the task, not here) — or for a cross-repo group.</summary>
    public Ulid? RepoId { get; set; }

    /// <summary>The ticket's direct parent in the tracker (an epic, a story, a feature).</summary>
    public string? ParentKey { get; set; }

    /// <summary>How the ticket is worked: on its own, as a Story (a task group whose members are
    /// its sub-tasks), or as one of a Story's sub-tasks.</summary>
    public TicketRole Role { get; set; } = TicketRole.Standalone;

    /// <summary>The Story's task group, for <see cref="TicketRole.Story"/> and
    /// <see cref="TicketRole.SubTask"/> links. A Story link's <see cref="TaskId"/> is its first
    /// member — where the Story ticket's own comments and notes land.</summary>
    public Ulid? GroupId { get; set; }

    /// <summary>Where the ticket sits in the tracker's hierarchy at the last poll (above a story,
    /// a story, a sub-task) — <see cref="TicketLevel.Unknown"/> from a runner that predates it.</summary>
    public TicketLevel Level { get; set; } = TicketLevel.Unknown;

    /// <summary>Keys of the tickets that block this one at the last poll (JSON array; null when
    /// none) — turned into task dependencies when both are worked in Orkka.</summary>
    public string? BlockedByKeysJson { get; set; }

    /// <summary>Imported by hand (by its key): the developer asked for this ticket, so it stays on
    /// the board whoever it is assigned to and whatever its status — a reassignment does not retire
    /// it and a Story member is never released for not being the developer's.</summary>
    public bool Pinned { get; set; }

    /// <summary>A pinned Story: the sub-tasks the developer left unticked at import (JSON array) —
    /// never members, never waited for.</summary>
    public string? ExcludedChildKeysJson { get; set; }

    /// <summary>A Story link: its sub-task keys in tracker order at the last poll (JSON array).</summary>
    public string? ChildKeysJson { get; set; }

    /// <summary>A sub-task worked on its own (its Story is not the developer's, or the Story's
    /// pull request is already closed): the Story's title and description, which the prompts
    /// carry as "Parent story context".</summary>
    public string? ParentTitle { get; set; }
    public string? ParentDescription { get; set; }

    /// <summary>When Orkka stopped reading this ticket on every poll: its task was archived, it
    /// was merged, 30 days passed since its pull request opened, or it vanished from the tracker.
    /// Null while tracked. A retired ticket that returns to intake is tracked again.</summary>
    public DateTimeOffset? TrackingEndedAt { get; set; }

    /// <summary>When <see cref="SyncedPhase"/> last moved forward — "30 days past PR opened"
    /// counts from here.</summary>
    public DateTimeOffset? SyncedAt { get; set; }

    /// <summary>The rule's folder inside the repository, copied at import.</summary>
    public string? Subpath { get; set; }

    /// <summary>Hash of the normalized ticket text as Orkka last stored it.</summary>
    public string? ContentHash { get; set; }

    /// <summary>The newest comment Orkka has copied onto the task.</summary>
    public DateTimeOffset? LastCommentAt { get; set; }

    /// <summary>False once Orkka retired the ticket — reassigned, closed before work started, or
    /// gone from the tracker; true again if it comes back. A ticket that merely moves on (To Do →
    /// In Progress) stays true.</summary>
    public bool AssignedToOwner { get; set; } = true;

    /// <summary>The ticket's status in the tracker at the last poll.</summary>
    public string? RemoteStatus { get; set; }

    /// <summary>JSON of the ticket's attachments (names, sizes, cached on the machine or not) —
    /// the files themselves stay on the developer's machine.</summary>
    public string AttachmentsJson { get; set; } = "[]";

    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>The furthest pipeline milestone the ticket's status has been moved for
    /// (<see cref="TrackerPhase"/>). Only ever moves forward.</summary>
    public TrackerPhase SyncedPhase { get; set; } = TrackerPhase.None;

    /// <summary>The move last handed to a runner, and when — so a second sync within the
    /// window does not hand the same move out again before the first is acknowledged.</summary>
    public TrackerPhase? PendingPhase { get; set; }
    public DateTimeOffset? PendingSince { get; set; }

    /// <summary>Failed attempts at <see cref="PendingPhase"/>; capped, then the move is given up
    /// and the task says why.</summary>
    public int TransitionAttempts { get; set; }

    public string? LastTransitionError { get; set; }

    /// <summary>The published forge pull request's state as the developer's CLI last reported
    /// it ("open", "merged", "closed"), and when it was last asked.</summary>
    public string? PrState { get; set; }
    public DateTimeOffset? PrCheckedAt { get; set; }
}

public enum TicketRole
{
    Standalone = 0,
    Story = 1,
    SubTask = 2,
}

/// <summary>The pipeline steps a ticket's status follows, in order. Moves only go forward, and
/// a step the project did not map is skipped.</summary>
public enum TrackerPhase
{
    None = 0,
    /// <summary>The task was created from the ticket.</summary>
    PickedUp = 1,
    /// <summary>The first PM review / planning run started.</summary>
    Planning = 2,
    /// <summary>The plan was approved — the first Developing run.</summary>
    Developing = 3,
    /// <summary>Orkka's own Code Review lane.</summary>
    InReview = 4,
    /// <summary>The forge pull request was published, or the work landed in a linked folder. For
    /// a Story: the shared pull request was marked ready.</summary>
    PrOpened = 5,
    /// <summary>The forge pull request merged.</summary>
    Merged = 6,
}
