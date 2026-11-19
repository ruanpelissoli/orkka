namespace Orkka.Modules.Trackers.PublicApi;

using Microsoft.EntityFrameworkCore;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Trackers.Contracts;
using Orkka.Modules.Trackers.Domain;

/// <summary>
/// Which tickets of one poll become new work — the ONE decision both the real poll
/// (<see cref="ReportAsync"/>) and the setup page's dry run (<see cref="PreviewIntakeAsync"/>)
/// make, so a preview can never promise something the poll then does differently:
/// <list type="number">
/// <item>Stories first (Story mode on): an untracked ticket in intake, assigned to the developer,
/// with at least one sub-task that is the developer's to do (<see cref="IsMemberCandidate"/>) —
/// its members are those sub-tasks in tracker order.</item>
/// <item>Then every other untracked ticket in intake assigned to the developer
/// (<see cref="IsNewTaskCandidate"/>) that no Story claimed.</item>
/// </list>
/// The poll then creates them; the preview only routes and lists them.
/// </summary>
internal sealed partial class TrackersApi
{
    /// <summary>A Story the poll would import, with its member sub-tasks in order.</summary>
    internal sealed record PlannedStory(TrackerTicket Story, IReadOnlyList<TrackerTicket> Members);

    /// <summary>What one report adds to the board: Stories (with their members) and plain tasks,
    /// in the order the poll imports them; and the tickets it reads that the board already tracks.</summary>
    internal sealed record IntakePlan(
        IReadOnlyList<PlannedStory> Stories,
        IReadOnlyList<TrackerTicket> Tasks,
        IReadOnlyList<TrackerTicket> Tracked,
        IReadOnlyList<TrackerTicket> Ignored);

    /// <summary>A ticket the board does not track yet becomes a task only from intake, and only
    /// when it is assigned to the developer.</summary>
    internal static bool IsNewTaskCandidate(TrackerTicket ticket) => ticket.InIntake && ticket.AssignedToMe;

    /// <summary>
    /// The intake decision for one report. <paramref name="linkedKeys"/> are the keys the
    /// project's ticket links already hold (tracked or ended) — the poll never makes a second
    /// task for one of them. Pure: reads nothing, writes nothing.
    /// </summary>
    internal static IntakePlan PlanIntake(
        IReadOnlyList<TrackerTicket> tickets, IReadOnlyCollection<string> linkedKeys,
        IReadOnlyCollection<string> intakeStatuses, bool storyMode)
    {
        var linked = new HashSet<string>(linkedKeys, StringComparer.OrdinalIgnoreCase);
        var report = new StoryReport(tickets, [], intakeStatuses);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stories = new List<PlannedStory>();

        if (storyMode)
        {
            foreach (var ticket in report.ByKey.Values)
            {
                if (linked.Contains(ticket.Key) || claimed.Contains(ticket.Key) || !IsStoryCandidate(ticket))
                    continue;
                var members = report.MemberChildren(ticket);
                if (members.Count == 0)
                    continue; // nothing under it for the developer to do: a task of its own
                stories.Add(new PlannedStory(ticket, members));
                claimed.Add(ticket.Key);
                foreach (var member in members)
                    claimed.Add(member.Key);
            }
        }

        var tasks = new List<TrackerTicket>();
        var tracked = new List<TrackerTicket>();
        var ignored = new List<TrackerTicket>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ticket in tickets)
        {
            if (!seen.Add(ticket.Key))
                continue;
            if (linked.Contains(ticket.Key))
                tracked.Add(ticket);
            else if (claimed.Contains(ticket.Key))
                continue;
            else if (IsNewTaskCandidate(ticket))
                tasks.Add(ticket);
            else
                ignored.Add(ticket);
        }

        return new IntakePlan(stories, tasks, tracked, ignored);
    }

    /// <summary>A ticket that may be imported as a Story: in intake, assigned to the developer,
    /// with sub-tasks (which of them are members is <see cref="StoryReport.MemberChildren"/>).</summary>
    internal static bool IsStoryCandidate(TrackerTicket ticket)
        => IsNewTaskCandidate(ticket) && ticket.ChildKeys is { Length: > 0 };

    // ── The dry run ─────────────────────────────────────────────────────────

    public async Task<TrackerPreviewAssignment?> BuildPreviewAssignmentAsync(Ulid projectId, CancellationToken ct = default)
    {
        var config = await db.ProjectConfigs.AsNoTracking()
            .Include(c => c.Epics)
            .FirstOrDefaultAsync(c => c.ProjectId == projectId, ct);
        if (config is null)
            return null;
        var connection = await db.Connections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == config.TrackerConnectionId, ct);
        if (connection is null)
            return null;

        // Exactly what the next poll would be handed — the tracked keys included, so tickets
        // already on the board read as tracked, not new. Read-only: nothing is leased or ended.
        var trackedLinks = await TrackedLinksAsync(config.Id, DateTimeOffset.UtcNow, ct, persist: false);
        var assignment = await AssignmentAsync(config, connection, trackedLinks, ct);
        return new TrackerPreviewAssignment(config.OwnerUserId, connection.Id, assignment);
    }

    public async Task<TrackerIntakePreview?> PreviewIntakeAsync(
        Ulid projectId, IReadOnlyList<TrackerTicket> tickets, CancellationToken ct = default)
    {
        var config = await db.ProjectConfigs.AsNoTracking()
            .Include(c => c.Epics)
            .FirstOrDefaultAsync(c => c.ProjectId == projectId, ct);
        if (config is null)
            return null;
        var connection = await db.Connections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == config.TrackerConnectionId, ct);
        if (connection is null)
            return null;

        var linkedKeys = await db.TicketLinks.AsNoTracking()
            .Where(l => l.ConfigId == config.Id)
            .Select(l => l.TicketKey)
            .ToListAsync(ct);
        var plan = PlanIntake(tickets, linkedKeys,
            Features.ProjectConfig.ProjectTrackerMappers.ParseIntakeStatuses(config.IntakeStatusesJson),
            config.StoryMode != 0);

        var repos = await projectsApi.ListReposAsync(config.ProjectId, ct);
        var assignee = string.IsNullOrWhiteSpace(connection.DisplayName) ? null : connection.DisplayName;

        var stories = new List<TrackerPreviewStory>(plan.Stories.Count);
        foreach (var story in plan.Stories)
        {
            var body = story.Story.Body;
            var members = new List<TrackerPreviewTicket>(story.Members.Count);
            foreach (var member in story.Members)
                members.Add(await PreviewTicketAsync(config, repos, member, assignee, ct));
            stories.Add(new TrackerPreviewStory(
                PreviewOf(story.Story, assignee, null, "story", null, []),
                (body is null ? null : TrackerBranchName.Render(config.BranchTemplate, MapType(body.Type), story.Story.Key, body.Title))
                    ?? $"orkka/{story.Story.Key}",
                [.. members]));
        }

        var tasks = new List<TrackerPreviewTicket>(plan.Tasks.Count);
        foreach (var ticket in plan.Tasks)
            tasks.Add(await PreviewTicketAsync(config, repos, ticket, assignee, ct));

        return new TrackerIntakePreview(
            tasks.Count + stories.Sum(s => s.Members.Length),
            [.. tasks],
            [.. stories],
            [.. plan.Tracked.Select(t => PreviewOf(t, t.AssignedToMe ? assignee : null, null, "tracked", null, []))],
            [.. plan.Ignored.Select(t => new TrackerPreviewSkipped(
                t.Key, TitleOf(t), t.Status ?? t.Body?.Status, t.Url,
                !t.AssignedToMe ? TrackerPreviewSkipReasons.NotAssignedToMe : TrackerPreviewSkipReasons.NotInIntake))]);
    }

    private async Task<TrackerPreviewTicket> PreviewTicketAsync(
        ProjectTrackerConfig config, IReadOnlyList<RepoSummary> repos, TrackerTicket ticket, string? assignee, CancellationToken ct)
    {
        if (ticket.Body is not { } body)
            return PreviewOf(ticket, assignee, null, "none", null, []);
        // The real routing, minus the repo router: asking the developer's AI CLI about every
        // ticket of a preview would cost a model call each. The poll asks it at import.
        var route = await RouteAsync(config, repos, ticket, body, ct, askRouter: false);
        var names = repos.ToDictionary(r => r.Id, r => r.Name);
        return PreviewOf(ticket, assignee,
            route.RepoId is { } repoId && names.TryGetValue(repoId, out var name) ? new RepoRef(repoId, name) : null,
            route.Source, route.Reason,
            [.. route.SpansRepoIds.Where(names.ContainsKey).Select(id => names[id])]);
    }

    private sealed record RepoRef(Ulid Id, string Name);

    private static TrackerPreviewTicket PreviewOf(
        TrackerTicket ticket, string? assignee, RepoRef? repo, string route, string? reason, string[] spans)
        => new(ticket.Key, TitleOf(ticket), ticket.Body?.Type, ticket.Status ?? ticket.Body?.Status, ticket.Url,
            assignee, repo?.Id, repo?.Name, spans, route, reason);

    private static string TitleOf(TrackerTicket ticket) => ticket.Body?.Title.Trim() ?? ticket.Key;
}
