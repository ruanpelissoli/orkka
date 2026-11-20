namespace Orkka.Modules.Tasks.PublicApi;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure.Services;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// Tracker dependency links as task dependencies. A tracker edge is an ordinary
/// <see cref="TaskDependency"/> (<see cref="DependencySource.Tracker"/>), so the queue, the board
/// and the autopilot gate on it like any other: the blocked task waits until the blocker merged
/// (a Story blocker's last member merges with the Story's pull request).
/// </summary>
public sealed partial class TasksApi
{
    private const int MaxExternalBlockedByChars = 1000;

    public async Task ReconcileTrackerDependenciesAsync(Ulid projectId, IReadOnlyList<TrackerBlockEdge> edges,
        IReadOnlyDictionary<Ulid, IReadOnlyList<string>> externalBlockedBy, CancellationToken ct = default)
    {
        // Story ends: the first live member is what the story starts with, the last what it ends with.
        var groupIds = edges.SelectMany(e => new[] { e.Blocked, e.Blocker })
            .Where(end => end.TaskId is null && end.GroupId is not null)
            .Select(end => end.GroupId!.Value)
            .Distinct()
            .ToList();
        var members = groupIds.Count == 0
            ? []
            : await _db.Tasks.IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.ProjectId == projectId && t.GroupId != null && groupIds.Contains(t.GroupId.Value)
                    && t.Status != TaskStatus.Archived && t.StoryOrder != null)
                .Select(t => new { t.Id, GroupId = t.GroupId!.Value, Order = t.StoryOrder!.Value })
                .ToListAsync(ct);
        var first = members.GroupBy(m => m.GroupId).ToDictionary(g => g.Key, g => g.MinBy(m => m.Order)!.Id);
        var last = members.GroupBy(m => m.GroupId).ToDictionary(g => g.Key, g => g.MaxBy(m => m.Order)!.Id);

        Ulid? Resolve(TrackerBlockEnd end, bool blocked)
        {
            if (end.TaskId is { } id)
                return id;
            if (end.GroupId is { } g && (blocked ? first : last).TryGetValue(g, out var member))
                return member;
            return null;
        }

        var notes = externalBlockedBy.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        var desired = new Dictionary<(Ulid Task, Ulid DependsOn), string>();
        foreach (var edge in edges)
        {
            // A link inside one Story: the story's order already chains its members.
            if (edge.Blocked.GroupId is { } bg && bg == edge.Blocker.GroupId)
                continue;
            if (Resolve(edge.Blocked, blocked: true) is not { } task || Resolve(edge.Blocker, blocked: false) is not { } dependsOn
                || task == dependsOn)
                continue;
            desired.TryAdd((task, dependsOn), edge.BlockerKey);
        }

        var existing = await _db.TaskDependencies.IgnoreQueryFilters()
            .Where(d => d.Source == DependencySource.Tracker && d.Task.ProjectId == projectId)
            .ToListAsync(ct);
        var stale = existing.Where(d => !desired.ContainsKey((d.TaskId, d.DependsOnTaskId))).ToList();
        if (stale.Count > 0)
        {
            _db.TaskDependencies.RemoveRange(stale);
            await _db.SaveChangesAsync(ct);
        }

        var have = existing.Except(stale).Select(d => (d.TaskId, d.DependsOnTaskId)).ToHashSet();
        var wanted = desired.Where(kv => !have.Contains(kv.Key)).ToList();
        if (wanted.Count > 0)
        {
            var ids = wanted.SelectMany(kv => new[] { kv.Key.Task, kv.Key.DependsOn }).Distinct().ToList();
            var tasks = await _db.Tasks.IgnoreQueryFilters().AsNoTracking()
                .Where(t => ids.Contains(t.Id) && t.ProjectId == projectId)
                .Select(t => new { t.Id, t.OrgId })
                .ToDictionaryAsync(t => t.Id, ct);
            // An AI or manual edge between the same pair already says it (one row per pair).
            var pairs = await _db.TaskDependencies.IgnoreQueryFilters().AsNoTracking()
                .Where(d => ids.Contains(d.TaskId))
                .Select(d => new { d.TaskId, d.DependsOnTaskId })
                .ToListAsync(ct);
            var taken = pairs.Select(p => (p.TaskId, p.DependsOnTaskId)).ToHashSet();

            foreach (var ((task, dependsOn), blockerKey) in wanted)
            {
                if (!tasks.TryGetValue(task, out var row) || !tasks.ContainsKey(dependsOn) || taken.Contains((task, dependsOn)))
                    continue;
                if (await TaskDependencyGraph.WouldCreateCycleAsync(_db, task, dependsOn, ct))
                {
                    // Waiting would deadlock both: say it instead.
                    logger.LogInformation("Tracker link {Task} blocked by {Key} would close a dependency cycle; shown as a note",
                        task, blockerKey);
                    (notes.TryGetValue(task, out var list) ? list : notes[task] = []).Add($"{blockerKey} (circular)");
                    continue;
                }
                _db.TaskDependencies.Add(new TaskDependency
                {
                    Id = Ulid.NewUlid(),
                    OrgId = row.OrgId,
                    TaskId = task,
                    DependsOnTaskId = dependsOn,
                    Reason = $"Blocked by {blockerKey} in the tracker.",
                    Source = DependencySource.Tracker,
                });
                await _db.SaveChangesAsync(ct);
            }
        }

        if (notes.Count > 0)
        {
            // Only tasks with a note now, or one to clear.
            var noteIds = notes.Keys.ToList();
            var withNote = notes.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key).ToList();
            var noted = await _db.Tasks.IgnoreQueryFilters()
                .Where(t => noteIds.Contains(t.Id) && t.ProjectId == projectId
                    && (withNote.Contains(t.Id) || t.ExternalBlockedBy != null))
                .ToListAsync(ct);
            if (noted.Count == 0)
                return;
            foreach (var t in noted)
            {
                var text = notes[t.Id] is { Count: > 0 } keys
                    ? string.Join(", ", keys.Distinct(StringComparer.OrdinalIgnoreCase))
                    : null;
                if (text is { Length: > MaxExternalBlockedByChars })
                    text = text[..MaxExternalBlockedByChars];
                if (t.ExternalBlockedBy != text)
                    t.ExternalBlockedBy = text;
            }
            await _db.SaveChangesAsync(ct);
        }
    }
}
