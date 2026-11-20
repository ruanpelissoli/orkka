namespace Orkka.Modules.Trackers.PublicApi;

using Microsoft.Extensions.Logging;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Trackers.Domain;

/// <summary>
/// The tracker's dependency links (Jira "is blocked by", Azure DevOps Predecessor, GitHub
/// "blocked by"), as stored on each link by the last poll, become task dependencies: when both
/// tickets are worked in this project the blocked one waits until its blocker merged; a blocker
/// Orkka does not work is a note on the card, never a wait.
/// </summary>
internal sealed partial class TrackersApi
{
    private async Task ReconcileDependenciesAsync(ProjectTrackerConfig config, IReadOnlyDictionary<string, TicketLink> links,
        CancellationToken ct)
    {
        var edges = new List<TrackerBlockEdge>();
        var outside = new Dictionary<Ulid, List<string>>();
        foreach (var link in links.Values.Where(l => l.TrackingEndedAt == null))
        {
            // Every tracked task is listed, so a note whose link went away is cleared. A Story
            // link and its first sub-task share a task: their notes add up.
            if (!outside.TryGetValue(link.TaskId, out var notes))
                outside[link.TaskId] = notes = [];
            foreach (var key in ParseKeys(link.BlockedByKeysJson))
            {
                if (links.TryGetValue(key, out var blocker))
                    edges.Add(new TrackerBlockEdge(End(link), End(blocker), blocker.TicketKey));
                else
                    notes.Add(key);
            }
        }
        try
        {
            await tasksApi.ReconcileTrackerDependenciesAsync(config.ProjectId, edges,
                outside.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Dependencies are a refinement of the poll, never a reason to fail it.
            logger.LogWarning(ex, "Could not apply the tracker's dependency links for source {ConfigId}", config.Id);
        }
    }

    private static TrackerBlockEnd End(TicketLink link)
        => link.Role == TicketRole.Story ? new TrackerBlockEnd(null, link.GroupId) : new TrackerBlockEnd(link.TaskId, link.GroupId);
}
