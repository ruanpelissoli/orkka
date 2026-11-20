namespace Orkka.Modules.Trackers.Domain;

using Orkka.SharedKernel.Domain;

/// <summary>
/// "Import SHOP-10 now": a ticket the developer asked for by its key, waiting for the next poll
/// of the project's ticket source. The poll reads it (and the chosen sub-tasks) by key with the
/// developer's own token — description, attachments, comments — and the report imports it as a
/// task or, with sub-tasks, a Story, whatever its status or assignee. Imported links are pinned
/// (<see cref="TicketLink.Pinned"/>), so a reassignment never takes them off the board.
/// </summary>
public sealed class TrackerImport : TenantEntity
{
    public Ulid ConfigId { get; set; }

    public Ulid ProjectId { get; set; }

    /// <summary>The ticket's key in the tracker's own form (SHOP-10, 4567, owner/repo#12).</summary>
    public required string TicketKey { get; set; }

    /// <summary>The sub-tasks the developer ticked, in the tracker's order (JSON array). Empty =
    /// the ticket is a task of its own.</summary>
    public string SubTaskKeysJson { get; set; } = "[]";

    public string? RequestedByUserId { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    /// <summary>Set when a report imported it (or found it already on the board), or gave up.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>The task it became — a Story's first member.</summary>
    public Ulid? TaskId { get; set; }

    /// <summary>Why it could not be imported (the ticket does not exist, the poll keeps failing).</summary>
    public string? Error { get; set; }

    /// <summary>Reports that did not carry the ticket yet — after a few, it does not exist.</summary>
    public int Attempts { get; set; }
}
