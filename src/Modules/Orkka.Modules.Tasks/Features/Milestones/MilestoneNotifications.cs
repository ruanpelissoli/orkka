namespace Orkka.Modules.Tasks.Features.Milestones;

using Orkka.Modules.Tasks.Contracts;

/// <summary>Builders for the realtime milestone event, so every call site emits the same shape.</summary>
internal static class MilestoneNotifications
{
    public static MilestoneChangedEvent Changed(
        Ulid milestoneId, string status, MilestoneCountsResponse counts) =>
        new(milestoneId, status, counts.Total, counts.Done, counts.InFlight,
            counts.NeedsAttention, counts.NotStarted, Deleted: false, DateTimeOffset.UtcNow);

    public static MilestoneChangedEvent Deleted(Ulid milestoneId) =>
        new(milestoneId, Status: null, 0, 0, 0, 0, 0, Deleted: true, DateTimeOffset.UtcNow);
}
