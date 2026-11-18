namespace Orkka.Modules.Tasks.Features.Milestones;

using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;

/// <summary>Builder for the realtime planning event, so the endpoint and the queue processor
/// emit the same shape.</summary>
internal static class MilestonePlanNotifications
{
    public static MilestonePlanChangedEvent From(MilestonePlanJob job) =>
        new(job.Id,
            job.ProjectId,
            job.MilestoneId,
            job.Status.ToString(),
            job.Status is MilestonePlanStatus.Pending or MilestonePlanStatus.Running
                ? job.CurrentStep ?? MilestonePlanSteps.Queued
                : null,
            job.FailureCode,
            job.ErrorMessage,
            job.StartedAt,
            DateTimeOffset.UtcNow);
}
