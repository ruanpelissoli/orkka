namespace Orkka.Modules.Tasks.Features.AgentStats;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;

/// <summary>
/// Per-agent operational counters for the Team page: runs, success rate, average duration
/// and the most recent error over a lookback window. Read-only, open to every org member
/// (the Team page itself is).
/// </summary>
public sealed class GetAgentStatsEndpoint(
    TasksDbContext db,
    IProjectsApi projectsApi,
    ICurrentUser currentUser,
    IProjectResolver projectResolver)
    : Endpoint<AgentStatsRequest, AgentStatsEnvelope>
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/agent-stats");
    }

    public override async Task HandleAsync(AgentStatsRequest req, CancellationToken ct)
    {
        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // The resolver lets a well-formed ULID through on access alone; confirm it exists in
        // this org so an unknown id reads as 404 rather than an empty stats page.
        var project = await projectsApi.GetProjectSummaryAsync(projectId, ct);
        if (project is null || project.OrgId != currentUser.OrgId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var (window, span) = ParseWindow(req.Window);
        var to = DateTimeOffset.UtcNow;
        var from = to - span;

        // Executions carry ProjectId only through their task; StartedAt is the anchor because a
        // Queued row has no start yet and should not count as a run.
        var rows = await db.TaskExecutions.AsNoTracking()
            .Where(e => e.OrgId == currentUser.OrgId
                        && e.Task.ProjectId == projectId
                        && e.AgentId != null
                        && e.StartedAt != null
                        && e.StartedAt >= from)
            .Select(e => new
            {
                AgentId = e.AgentId!.Value,
                e.Status,
                e.StartedAt,
                e.CompletedAt,
                e.ErrorMessage,
            })
            .ToListAsync(ct);

        // Aggregate in memory: the per-agent shape needs "last error" alongside counts, which is a
        // window function in SQL and a one-liner here; the window is bounded so the row count is small.
        var agents = rows
            .GroupBy(r => r.AgentId)
            .Select(g =>
            {
                var finished = g.Where(r => r.CompletedAt is not null && r.StartedAt is not null).ToList();
                var lastError = g
                    .Where(r => r.Status == ExecutionStatus.Failed && !string.IsNullOrWhiteSpace(r.ErrorMessage))
                    .OrderByDescending(r => r.StartedAt)
                    .FirstOrDefault();

                return new AgentStatsResponse(
                    g.Key,
                    Runs: g.Count(),
                    Succeeded: g.Count(r => r.Status == ExecutionStatus.Completed),
                    Failed: g.Count(r => r.Status == ExecutionStatus.Failed),
                    Running: g.Count(r => r.Status == ExecutionStatus.Running),
                    AvgDurationSeconds: finished.Count == 0
                        ? null
                        : Math.Round(finished.Average(r => (r.CompletedAt!.Value - r.StartedAt!.Value).TotalSeconds), 1),
                    LastRunAt: g.Max(r => r.StartedAt),
                    LastError: lastError is null ? null : Truncate(lastError.ErrorMessage!, 300),
                    LastErrorAt: lastError?.StartedAt);
            })
            .OrderByDescending(a => a.Runs)
            .ToList();

        await Send.OkAsync(new AgentStatsEnvelope(window, from, to, agents), ct);
    }

    /// <summary>Kept as the name this endpoint's tests and callers already use; the window
    /// vocabulary itself lives in <see cref="StatsWindow"/> so board metrics means the same
    /// thing by "7d" that this page does.</summary>
    internal static (string Window, TimeSpan Span) ParseWindow(string? raw) => StatsWindow.Parse(raw);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
