namespace Orkka.Modules.Tasks.Features.CodeReview;

using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.Modules.SourceControl.Contracts;
using Microsoft.Extensions.Logging;

/// <summary>
/// Arms the worker's conflict resolver when a user-clicked merge fails on conflicts, so
/// the click itself is enough: the resolver combines the branches and completes the merge
/// instead of parking the failure on the human. Setting <c>MergeResolutionStrategy</c> is
/// the cross-process signal — the worker's merge sweep picks it up on its next cycle and
/// runs the resolution with the same code path as a human-picked strategy.
/// </summary>
public static class MergeAutoResolution
{
    /// <summary>What the user sees when their merge click turned into a resolver run.</summary>
    public const string StartedMessage =
        "Merge conflicts detected — Orkka is combining the changes automatically and will merge when done.";

    public enum ArmResult
    {
        /// <summary>Not a conflict failure (or unresolvable) — surface the raw error.</summary>
        NotArmed,
        /// <summary>A resolution is already queued or running — nothing new to persist.</summary>
        AlreadyInFlight,
        /// <summary>This call armed the resolver — tell the user it started.</summary>
        Armed,
    }

    /// <summary>
    /// Decides whether a failed merge should hand off to the automatic resolver, and arms
    /// it when so. The caller owns messaging (it knows what it already persisted); this
    /// only flips the strategy flag. Never throws — an arming failure just leaves the
    /// caller on its normal error path.
    /// </summary>
    public static async Task<ArmResult> TryArmAsync(
        TasksDbContext db, IPullRequestService pullRequests,
        Ulid repoId, int prNumber,
        TaskEntity task, string? mergeError, ILogger logger, CancellationToken ct)
    {
        try
        {
            if (task.IsMerged || string.IsNullOrEmpty(task.PrUrl))
                return ArmResult.NotArmed;

            // A pending strategy or a live lease means a resolver run is already serving
            // this task — the click's intent is covered, don't queue a second run.
            if (task.MergeResolutionStrategy is not null)
                return ArmResult.AlreadyInFlight;
            if (task.LeaseExpiresAt is { } leaseUntil && leaseUntil > DateTimeOffset.UtcNow)
                return ArmResult.AlreadyInFlight;

            // Environment-blocked failures (dirty local repo, wrong branch checked out,
            // runner offline) cannot be fixed by any resolver run.
            if (MergeBlockedReasons.IsEnvironmentBlocked(mergeError))
                return ArmResult.NotArmed;

            // Only arm when the dry-run merge on the runner confirms actual conflicts — any
            // other refusal would waste a full resolver-agent run.
            var pr = await pullRequests.GetAsync(repoId, prNumber, ct);
            if (pr.IsFailure || pr.Value!.Mergeable != false)
                return ArmResult.NotArmed;

            task.MergeResolutionStrategy = "combine";
            task.MergeResolutionStartedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Merge conflicts on task {TaskId} — armed automatic 'combine' resolution from the merge click",
                task.Id);
            return ArmResult.Armed;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to arm automatic merge resolution for task {TaskId}", task.Id);
            return ArmResult.NotArmed;
        }
    }
}
