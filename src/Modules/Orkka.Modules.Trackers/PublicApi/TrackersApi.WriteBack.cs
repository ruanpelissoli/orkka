namespace Orkka.Modules.Trackers.PublicApi;

using Microsoft.EntityFrameworkCore;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Trackers.Domain;
using Orkka.Modules.Trackers.Features.ProjectConfig;

/// <summary>
/// Status write-back — the only thing Orkka ever writes to a tracker. There is no event to hook:
/// each sync, the host works out how far every tracked ticket's task has got (started, forge
/// pull request opened, merged), compares it with the furthest status it has already moved the
/// ticket to, and hands the runner the next move the project's status map defines. The runner
/// applies it with the developer's token and acknowledges.
///
/// Moves only go forward, and only to statuses the project mapped. A move that keeps failing
/// (a workflow with no such transition, a missing permission) is given up after a few tries and
/// the task says so, rather than retrying forever.
/// </summary>
internal sealed partial class TrackersApi
{
    internal const int MaxTransitionAttempts = 5;

    // A move handed out is not handed out again until it is acknowledged or this passes — two
    // machines holding the same connection must not both apply it, and a failed one waits this
    // long before its retry.
    internal static readonly TimeSpan PendingWindow = TimeSpan.FromMinutes(2);

    // Asking the forge CLI whether a pull request merged costs a network call on the developer's
    // machine; a merged status a few minutes late is fine.
    internal static readonly TimeSpan PrCheckInterval = TimeSpan.FromMinutes(10);

    internal async Task<(TrackerTransition[] Transitions, TrackerPrCheck[] PrChecks)> PlanWriteBackAsync(
        Ulid orgId, string userId, IReadOnlyList<TrackerConnection> connections, CancellationToken ct)
    {
        var connById = connections.ToDictionary(c => c.Id);
        var configs = await db.ProjectConfigs.IgnoreQueryFilters()
            .Where(c => c.OrgId == orgId && c.OwnerUserId == userId && c.Enabled
                && connById.Keys.Contains(c.TrackerConnectionId))
            .ToDictionaryAsync(c => c.Id, ct);
        if (configs.Count == 0)
            return ([], []);

        var links = await db.TicketLinks.IgnoreQueryFilters()
            // Every tracked ticket, whoever it is assigned to now: work that carries on after a
            // reassignment still moves its ticket.
            .Where(l => configs.Keys.Contains(l.ConfigId) && l.TrackingEndedAt == null && l.SyncedPhase != TrackerPhase.Merged)
            .ToListAsync(ct);
        if (links.Count == 0)
            return ([], []);

        var states = (await tasksApi.GetTrackerTaskStatesAsync([.. links.Select(l => l.TaskId)], ct))
            .ToDictionary(s => s.TaskId);
        var groups = await GroupStatesAsync(links, ct);
        var checkedGroups = new HashSet<Ulid>();
        var repos = new Dictionary<Ulid, RepoSummary?>();
        var now = DateTimeOffset.UtcNow;
        var transitions = new List<TrackerTransition>();
        var checks = new List<TrackerPrCheck>();

        foreach (var link in links)
        {
            // A Story and its sub-tasks follow the story as a whole (TrackerGroupState): they
            // reach "PR opened" together, when the story's pull requests are marked ready.
            TrackerGroupState? group = link.GroupId is { } gid && groups.TryGetValue(gid, out var g) ? g : null;
            TrackerTaskState? state = null;
            if (group is null && (!states.TryGetValue(link.TaskId, out state) || state.IsArchived))
                continue;
            if (group is not null && !group.Merged && StoryLinkGone(link, group))
                continue;

            var config = configs[link.ConfigId];
            var map = ProjectTrackerMappers.ParseStatusMap(config.StatusMapJson);
            var desired = group is not null ? Desired(group, link) : Desired(state!, link);

            if (NextMove(link.SyncedPhase, desired, map) is { } move
                && !(link.PendingPhase == move.Phase && link.PendingSince > now - PendingWindow)
                && !(link.PendingPhase == move.Phase && link.TransitionAttempts >= MaxTransitionAttempts))
            {
                if (link.PendingPhase != move.Phase)
                {
                    link.TransitionAttempts = 0;
                    link.LastTransitionError = null;
                }
                link.PendingPhase = move.Phase;
                link.PendingSince = now;
                var github = link.Kind == TrackerKinds.GitHub;
                transitions.Add(new TrackerTransition(
                    TransitionId(link.Id, move.Phase), connById[config.TrackerConnectionId].ConnectionKey,
                    link.TicketKey, link.TicketId, move.Status, move.Phase.ToString(),
                    github ? config.BoardId : null, github ? config.StatusField : null));
            }

            if (group is not null)
            {
                // A Story's pull requests (one per repository) are asked about once per group, from
                // the Story's own link: merged is what settles the story, closed is what makes a
                // late sub-task a task of its own.
                if (link.Role == TicketRole.Story && checkedGroups.Add(group.GroupId)
                    && (link.PrCheckedAt is not { } groupCheckedAt || groupCheckedAt < now - PrCheckInterval))
                {
                    foreach (var branch in group.Branches.Where(b => b.ForgePrUrl is not null
                                 && b.ForgeState is not ("merged" or "closed")))
                    {
                        if (!repos.TryGetValue(branch.RepoId, out var branchRepo))
                            repos[branch.RepoId] = branchRepo = await projectsApi.GetRepoAsync(branch.RepoId, ct);
                        if (branchRepo is null)
                            continue;
                        checks.Add(new TrackerPrCheck(
                            GroupCheckId(group.GroupId, branch.RepoId), branchRepo.GitUrl,
                            branchRepo.ForgeKind.ToString().ToLowerInvariant(), branchRepo.CliAccount, branch.ForgePrUrl!));
                        link.PrCheckedAt = now;
                    }
                }
                continue;
            }

            // "Merged" for a remote repository is the forge's to say: the developer's team merges
            // the pull request there. Ask their CLI now and then, only while it matters.
            if (state!.PublishedPrUrl is { } prUrl
                && !string.IsNullOrWhiteSpace(map.Merged)
                && link.PrState is not ("merged" or "closed")
                && (link.PrCheckedAt is not { } checkedAt || checkedAt < now - PrCheckInterval))
            {
                // The task's repository now: a routing question answered after import set it there.
                var repoId = state.RepoId ?? link.RepoId;
                RepoSummary? repo = null;
                if (repoId is { } rid && !repos.TryGetValue(rid, out repo))
                    repos[rid] = repo = await projectsApi.GetRepoAsync(rid, ct);
                if (repo is not null)
                {
                    checks.Add(new TrackerPrCheck(
                        link.Id.ToString(), repo.GitUrl, repo.ForgeKind.ToString().ToLowerInvariant(),
                        repo.CliAccount, prUrl));
                    link.PrCheckedAt = now;
                }
            }
        }

        await db.SaveChangesAsync(ct);
        return ([.. transitions], [.. checks]);
    }

    public async Task<TrackerReportAck> AckTransitionsAsync(
        Ulid orgId, string userId, TrackerTransitionReport report, CancellationToken ct = default)
    {
        await AckGroupPrStatesAsync(orgId, userId, report.PrStates, ct);

        var linkIds = report.Transitions.Select(a => ParseTransitionId(a.TransitionId)?.LinkId)
            .Concat(report.PrStates.Select(p => Ulid.TryParse(p.CheckId, out var id) ? id : (Ulid?)null))
            .OfType<Ulid>()
            .ToHashSet();
        if (linkIds.Count == 0)
            return new TrackerReportAck(true);

        // Only the reporter's own tickets: a link belongs to a source the reporter owns.
        var links = await db.TicketLinks.IgnoreQueryFilters()
            .Where(l => linkIds.Contains(l.Id) && l.OrgId == orgId)
            .ToListAsync(ct);
        var configIds = links.Select(l => l.ConfigId).Distinct().ToList();
        var configs = await db.ProjectConfigs.IgnoreQueryFilters().AsNoTracking()
            .Where(c => configIds.Contains(c.Id) && c.OwnerUserId == userId)
            .ToDictionaryAsync(c => c.Id, ct);
        var owned = links.Where(l => configs.ContainsKey(l.ConfigId)).ToDictionary(l => l.Id);

        foreach (var ack in report.Transitions)
        {
            if (ParseTransitionId(ack.TransitionId) is not { } id
                || !owned.TryGetValue(id.LinkId, out var link)
                || link.PendingPhase != id.Phase)
                continue; // stale or foreign — the next sync hands out what is still due

            var map = ProjectTrackerMappers.ParseStatusMap(configs[link.ConfigId].StatusMapJson);
            var status = StatusFor(map, id.Phase) ?? id.Phase.ToString();
            var tracker = TrackerName(link.Kind);

            if (ack.Ok)
            {
                if (id.Phase > link.SyncedPhase)
                {
                    link.SyncedPhase = id.Phase;
                    link.SyncedAt = DateTimeOffset.UtcNow;
                }
                link.PendingPhase = null;
                link.PendingSince = null;
                link.TransitionAttempts = 0;
                link.LastTransitionError = null;
                link.RemoteStatus = status;
                await tasksApi.SetTrackerSyncErrorAsync(link.TaskId, null, ct);
                await tasksApi.AddTaskMessageAsync(link.TaskId, "system",
                    $"Moved {link.TicketKey} to “{status}” in {tracker}.", MessageType.SystemEvent, ct: ct);
                continue;
            }

            link.TransitionAttempts++;
            link.LastTransitionError = ack.Error is { Length: > 1000 } tooLong ? tooLong[..1000] : ack.Error;
            // PendingSince stays: the retry waits out the window instead of hammering the tracker.
            if (link.TransitionAttempts == MaxTransitionAttempts)
            {
                await tasksApi.SetTrackerSyncErrorAsync(link.TaskId,
                    $"Couldn't move {link.TicketKey} to “{status}” in {tracker}", ct);
                await tasksApi.AddTaskMessageAsync(link.TaskId, "system",
                    $"Couldn't move {link.TicketKey} to “{status}” in {tracker} after {MaxTransitionAttempts} tries: "
                    + $"{ack.Error ?? "the tracker refused"}. Move it by hand, or fix the status map in the "
                    + "project's ticket source settings.",
                    MessageType.SystemEvent, ct: ct);
            }
        }

        foreach (var pr in report.PrStates)
        {
            if (Ulid.TryParse(pr.CheckId, out var linkId) && owned.TryGetValue(linkId, out var link)
                && pr.State is { Length: > 0 } state)
                link.PrState = state;
        }

        await db.SaveChangesAsync(ct);
        return new TrackerReportAck(true);
    }

    /// <summary>How far the task has got. Merged: its forge pull request merged. PR opened:
    /// published, or landed in a folder (a folder landing counts as merged too). Then Orkka's
    /// review lane, the build, the first agent run — and a task that exists is picked up.</summary>
    internal static TrackerPhase Desired(TrackerTaskState state, TicketLink link)
        => state.LandedInFolder || link.PrState == "merged" ? TrackerPhase.Merged
            : state.PublishedPrUrl is not null ? TrackerPhase.PrOpened
            : state.InReview ? TrackerPhase.InReview
            : state.Developing ? TrackerPhase.Developing
            : state.Started ? TrackerPhase.Planning
            : TrackerPhase.PickedUp;

    /// <summary>
    /// How far a Story (or one of its sub-tasks) has got. Merged: every story pull request merged
    /// (or it landed in a folder). PR opened: only when the story's pull requests were marked
    /// ready, so the Story and every sub-task move to it together. Before that, the furthest step
    /// any of the link's members reached — all members for the Story, the sub-task's own for a
    /// sub-task.
    /// </summary>
    internal static TrackerPhase Desired(TrackerGroupState group, TicketLink link)
    {
        if (group.Merged)
            return TrackerPhase.Merged;
        if (group.ReadyAt is not null)
            return TrackerPhase.PrOpened;
        var members = MembersFor(link, group).Where(m => !m.IsArchived).ToList();
        return members.Any(m => m.InReview || m.Landed) ? TrackerPhase.InReview
            : members.Any(m => m.Developing) ? TrackerPhase.Developing
            : members.Any(m => m.Planning) ? TrackerPhase.Planning
            : TrackerPhase.PickedUp;
    }

    internal static string GroupCheckId(Ulid groupId, Ulid repoId) => $"g:{groupId}:{repoId}";

    /// <summary>The Story pull-request states the runner read: stored on the story's repository
    /// rows, for the reporter's own stories only.</summary>
    private async Task AckGroupPrStatesAsync(
        Ulid orgId, string userId, IReadOnlyList<TrackerPrState> states, CancellationToken ct)
    {
        foreach (var pr in states)
        {
            var parts = pr.CheckId.Split(':');
            if (parts.Length != 3 || parts[0] != "g" || pr.State is not { Length: > 0 } state
                || !Ulid.TryParse(parts[1], out var groupId) || !Ulid.TryParse(parts[2], out var repoId))
                continue;

            var owned = await db.TicketLinks.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.GroupId == groupId && l.OrgId == orgId)
                .Join(db.ProjectConfigs.IgnoreQueryFilters(), l => l.ConfigId, c => c.Id, (l, c) => c.OwnerUserId)
                .AnyAsync(owner => owner == userId, ct);
            if (owned)
                await tasksApi.SetGroupBranchForgeStateAsync(groupId, repoId, state, ct);
        }
    }

    /// <summary>The furthest mapped phase past what was already synced, up to the desired one.
    /// An unmapped phase is skipped, never a reason to stop: a project that maps only "PR opened"
    /// still gets that move.</summary>
    internal static (TrackerPhase Phase, string Status)? NextMove(
        TrackerPhase synced, TrackerPhase desired, TrackerStatusMap map)
    {
        for (var phase = desired; phase > synced; phase--)
        {
            if (StatusFor(map, phase) is { } status)
                return (phase, status);
        }
        return null;
    }

    private static string? StatusFor(TrackerStatusMap map, TrackerPhase phase)
    {
        var status = phase switch
        {
            TrackerPhase.PickedUp => map.PickedUp,
            TrackerPhase.Planning => map.Planning,
            TrackerPhase.Developing => map.Developing,
            TrackerPhase.InReview => map.InReview,
            TrackerPhase.PrOpened => map.PrOpened,
            TrackerPhase.Merged => map.Merged,
            _ => null,
        };
        return string.IsNullOrWhiteSpace(status) ? null : status;
    }

    internal static string TransitionId(Ulid linkId, TrackerPhase phase) => $"{linkId}:{phase}";

    private static (Ulid LinkId, TrackerPhase Phase)? ParseTransitionId(string value)
    {
        var parts = value.Split(':');
        return parts.Length == 2 && Ulid.TryParse(parts[0], out var linkId)
            && Enum.TryParse<TrackerPhase>(parts[1], out var phase)
            ? (linkId, phase)
            : null;
    }
}
