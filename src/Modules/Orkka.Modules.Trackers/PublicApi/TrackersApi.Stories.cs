namespace Orkka.Modules.Trackers.PublicApi;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Trackers.Domain;

/// <summary>
/// Stories (the project's <c>StoryMode</c> on): a ticket assigned to the developer, in intake,
/// with at least one open sub-task becomes a Story — a task group whose members are its
/// sub-tasks in tracker order, worked one after another on one shared branch per repository
/// and published as one pull request. The Story link points at the group; each sub-task link at
/// its member.
///
/// Sub-tasks the tracker adds later join at the end of the chain (turning a ready pull request
/// back into a draft until they land); once the Story's pull request merged or closed they are
/// worked on their own, with the Story's context. A sub-task assigned to the developer whose
/// Story is someone else's is a task of its own with the Story's title and description as
/// "Parent story context". A Story with no open sub-tasks is just a task.
/// </summary>
internal sealed partial class TrackersApi
{
    /// <summary>One report, indexed for the Story pass.</summary>
    private sealed class StoryReport(IReadOnlyList<TrackerTicket> tickets, Dictionary<string, TicketLink> links)
    {
        public Dictionary<string, TrackerTicket> ByKey { get; } = tickets
            .GroupBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, TicketLink> Links { get; } = links;

        /// <summary>Keys linked by the Story pass this poll — the main pass leaves them alone.</summary>
        public HashSet<string> JustImported { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The Story's sub-tasks this report carries with their text, not done, in order.</summary>
        public List<TrackerTicket> OpenChildren(TrackerTicket story)
            => [.. (story.ChildKeys ?? [])
                .Select(k => ByKey.TryGetValue(k, out var c) ? c : null)
                .OfType<TrackerTicket>()
                .Where(c => c.Body is not null && c.StatusCategory != "done")];

        /// <summary>
        /// Sub-tasks with no member yet that are not known to be done: reported open (their
        /// member could not be made), or new under the Story and not read yet — asked for by key
        /// on the next poll. A key asked for last time and still not reported does not exist.
        /// </summary>
        public int PendingCount(TrackerTicket story, IReadOnlyCollection<string> previousChildren)
            => (story.ChildKeys ?? []).Count(k => !Links.ContainsKey(k)
                && (ByKey.TryGetValue(k, out var c)
                    ? c.StatusCategory != "done"
                    : !previousChildren.Contains(k, StringComparer.OrdinalIgnoreCase)));
    }

    private async Task<int> IngestStoriesAsync(ImportContext router, StoryReport stories, string tracker, CancellationToken ct)
    {
        var count = 0;
        foreach (var ticket in stories.ByKey.Values)
        {
            if (stories.Links.ContainsKey(ticket.Key) || !ticket.InIntake || !ticket.AssignedToMe
                || ticket.ChildKeys is not { Length: > 0 })
                continue;
            var open = stories.OpenChildren(ticket);
            if (open.Count == 0)
                continue; // a Story with nothing open to do is worked as a task of its own

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
            if (!stories.ByKey.TryGetValue(link.TicketKey, out var ticket))
                continue;
            try
            {
                count += await SyncStoryChildrenAsync(router, link, ticket, stories, tracker, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not sync the sub-tasks of story {Key}", link.TicketKey);
            }
        }
        return count;
    }

    private async Task<bool> ImportStoryAsync(
        ImportContext router, TrackerTicket ticket, IReadOnlyList<TrackerTicket> open,
        StoryReport stories, string tracker, CancellationToken ct)
    {
        if (ticket.Body is not { } body)
            return false;
        var config = router.Config;
        router.Repos ??= await projectsApi.ListReposAsync(config.ProjectId, ct);

        var specs = new List<StorySubTaskSpec>(open.Count);
        foreach (var child in open)
            specs.Add(await SubTaskSpecAsync(router, child, ct));

        var branch = TrackerBranchName.Render(config.BranchTemplate, MapType(body.Type), ticket.Key, body.Title)
            ?? $"orkka/{ticket.Key}";
        var result = await tasksApi.CreateTrackerStoryAsync(new TrackerStorySpec(
            config.OrgId, config.ProjectId, config.OwnerUserId, ticket.Key, ticket.Url,
            TaskTitle(ticket.Key, body.Title), TaskDescription(body), branch, specs, EpicLabel(config, ticket)), ct);
        if (result.Members.Count == 0)
            return false;

        var storyLink = NewLink(router, ticket, body, result.Members[0].TaskId, TicketRole.Story, null, null);
        storyLink.GroupId = result.GroupId;
        storyLink.ChildKeysJson = JsonSerializer.Serialize(ticket.ChildKeys ?? [], Json);
        db.TicketLinks.Add(storyLink);
        stories.Links[ticket.Key] = storyLink;
        stories.JustImported.Add(ticket.Key);
        await AddCommentsAsync(storyLink, ticket, tracker, ct);

        foreach (var child in open)
        {
            var members = result.Members
                .Where(m => string.Equals(m.SubTaskKey, child.Key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (members.Count > 0)
                await LinkSubTaskAsync(router, child, members, result.GroupId, stories, tracker, ct);
        }

        await tasksApi.SetStoryPendingChildrenAsync(result.GroupId, stories.PendingCount(ticket, []), ct);
        await tasksApi.AddTaskMessageAsync(result.Members[0].TaskId, "system",
            $"{ticket.Key} is a Story with {open.Count} open sub-task(s) in {tracker}. They are worked in order on "
            + $"`{branch}`, one pull request for the whole story: a draft after the first lands, ready for review "
            + "after the last.",
            MessageType.SystemEvent, ct: ct);
        return true;
    }

    /// <summary>A Story Orkka already works: sub-tasks added since join at the end.</summary>
    private async Task<int> SyncStoryChildrenAsync(
        ImportContext router, TicketLink link, TrackerTicket ticket, StoryReport stories, string tracker, CancellationToken ct)
    {
        var groupId = link.GroupId!.Value;
        var previous = ParseKeys(link.ChildKeysJson);
        if (ticket.ChildKeys is { } reported)
            link.ChildKeysJson = JsonSerializer.Serialize(reported, Json);

        var added = 0;
        foreach (var key in ticket.ChildKeys ?? [])
        {
            if (stories.Links.ContainsKey(key)
                || !stories.ByKey.TryGetValue(key, out var child)
                || child.Body is null || child.StatusCategory == "done")
                continue;

            router.Repos ??= await projectsApi.ListReposAsync(router.Config.ProjectId, ct);
            var spec = await SubTaskSpecAsync(router, child, ct);
            var appended = await tasksApi.AppendStoryMemberAsync(groupId, spec, ct);

            if (appended.Closed)
            {
                // The Story's pull request already merged or closed: the new sub-task is its own
                // task, carrying the Story's context.
                var story = await tasksApi.GetStoryAsync(groupId, ct);
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

            await LinkSubTaskAsync(router, child, appended.Members, groupId, stories, tracker, ct);
            added++;
        }

        await tasksApi.SetStoryPendingChildrenAsync(groupId, stories.PendingCount(ticket, previous), ct);
        return added;
    }

    private async Task LinkSubTaskAsync(
        ImportContext router, TrackerTicket child, IReadOnlyList<StoryMemberRef> members, Ulid groupId,
        StoryReport stories, string tracker, CancellationToken ct)
    {
        var config = router.Config;
        if (stories.Links.TryGetValue(child.Key, out var existing))
        {
            // Imported on its own before its Story was the developer's, and adopted untouched.
            existing.Role = TicketRole.SubTask;
            existing.GroupId = groupId;
            existing.ParentTitle = null;
            existing.ParentDescription = null;
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

        foreach (var member in members.Where(m => m.Created))
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
    };

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
        if ((ticket.Status ?? ticket.Body?.Status) is { } status)
            link.RemoteStatus = Truncate(status, 200);

        if (ticket.Body is { } body && ticket.ContentHash != link.ContentHash)
        {
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
    /// with no member yet, and the Story of a sub-task worked on its own whose context is wanted.</summary>
    internal async Task<IReadOnlyList<string>> ExtraKnownKeysAsync(Ulid configId, CancellationToken ct)
    {
        var rows = await db.TicketLinks.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.ConfigId == configId)
            .Select(l => new { l.TicketKey, l.Role, l.ChildKeysJson, l.ParentKey, l.ParentTitle, l.TrackingEndedAt })
            .ToListAsync(ct);
        var linked = rows.Select(r => r.TicketKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var live = rows.Where(r => r.TrackingEndedAt == null).ToList();

        return [.. live.Where(r => r.Role == TicketRole.Story).SelectMany(r => ParseKeys(r.ChildKeysJson))
            .Concat(live.Where(r => r.ParentTitle == "" && r.ParentKey is not null).Select(r => r.ParentKey!))
            .Where(k => !linked.Contains(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(200)];
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

    /// <summary>A ticket whose direct parent is a Story (not an epic / feature / initiative).</summary>
    internal static bool WantsParentContext(TrackerTicket ticket)
        => ticket.ParentKey is { Length: > 0 } parent
            && !string.Equals(parent, ticket.EpicKey, StringComparison.OrdinalIgnoreCase)
            && ticket.ParentType is { Length: > 0 } type
            && !type.Contains("epic", StringComparison.OrdinalIgnoreCase)
            && !type.Contains("feature", StringComparison.OrdinalIgnoreCase)
            && !type.Contains("initiative", StringComparison.OrdinalIgnoreCase);

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
