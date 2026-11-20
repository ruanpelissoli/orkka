namespace Orkka.Modules.Trackers.PublicApi;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Trackers.Domain;

/// <summary>
/// Stories (the project's <c>StoryMode</c> on): a ticket assigned to the developer, in intake,
/// with at least one sub-task for the developer to do becomes a Story — a task group whose
/// members are those sub-tasks in tracker order, worked one after another on one shared branch
/// per repository and published as one pull request. The Story link points at the group; each
/// sub-task link at its member.
///
/// Orkka only ever works tickets assigned to the developer: a sub-task is a member only while it
/// is assigned to them and not under way (<see cref="IsMemberCandidate"/>). Sub-tasks of
/// teammates, unassigned ones and ones already in progress / resolved / in test never become
/// members; a Story with none for the developer is worked as a task of its own. A member whose
/// sub-task stops being the developer's is taken out of the Story while nothing has started
/// (<see cref="ReleaseNotMineMembersAsync"/>), and joins its end again if it comes back.
///
/// Sub-tasks the tracker adds (or gives the developer) later join at the end of the chain
/// (turning a ready pull request back into a draft until they land); once the Story's pull
/// request merged or closed they are worked on their own, with the Story's context. A sub-task
/// assigned to the developer whose Story is someone else's is a task of its own with the Story's
/// title and description as "Parent story context".
/// </summary>
internal sealed partial class TrackersApi
{
    /// <summary>One report, indexed for the Story pass.</summary>
    private sealed class StoryReport(
        IReadOnlyList<TrackerTicket> tickets, Dictionary<string, TicketLink> links, IReadOnlyCollection<string> intakeStatuses)
    {
        public Dictionary<string, TrackerTicket> ByKey { get; } = tickets
            .GroupBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, TicketLink> Links { get; } = links;

        /// <summary>The source's intake statuses: a sub-task in one of them is not under way.</summary>
        public IReadOnlyCollection<string> IntakeStatuses { get; } = intakeStatuses;

        /// <summary>Keys linked by the Story pass this poll — the main pass leaves them alone.</summary>
        public HashSet<string> JustImported { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool IsCandidate(TrackerTicket child) => IsMemberCandidate(child, IntakeStatuses);

        /// <summary>The Story's sub-tasks this report carries with their text that are the
        /// developer's to do (<see cref="IsMemberCandidate"/>), in order.</summary>
        public List<TrackerTicket> MemberChildren(TrackerTicket story)
            => [.. (story.ChildKeys ?? [])
                .Select(k => ByKey.TryGetValue(k, out var c) ? c : null)
                .OfType<TrackerTicket>()
                .Where(c => c.Body is not null && IsCandidate(c))];

        /// <summary>
        /// Sub-tasks with no member yet that the Story still waits for: reported and the
        /// developer's to do (their member could not be made), or new under the Story and not
        /// read yet — asked for by key on the next poll, then judged like the rest. A sub-task
        /// read and not the developer's never becomes a member, so it is not waited for. A key
        /// asked for last time and still not reported does not exist.
        /// </summary>
        public int PendingCount(TrackerTicket story, IReadOnlyCollection<string> previousChildren,
            IReadOnlyCollection<string>? excluded = null)
            => (story.ChildKeys ?? []).Count(k => !Links.ContainsKey(k)
                && excluded?.Contains(k, StringComparer.OrdinalIgnoreCase) != true
                && (ByKey.TryGetValue(k, out var c)
                    ? IsCandidate(c)
                    : !previousChildren.Contains(k, StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// A sub-task Orkka may work as a Story member: assigned to the developer and not under way —
    /// its status in the tracker's to-do category (Jira "To Do", ADO Proposed), or one of the
    /// source's intake statuses. In progress, resolved, in test, in review and done are not.
    /// </summary>
    internal static bool IsMemberCandidate(TrackerTicket child, IReadOnlyCollection<string> intakeStatuses)
        => child.AssignedToMe && child.StatusCategory != "done" && NotUnderWay(child, intakeStatuses);

    /// <summary>The ticket's status is in the to-do category, or one of the intake statuses.</summary>
    internal static bool NotUnderWay(TrackerTicket ticket, IReadOnlyCollection<string> intakeStatuses)
        => ticket.StatusCategory == "todo"
            || ((ticket.Status ?? ticket.Body?.Status) is { } status
                && intakeStatuses.Contains(status.Trim(), StringComparer.OrdinalIgnoreCase));

    /// <param name="planned">The Stories <see cref="PlanIntake"/> found in this report — the same
    /// list the setup page's dry run shows.</param>
    private async Task<int> IngestStoriesAsync(
        ImportContext router, StoryReport stories, IReadOnlyList<PlannedStory> planned, string tracker, CancellationToken ct)
    {
        var count = 0;
        foreach (var (ticket, open) in planned)
        {
            // A Story's member imported just above may itself have sub-tasks; once linked it is
            // the earlier Story's (PlanIntake already left it out — this keeps the two in step).
            if (stories.Links.ContainsKey(ticket.Key))
                continue;

            try
            {
                if (await ImportStoryAsync(router, ticket, open, stories, tracker, ct))
                    count++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not ingest story {Key} for source {ConfigId}", ticket.Key, router.Config.Id);
            }
        }

        foreach (var link in stories.Links.Values.Where(l => l.Role == TicketRole.Story && l.TrackingEndedAt == null
                     && l.GroupId is not null && !stories.JustImported.Contains(l.TicketKey)).ToList())
        {
            try
            {
                // Members whose sub-task is not the developer's to do leave first, so the Story's
                // order and pending count never wait for them.
                if (await ReleaseNotMineMembersAsync(link, stories, tracker, ct))
                    continue; // nothing of the developer's left: the Story ticket is a task of its own
                if (!stories.ByKey.TryGetValue(link.TicketKey, out var ticket))
                    continue;
                count += await SyncStoryChildrenAsync(router, link, ticket, stories, tracker, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not sync the sub-tasks of story {Key}", link.TicketKey);
            }
        }
        return count;
    }

    /// <param name="excluded">An import by hand: the sub-tasks the developer left unticked —
    /// never members, never waited for. Null for intake.</param>
    private async Task<bool> ImportStoryAsync(
        ImportContext router, TrackerTicket ticket, IReadOnlyList<TrackerTicket> open,
        StoryReport stories, string tracker, CancellationToken ct, IReadOnlyCollection<string>? excluded = null)
    {
        if (ticket.Body is not { } body)
            return false;
        var config = router.Config;
        router.Repos ??= await projectsApi.ListReposAsync(config.ProjectId, ct);

        var specs = new List<StorySubTaskSpec>(open.Count);
        var revived = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in open)
        {
            specs.Add(await SubTaskSpecAsync(router, child, ct));
            if (await ReviveReleasedAsync(child, ticket.Key, stories, tracker, ct))
                revived.Add(child.Key);
        }

        var branch = TrackerBranchName.Render(config.BranchTemplate, MapType(body.Type), ticket.Key, body.Title)
            ?? $"orkka/{ticket.Key}";
        var result = await tasksApi.CreateTrackerStoryAsync(new TrackerStorySpec(
            config.OrgId, config.ProjectId, config.OwnerUserId, ticket.Key, ticket.Url,
            TaskTitle(ticket.Key, body.Title), TaskDescription(body), branch, specs, EpicLabel(config, ticket)), ct);
        if (result.Members.Count == 0)
        {
            // Every sub-task was already work of its own that cannot join (started, or archived
            // by a person): no story — an empty group would only linger on the board.
            if (result.Created)
                await tasksApi.CancelStoryAsync(result.GroupId, ct);
            return false;
        }

        var storyLink = NewLink(router, ticket, body, result.Members[0].TaskId, TicketRole.Story, null, null);
        storyLink.GroupId = result.GroupId;
        storyLink.ChildKeysJson = JsonSerializer.Serialize(ticket.ChildKeys ?? [], Json);
        if (excluded is { Count: > 0 })
            storyLink.ExcludedChildKeysJson = JsonSerializer.Serialize(excluded.ToArray(), Json);
        db.TicketLinks.Add(storyLink);
        stories.Links[ticket.Key] = storyLink;
        stories.JustImported.Add(ticket.Key);
        await AddCommentsAsync(storyLink, ticket, tracker, ct);

        foreach (var child in open)
        {
            var members = result.Members
                .Where(m => string.Equals(m.SubTaskKey, child.Key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (members.Count > 0)
                await LinkSubTaskAsync(router, child, members, result.GroupId, stories, tracker, ct, revived.Contains(child.Key));
        }

        await tasksApi.SetStoryPendingChildrenAsync(result.GroupId, stories.PendingCount(ticket, [], excluded), ct);
        await tasksApi.AddTaskMessageAsync(result.Members[0].TaskId, "system",
            excluded is null
                ? $"{ticket.Key} is a Story with {open.Count} sub-task(s) assigned to you in {tracker} that have not been started "
                    + "(sub-tasks of other people, and ones already under way, are left alone). Yours are worked in order on "
                    + $"`{branch}`, one pull request for the whole story: a draft after the first lands, ready for review "
                    + "after the last."
                : $"{ticket.Key} was imported from {tracker} as a Story with the {open.Count} sub-task(s) you picked. They are "
                    + $"worked in order on `{branch}`, one pull request for the whole story: a draft after the first lands, "
                    + "ready for review after the last.",
            MessageType.SystemEvent, ct: ct);
        return true;
    }

    /// <summary>A Story Orkka already works: sub-tasks added since — or given to the developer
    /// since — join at the end.</summary>
    private async Task<int> SyncStoryChildrenAsync(
        ImportContext router, TicketLink link, TrackerTicket ticket, StoryReport stories, string tracker, CancellationToken ct)
    {
        var groupId = link.GroupId!.Value;
        var previous = ParseKeys(link.ChildKeysJson);
        var excluded = ParseKeys(link.ExcludedChildKeysJson);
        if (ticket.ChildKeys is { } reported)
            link.ChildKeysJson = JsonSerializer.Serialize(reported, Json);

        var added = 0;
        foreach (var key in ticket.ChildKeys ?? [])
        {
            if (excluded.Contains(key, StringComparer.OrdinalIgnoreCase))
                continue; // left unticked when the Story was imported by hand
            if (!stories.ByKey.TryGetValue(key, out var child) || child.Body is null || !stories.IsCandidate(child))
                continue;
            // Linked already: tracked, or archived by a person (left alone). Only a sub-task Orkka
            // itself let go of (taken out of the story, or retired) comes back.
            if (stories.Links.TryGetValue(key, out var known) && (known.TrackingEndedAt is null || known.AssignedToOwner))
                continue;

            router.Repos ??= await projectsApi.ListReposAsync(router.Config.ProjectId, ct);
            var spec = await SubTaskSpecAsync(router, child, ct);
            var revived = await ReviveReleasedAsync(child, link.TicketKey, stories, tracker, ct);
            var appended = await tasksApi.AppendStoryMemberAsync(groupId, spec, ct);

            if (appended.Closed)
            {
                // The Story's pull request already merged or closed: the new sub-task is its own
                // task, carrying the Story's context.
                var story = await tasksApi.GetStoryAsync(groupId, ct);
                if (known is not null)
                {
                    // Its task exists (just revived): it is worked on its own from here.
                    known.Role = TicketRole.Standalone;
                    known.GroupId = null;
                    known.ParentKey = link.TicketKey;
                    known.ParentTitle = Truncate(story?.Title ?? link.TicketKey, 490);
                    known.ParentDescription = Truncate(story?.Description, 19_000);
                    known.TrackingEndedAt = null;
                    known.AssignedToOwner = true;
                    stories.JustImported.Add(key);
                    added++;
                    continue;
                }
                if (await ImportAsync(router, child, ct, (link.TicketKey, story?.Title, story?.Description)) is { } standalone)
                {
                    db.TicketLinks.Add(standalone);
                    stories.Links[key] = standalone;
                    stories.JustImported.Add(key);
                    added++;
                }
                continue;
            }

            if (appended.Members.Count == 0)
                continue;

            if (appended.WasReady)
            {
                var undo = storyPullRequests is null
                    ? null
                    : await storyPullRequests.ReturnToDraftAsync(groupId, ct);
                await tasksApi.AddTaskMessageAsync(link.TaskId, "system",
                    undo is { IsSuccess: false }
                        ? $"{key} was added to {link.TicketKey}. The story's pull request could not be turned back into a draft "
                          + $"({undo.Error}) — it will be marked ready again once {key} lands."
                        : $"{key} was added to {link.TicketKey}: the story's pull request is a draft again until {key} lands.",
                    MessageType.SystemEvent, ct: ct);
            }
            else
            {
                await tasksApi.AddTaskMessageAsync(link.TaskId, "system",
                    $"{key} was added to {link.TicketKey} in {tracker} — it joins the end of the story.",
                    MessageType.SystemEvent, ct: ct);
            }

            await LinkSubTaskAsync(router, child, appended.Members, groupId, stories, tracker, ct, revived);
            added++;
        }

        await tasksApi.SetStoryPendingChildrenAsync(groupId, stories.PendingCount(ticket, previous, excluded), ct);
        return added;
    }

    private async Task LinkSubTaskAsync(
        ImportContext router, TrackerTicket child, IReadOnlyList<StoryMemberRef> members, Ulid groupId,
        StoryReport stories, string tracker, CancellationToken ct, bool revived = false)
    {
        var config = router.Config;
        if (stories.Links.TryGetValue(child.Key, out var existing))
        {
            // Imported on its own before its Story was the developer's and adopted untouched, or
            // a sub-task Orkka let go of earlier that is the developer's again.
            existing.Role = TicketRole.SubTask;
            existing.GroupId = groupId;
            existing.TaskId = members[0].TaskId;
            existing.ParentKey = child.ParentKey ?? existing.ParentKey;
            existing.ParentTitle = null;
            existing.ParentDescription = null;
            existing.TrackingEndedAt = null;
            existing.AssignedToOwner = true;
        }
        else
        {
            var link = NewLink(router, child, child.Body!, members[0].TaskId, TicketRole.SubTask, members[0].RepoId, null);
            link.GroupId = groupId;
            db.TicketLinks.Add(link);
            stories.Links[child.Key] = link;
            await AddCommentsAsync(link, child, tracker, ct);
        }
        stories.JustImported.Add(child.Key);

        // A revived member is back in Drafting like a new one, so it is submitted like one.
        foreach (var member in members.Where(m => m.Created || revived))
        {
            if (member.RepoId is null)
            {
                await tasksApi.AskTrackerRepoAsync(member.TaskId, [.. (router.Repos ?? []).Select(r => r.Name)],
                    "", config.AutoSubmit, ct);
            }
            else if (config.AutoSubmit)
            {
                // Submitted now; the dependency on the previous member holds it in the queue
                // until that member is Completed.
                var submitted = await tasksApi.SubmitTaskAsync(config.OrgId, member.TaskId, locale: null, ct);
                if (!submitted.Ok)
                    await tasksApi.AddTaskMessageAsync(member.TaskId, "system",
                        $"Imported from {tracker} but not submitted: {submitted.Message}",
                        MessageType.SystemEvent, ct: ct);
            }
        }
    }

    /// <summary>
    /// A sub-task Orkka let go of earlier (taken out of its Story, or retired: tracking ended,
    /// <see cref="TicketLink.AssignedToOwner"/> false) that is the developer's to do again: its
    /// task comes back to Drafting so it can rejoin the Story. False when there is nothing to revive.
    /// </summary>
    private async Task<bool> ReviveReleasedAsync(
        TrackerTicket child, string storyKey, StoryReport stories, string tracker, CancellationToken ct)
    {
        if (!stories.Links.TryGetValue(child.Key, out var link) || link.TrackingEndedAt is null || link.AssignedToOwner)
            return false;
        await tasksApi.ReviveTrackerTaskAsync(link.TaskId,
            $"{child.Key} is assigned to you in {tracker} and not started — it joins the story {storyKey} again.", ct);
        return true;
    }

    /// <summary>
    /// Orkka only works what is the developer's: a member whose sub-task is not assigned to them
    /// (or, for a ticket Orkka never moved itself, is already under way in the tracker) leaves the
    /// Story while nothing has started — archived and taken out of the chain, its dependents
    /// rewired to its predecessor, its link no longer tracked (and back when the sub-task is the
    /// developer's again). Started work is never touched: a member not assigned to the developer
    /// gets one note instead. When no member is left and nothing landed or was published, the
    /// Story group is cancelled and its link removed, so the Story ticket is worked as a task of
    /// its own — in this same poll, when it is still in intake and assigned to the developer.
    /// True when the Story was dissolved.
    /// </summary>
    private async Task<bool> ReleaseNotMineMembersAsync(TicketLink storyLink, StoryReport stories, string tracker, CancellationToken ct)
    {
        var groupId = storyLink.GroupId!.Value;
        var subLinks = stories.Links.Values
            .Where(l => l.Role == TicketRole.SubTask && l.GroupId == groupId && l.TrackingEndedAt == null)
            .ToList();
        TrackerGroupState? group = null;
        var released = false;
        foreach (var link in subLinks)
        {
            if (!stories.ByKey.TryGetValue(link.TicketKey, out var child) || child.StatusCategory == "done")
                continue; // not read this poll, or done (the main pass retires it if nothing started)
            if (link.Pinned)
                continue; // imported by hand: the developer chose it, whoever has it

            var mine = child.AssignedToMe;
            // Orkka's own write-back moves a member's ticket along, so its status only says
            // "someone else started this" while Orkka never moved it.
            var notUnderWay = link.SyncedPhase != TrackerPhase.None || link.PendingPhase is not null
                || NotUnderWay(child, stories.IntakeStatuses);
            if (mine && notUnderWay)
            {
                link.AssignedToOwner = true; // the developer's again: a later reassignment is noted again
                continue;
            }

            group ??= (await tasksApi.GetTrackerGroupStatesAsync([groupId], ct)).FirstOrDefault();
            if (group is null)
                return false;
            var members = MembersFor(link, group);
            if (members.Any(m => !m.IsArchived && m.Planning))
            {
                if (!mine && link.AssignedToOwner)
                {
                    link.AssignedToOwner = false;
                    await tasksApi.AddTaskMessageAsync(link.TaskId, "system",
                        $"{link.TicketKey} is not assigned to you in {tracker}; Orkka will not pick up more of it — "
                        + "archive this task if it isn't yours.",
                        MessageType.SystemEvent, ct: ct);
                }
                continue;
            }

            var reason = mine
                ? $"{link.TicketKey} is already under way in {tracker} (“{child.Status ?? child.Body?.Status}”), so it is not Orkka's to start."
                : $"{link.TicketKey} is not assigned to you in {tracker} — Orkka only works tickets assigned to you.";
            var held = false;
            IEnumerable<Ulid> taskIds = members.Count > 0 ? members.Select(m => m.TaskId) : [link.TaskId];
            foreach (var taskId in taskIds)
                held |= await tasksApi.ReleaseStoryMemberAsync(taskId, reason, ct) == TrackerTaskRetirement.Held;
            if (held)
                continue; // it started just now; the next poll treats it as started work

            link.TrackingEndedAt = DateTimeOffset.UtcNow;
            link.AssignedToOwner = false;
            link.Role = TicketRole.Standalone;
            link.GroupId = null;
            link.ParentKey = storyLink.TicketKey;
            link.ParentTitle = ""; // revived on its own later, it reads the Story for its context
            link.ParentDescription = null;
            link.PendingPhase = null;
            link.PendingSince = null;
            released = true;
        }
        if (!released)
            return false;

        group = (await tasksApi.GetTrackerGroupStatesAsync([groupId], ct)).FirstOrDefault();
        if (group is null)
            return false;
        var live = group.Members.Where(m => !m.IsArchived).OrderBy(m => m.StoryOrder).ToList();
        if (live.Count > 0)
        {
            // The Story ticket's notes and comments land on its first member: never on one taken out.
            if (!group.Members.Any(m => m.TaskId == storyLink.TaskId))
                storyLink.TaskId = live[0].TaskId;
            return false;
        }

        // Nothing of the developer's left. Work that landed or was published stays a Story and
        // ends the way an all-archived story does; otherwise the group goes.
        if (!stories.ByKey.ContainsKey(storyLink.TicketKey) || group.Members.Any(m => m.Landed)
            || group.ReadyAt is not null || group.Branches.Any(b => b.ForgePrUrl is not null))
            return false;
        await tasksApi.CancelStoryAsync(groupId, ct);
        db.TicketLinks.Remove(storyLink);
        stories.Links.Remove(storyLink.TicketKey);
        // Saved now, so the key is free for the Story ticket's own link later in this poll.
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<StorySubTaskSpec> SubTaskSpecAsync(ImportContext router, TrackerTicket child, CancellationToken ct)
    {
        var body = child.Body!;
        var route = await RouteAsync(router.Config, router.Repos ?? [], child, body, ct);
        IReadOnlyList<Ulid> repoIds = route.SpansRepoIds.Count >= 2 ? route.SpansRepoIds
            : route.RepoId is { } repoId ? [repoId] : [];
        return new StorySubTaskSpec(
            child.Key, child.Url, ExternalRef(router.Connection, child.Key),
            TaskTitle(child.Key, body.Title), TaskDescription(body),
            MapType(body.Type), MapPriority(body.Priority), [.. body.Labels.Take(10)],
            repoIds, route.Subpath);
    }

    private static TicketLink NewLink(
        ImportContext router, TrackerTicket ticket, TrackerTicketBody body, Ulid taskId, TicketRole role,
        Ulid? repoId, string? subpath) => new()
    {
        Id = Ulid.NewUlid(),
        OrgId = router.Config.OrgId,
        ConfigId = router.Config.Id,
        ProjectId = router.Config.ProjectId,
        TaskId = taskId,
        Kind = router.Connection.Kind,
        SiteUrl = router.Connection.SiteUrl,
        TicketKey = ticket.Key,
        TicketId = ticket.Id,
        TicketUrl = ticket.Url,
        EpicKey = ticket.EpicKey,
        ParentKey = ticket.ParentKey,
        Role = role,
        RepoId = repoId,
        Subpath = subpath,
        ContentHash = ticket.ContentHash,
        RemoteStatus = Truncate(ticket.Status ?? body.Status, 200),
        AttachmentsJson = JsonSerializer.Serialize(body.Attachments, Json),
        LastSeenAt = DateTimeOffset.UtcNow,
        Level = ticket.Level,
        BlockedByKeysJson = BlockedByJson(ticket),
    };

    /// <summary>The hierarchy and dependency links as of this report — refreshed on every poll of
    /// a tracked ticket.</summary>
    private static void ApplyHierarchy(TicketLink link, TrackerTicket ticket)
    {
        if (ticket.Level != TicketLevel.Unknown)
            link.Level = ticket.Level;
        link.BlockedByKeysJson = BlockedByJson(ticket);
    }

    private static string? BlockedByJson(TrackerTicket ticket)
        => ticket.BlockedByKeys is { Length: > 0 } keys
            ? JsonSerializer.Serialize(keys.Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToArray(), Json)
            : null;

    private static string? EpicLabel(ProjectTrackerConfig config, TrackerTicket ticket)
    {
        var rule = ticket.EpicKey is { } epicKey
            ? config.Epics.FirstOrDefault(e => e.Kind == TrackerRuleKind.Epic
                && string.Equals(e.EpicKey, epicKey, StringComparison.OrdinalIgnoreCase))
            : null;
        return rule is { EpicTitle: { Length: > 0 } title } ? $"{rule.EpicKey} {title}" : ticket.EpicKey;
    }

    /// <summary>The Story ticket itself: status, text and comments sync onto its first member's
    /// thread. Its assignee is not watched — the sub-tasks carry the work.</summary>
    private async Task ApplyToStoryTicketAsync(TicketLink link, TrackerTicket ticket, string tracker, CancellationToken ct)
    {
        link.LastSeenAt = DateTimeOffset.UtcNow;
        link.TicketUrl = ticket.Url;
        link.EpicKey = ticket.EpicKey ?? link.EpicKey;
        link.ParentKey = ticket.ParentKey ?? link.ParentKey;
        ApplyHierarchy(link, ticket);
        if ((ticket.Status ?? ticket.Body?.Status) is { } status)
            link.RemoteStatus = Truncate(status, 200);

        if (ticket.Body is { } body && ticket.ContentHash != link.ContentHash)
        {
            if (link.GroupId is { } groupId)
                await tasksApi.SetStoryTitleAsync(groupId, TaskTitle(ticket.Key, body.Title), ct);
            link.AttachmentsJson = JsonSerializer.Serialize(body.Attachments, Json);
            await tasksApi.AddTaskMessageAsync(link.TaskId, "system",
                Truncate($"The story {ticket.Key} was updated in {tracker}. It now reads:\n\n### {body.Title}\n\n{TaskDescription(body)}",
                    MaxNoteChars)!,
                MessageType.SystemEvent, ct: ct);
            link.ContentHash = ticket.ContentHash;
        }

        await AddCommentsAsync(link, ticket, tracker, ct);
    }

    /// <summary>Keys to read on the next poll that no tracked link covers: a Story's sub-tasks
    /// with no member yet — new ones, ones not the developer's (yet), and ones Orkka let go of
    /// that may become the developer's again — and the Story of a sub-task worked on its own
    /// whose context is wanted.</summary>
    internal async Task<IReadOnlyList<string>> ExtraKnownKeysAsync(Ulid configId, CancellationToken ct)
    {
        var rows = await db.TicketLinks.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.ConfigId == configId)
            .Select(l => new { l.TicketKey, l.Role, l.ChildKeysJson, l.ExcludedChildKeysJson, l.ParentKey, l.ParentTitle, l.TrackingEndedAt, l.AssignedToOwner })
            .ToListAsync(ct);
        // Tickets asked for by key, with the sub-tasks picked for them — read on the very next poll.
        var imports = await db.Imports.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.ConfigId == configId && i.CompletedAt == null)
            .Select(i => new { i.TicketKey, i.SubTaskKeysJson })
            .ToListAsync(ct);
        // A link Orkka itself ended (AssignedToOwner false) does not cover its key; one a person
        // archived, or that merged, does.
        var linked = rows.Where(r => r.TrackingEndedAt == null || r.AssignedToOwner)
            .Select(r => r.TicketKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var live = rows.Where(r => r.TrackingEndedAt == null).ToList();

        var wanted = imports.SelectMany(i => ParseKeys(i.SubTaskKeysJson).Prepend(i.TicketKey))
            .Concat(live.Where(r => r.Role == TicketRole.Story)
                .SelectMany(r => ParseKeys(r.ChildKeysJson).Except(ParseKeys(r.ExcludedChildKeysJson), StringComparer.OrdinalIgnoreCase))
                .Where(k => !linked.Contains(k)))
            .Concat(live.Where(r => r.ParentTitle == "" && r.ParentKey is not null).Select(r => r.ParentKey!)
                .Where(k => !linked.Contains(k)));
        return [.. wanted
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(300)];
    }

    /// <summary>The report carried a Story read for a sub-task's context: store it on the links
    /// waiting for it.</summary>
    private static void FillParentContext(IEnumerable<TicketLink> links, TrackerTicket ticket)
    {
        if (ticket.Body is not { } body)
            return;
        foreach (var link in links.Where(l => l.ParentTitle == ""
                     && string.Equals(l.ParentKey, ticket.Key, StringComparison.OrdinalIgnoreCase)))
        {
            link.ParentTitle = Truncate(body.Title.Trim(), 490);
            link.ParentDescription = Truncate(TaskDescription(body), 19_000);
        }
    }

    /// <summary>A ticket whose direct parent is a Story (not an epic / feature / initiative): by
    /// the parent's level, or — from a runner that predates levels — by its type name.</summary>
    internal static bool WantsParentContext(TrackerTicket ticket)
        => ticket.ParentKey is { Length: > 0 } parent
            && !string.Equals(parent, ticket.EpicKey, StringComparison.OrdinalIgnoreCase)
            && ticket.ParentType is { Length: > 0 } type
            && ticket.ParentLevel switch
            {
                TicketLevel.Above => false,
                TicketLevel.Unknown => !type.Contains("epic", StringComparison.OrdinalIgnoreCase)
                    && !type.Contains("feature", StringComparison.OrdinalIgnoreCase)
                    && !type.Contains("initiative", StringComparison.OrdinalIgnoreCase),
                _ => true,
            };

    private async Task<Dictionary<Ulid, TrackerGroupState>> GroupStatesAsync(IEnumerable<TicketLink> links, CancellationToken ct)
    {
        var ids = links.Where(l => l.GroupId is not null).Select(l => l.GroupId!.Value).Distinct().ToList();
        return ids.Count == 0
            ? []
            : (await tasksApi.GetTrackerGroupStatesAsync(ids, ct)).ToDictionary(g => g.GroupId);
    }

    /// <summary>A Story link stops being tracked once every member is archived or every story pull
    /// request merged; a sub-task link once its own members are all archived.</summary>
    internal static bool StoryLinkGone(TicketLink link, TrackerGroupState group)
    {
        if (group.Merged)
            return true;
        var members = MembersFor(link, group);
        return members.Count == 0 || members.All(m => m.IsArchived);
    }

    /// <summary>The members a link answers for: all of them for the Story, the sub-task's own
    /// (one per repository) for a sub-task.</summary>
    internal static List<StoryMemberState> MembersFor(TicketLink link, TrackerGroupState group)
        => link.Role == TicketRole.Story
            ? [.. group.Members]
            : [.. group.Members.Where(m => string.Equals(m.SubTaskKey, link.TicketKey, StringComparison.OrdinalIgnoreCase))];

    /// <summary>Retires the link's task — every member of a sub-task worked in several
    /// repositories. True when tracking should end (nothing is still running).</summary>
    private async Task<bool> RetireLinkAsync(TicketLink link, string reason, CancellationToken ct)
    {
        IEnumerable<Ulid> taskIds = [link.TaskId];
        if (link.Role == TicketRole.SubTask && link.GroupId is { } groupId)
        {
            var group = (await tasksApi.GetTrackerGroupStatesAsync([groupId], ct)).FirstOrDefault();
            if (group is not null && MembersFor(link, group) is { Count: > 0 } members)
                taskIds = members.Select(m => m.TaskId);
        }

        var held = false;
        foreach (var taskId in taskIds)
            held |= await tasksApi.RetireTrackerTaskAsync(taskId, reason, ct) == TrackerTaskRetirement.Held;
        return !held;
    }

    private static string[] ParseKeys(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<string[]>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
