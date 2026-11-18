namespace Orkka.Modules.Tasks.Infrastructure.Services;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Dependency-graph traversal shared by the AI analyzer and the manual dependency endpoints.
/// </summary>
internal static class TaskDependencyGraph
{
    /// <summary>
    /// True if adding an edge taskId → dependsOnTaskId would create a cycle, i.e. taskId is
    /// already reachable from dependsOnTaskId by following existing dependency edges (BFS).
    /// </summary>
    public static async Task<bool> WouldCreateCycleAsync(
        TasksDbContext db, Ulid taskId, Ulid dependsOnTaskId, CancellationToken ct)
    {
        var visited = new HashSet<Ulid>();
        var queue = new Queue<Ulid>();
        queue.Enqueue(dependsOnTaskId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == taskId)
                return true;

            if (!visited.Add(current))
                continue;

            var deps = await db.TaskDependencies
                .AsNoTracking()
                .Where(d => d.TaskId == current)
                .Select(d => d.DependsOnTaskId)
                .ToListAsync(ct);

            foreach (var dep in deps)
                queue.Enqueue(dep);
        }

        return false;
    }
}
