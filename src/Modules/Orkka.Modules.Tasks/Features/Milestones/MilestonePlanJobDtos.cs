namespace Orkka.Modules.Tasks.Features.Milestones;

using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;

/// <summary>
/// A planning run's current state — the only thing POST /milestones/plan returns now, and what
/// the client polls or re-fetches after a reconnect.
/// </summary>
/// <param name="Status">One of <see cref="MilestonePlanStatus"/>, as a name.</param>
/// <param name="Step">One of <see cref="MilestonePlanSteps"/> while running, else null.</param>
/// <param name="Plan">The proposal, present only once Completed. Nothing is persisted from it —
/// the user still edits it and calls accept, exactly as before.</param>
/// <param name="FailureCode">One of <see cref="MilestonePlanFailureCodes"/> when Failed. The
/// client branches on this: <c>budget</c> is the top-up prompt the old HTTP 402 drove.</param>
/// <param name="StartedAt">When a worker claimed it. The client shows elapsed time from here —
/// a run that says how long it has been going is a run people wait for.</param>
public sealed record MilestonePlanJobResponse(
    Ulid JobId,
    string Status,
    string? Step,
    Ulid? MilestoneId,
    Ulid? RepoId,
    MilestonePlanResponse? Plan,
    string? FailureCode,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

internal static class MilestonePlanJobMapping
{
    /// <summary>
    /// Projects a job row for the client. The stored proposal is handed back as raw JSON rather
    /// than re-serialized through the DTO: it was written by the same contract that reads it,
    /// and a round-trip would only add a way for the two to disagree.
    /// </summary>
    public static MilestonePlanJobResponse ToResponse(
        this MilestonePlanJob job, MilestonePlanResponse? plan) =>
        new(job.Id,
            job.Status.ToString(),
            // A finished job has no current step — leaving the last one set would keep a
            // progress label on screen next to a completed plan.
            job.Status is MilestonePlanStatus.Pending or MilestonePlanStatus.Running
                ? job.CurrentStep ?? MilestonePlanSteps.Queued
                : null,
            job.MilestoneId,
            job.RepoId,
            plan,
            job.FailureCode,
            job.ErrorMessage,
            job.CreatedAt,
            job.StartedAt,
            job.CompletedAt);
}
