namespace Orkka.Modules.Trackers.PublicApi;

using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Trackers.Contracts;
using Orkka.Modules.Trackers.Domain;
using Orkka.Modules.Trackers.Infrastructure;

/// <summary>
/// The Trackers module's public surface: connections from the runner's Hello, and ingestion —
/// which ticket sources a runner polls, and what its reports do to the board.
///
/// Everything here runs in the Worker's runner hub, which has no signed-in user: the tenant
/// filter is off, so every query scopes by org and user by hand, and nothing in a report is
/// trusted to name a project — the config id is checked against the reporter's own.
/// </summary>
internal sealed partial class TrackersApi(
    TrackersDbContext db,
    ITasksApi tasksApi,
    IProjectsApi projectsApi,
    IRepoRouter repoRouter,
    ILogger<TrackersApi> logger,
    // Optional so tests that build the ingestion alone keep compiling; DI always supplies it.
    Orkka.Modules.SourceControl.Contracts.IStoryPullRequests? storyPullRequests = null) : ITrackersApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // A comment or an update note larger than this is cut — the full text is in the tracker.
    private const int MaxNoteChars = 6_000;

    public async Task UpsertRunnerConnectionsAsync(
        Ulid orgId, string userId, string machineName,
        IReadOnlyList<RunnerTrackerConnection> connections, CancellationToken ct = default)
    {
        var existing = await db.Connections.IgnoreQueryFilters()
            .Where(c => c.OrgId == orgId && c.UserId == userId)
            .ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var c in connections)
        {
            if (string.IsNullOrWhiteSpace(c.Id) || !reported.Add(c.Id))
                continue;

            var row = existing.FirstOrDefault(e => e.ConnectionKey == c.Id);
            if (row is null)
            {
                row = new TrackerConnection
                {
                    Id = Ulid.NewUlid(),
                    OrgId = orgId,
                    UserId = userId,
                    ConnectionKey = c.Id,
                    Kind = c.Kind,
                    SiteUrl = c.SiteUrl,
                    MachineName = machineName,
                };
                db.Connections.Add(row);
            }

            row.Kind = c.Kind;
            row.SiteUrl = c.SiteUrl;
            row.AccountId = c.AccountId;
            row.DisplayName = c.DisplayName;
            row.Healthy = c.Healthy;
            row.HealthDetail = Truncate(c.HealthDetail, 1000);
            row.CheckedAt = c.CheckedAt;
            row.MachineName = machineName;
            row.LastSeenAt = now;
        }

        // A connection this machine no longer holds was removed there (`./orkka tracker
        // remove`). Only this machine's: the user's other machines speak for themselves. A
        // project still pointing at it keeps its configuration and reports the connection
        // missing — deleting a project's ticket source because a laptop was reinstalled
        // would lose the epic mapping someone built by hand.
        foreach (var gone in existing.Where(e => e.MachineName == machineName && !reported.Contains(e.ConnectionKey)))
        {
            gone.Healthy = false;
            gone.HealthDetail = $"No longer on {machineName} — add it again with `./orkka tracker add`.";
        }

        await db.SaveChangesAsync(ct);
    }

    // ── Polling ─────────────────────────────────────────────────────────────

    /// <summary>A ticket whose pull request opened this long ago stops being tracked (when
    /// "Merged" is not mapped, nothing else would end it).</summary>
    internal static readonly TimeSpan TrackAfterPrOpenedFor = TimeSpan.FromDays(30);

    public async Task<TrackerSyncResponse> SyncAsync(
        Ulid orgId, string userId, TrackerSyncRequest request, CancellationToken ct = default,
        bool supersedesOtherInstances = false)
    {
        if (request.ConnectionIds.Length == 0 || string.IsNullOrWhiteSpace(request.InstanceId))
            return new TrackerSyncResponse([]);

        var connections = await db.Connections.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.OrgId == orgId && c.UserId == userId && request.ConnectionIds.Contains(c.ConnectionKey))
            .ToListAsync(ct);
        if (connections.Count == 0)
            return new TrackerSyncResponse([]);
        var byId = connections.ToDictionary(c => c.Id);

        var now = DateTimeOffset.UtcNow;
        var candidates = await db.ProjectConfigs.IgnoreQueryFilters().AsNoTracking()
            .Include(c => c.Epics)
            .Where(c => c.OrgId == orgId && c.OwnerUserId == userId && c.Enabled
                && byId.Keys.Contains(c.TrackerConnectionId))
            .ToListAsync(ct);

        var assignments = new List<TrackerPollAssignment>();
        foreach (var config in candidates)
        {
            var due = config.LastPolledAt is not { } last
                || now - last >= TimeSpan.FromSeconds(config.PollIntervalSeconds);
            var epicRules = config.Epics.Where(e => e.Kind == TrackerRuleKind.Epic).ToList();
            // A source limited to epics with none configured has nothing to ask for.
            if (!due || (config.ScopeMode == TrackerScopeMode.Epics && epicRules.Count == 0))
                continue;

            // The lease is taken with a conditional update: two machines asking at once both
            // read "free", only one write lands. It outlives a couple of missed polls so a
            // machine that briefly drops does not hand the source to another and back.
            // The owner's active runner connection takes it over outright: the host keeps one
            // connection per workspace and user, so another holder is a process it replaced —
            // waiting out that lease paused intake for five minutes after every restart.
            var leaseUntil = now + TimeSpan.FromSeconds(Math.Max(config.PollIntervalSeconds * 3, 300));
            var taken = await db.ProjectConfigs.IgnoreQueryFilters()
                .Where(c => c.Id == config.Id
                    && (supersedesOtherInstances || c.LeaseHolder == null
                        || c.LeaseHolder == request.InstanceId || c.LeaseExpiresAt < now))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.LeaseHolder, request.InstanceId)
                    .SetProperty(c => c.LeaseExpiresAt, leaseUntil), ct);
            if (taken == 0)
                continue;

            var trackedLinks = await TrackedLinksAsync(config.Id, now, ct);
            var known = trackedLinks
                .Select(l => new TrackerKnownTicket(l.TicketKey, l.ContentHash, l.LastCommentAt))
                // Keys Orkka needs read but does not track: a Story's new sub-tasks (so they can
                // join the story) and the parent Story of a sub-task worked on its own (for its
                // context). No hash, so their text travels.
                .Concat((await ExtraKnownKeysAsync(config.Id, ct))
                    .Where(k => !trackedLinks.Any(l => string.Equals(l.TicketKey, k, StringComparison.OrdinalIgnoreCase)))
                    .Select(k => new TrackerKnownTicket(k, null, null)))
                .ToArray();

            var connection = byId[config.TrackerConnectionId];
            assignments.Add(new TrackerPollAssignment(
                config.Id.ToString(), connection.ConnectionKey, connection.Kind, config.Scope,
                [.. epicRules.OrderBy(e => e.Position).Select(e => e.EpicKey)], config.ExtraClause, config.IncludeDescendants,
                known,
                config.ScopeMode == TrackerScopeMode.Epics ? TrackerScopeModes.Epics : TrackerScopeModes.All,
                Features.ProjectConfig.ProjectTrackerMappers.ParseIntakeStatuses(config.IntakeStatusesJson)));
        }

        var (transitions, prChecks) = await PlanWriteBackAsync(orgId, userId, connections, ct);
        return new TrackerSyncResponse([.. assignments], transitions, prChecks);
    }

    /// <summary>
    /// The tickets read on every poll whatever their status: links whose task is not archived,
    /// that have not been merged, and that are less than <see cref="TrackAfterPrOpenedFor"/> past
    /// their pull request opening — regardless of who they are assigned to now. Links that just
    /// dropped out get <see cref="TicketLink.TrackingEndedAt"/>.
    /// </summary>
    internal async Task<List<TicketLink>> TrackedLinksAsync(Ulid configId, DateTimeOffset now, CancellationToken ct)
    {
        var links = await db.TicketLinks.IgnoreQueryFilters()
            .Where(l => l.ConfigId == configId && l.TrackingEndedAt == null)
            .ToListAsync(ct);
        if (links.Count == 0)
            return links;

        var states = (await tasksApi.GetTrackerTaskStatesAsync([.. links.Select(l => l.TaskId)], ct))
            .ToDictionary(s => s.TaskId);
        var groups = await GroupStatesAsync(links, ct);
        var tracked = new List<TicketLink>(links.Count);
        var ended = false;
        foreach (var link in links)
        {
            var archived = link.GroupId is { } gid && groups.TryGetValue(gid, out var group)
                ? StoryLinkGone(link, group)
                : !states.TryGetValue(link.TaskId, out var state) || state.IsArchived;
            var finished = link.SyncedPhase == TrackerPhase.Merged
                || (link.SyncedPhase == TrackerPhase.PrOpened && link.SyncedAt is { } at && now - at > TrackAfterPrOpenedFor);
            if (archived || finished)
            {
                link.TrackingEndedAt = now;
                ended = true;
                continue;
            }
            tracked.Add(link);
        }
        if (ended)
            await db.SaveChangesAsync(ct);
        return tracked;
    }

    public async Task<TrackerReportAck> ReportAsync(
        Ulid orgId, string userId, TrackerTicketReport report, CancellationToken ct = default)
    {
        if (!Ulid.TryParse(report.ConfigId, out var configId))
            return new TrackerReportAck(false, "Unknown ticket source.");

        var config = await db.ProjectConfigs.IgnoreQueryFilters()
            .Include(c => c.Epics)
            .FirstOrDefaultAsync(c => c.Id == configId && c.OrgId == orgId && c.OwnerUserId == userId, ct);
        if (config is null)
            return new TrackerReportAck(false, "Unknown ticket source.");
        if (config.LeaseHolder != report.InstanceId)
            return new TrackerReportAck(false, "Another machine is polling this ticket source now.");

        config.LastPolledAt = report.PolledAt;
        if (!report.Ok)
        {
            // A failed poll says nothing about which tickets still exist: record the reason for
            // the settings page and change nothing else.
            config.LastPollError = Truncate(report.Error ?? "The tracker could not be reached.", 2000);
            await db.SaveChangesAsync(ct);
            return new TrackerReportAck(true);
        }

        config.LastPollError = null;

        var connection = await db.Connections.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == config.TrackerConnectionId, ct);
        if (connection is null)
        {
            await db.SaveChangesAsync(ct);
            return new TrackerReportAck(false, "The ticket source's connection is gone.");
        }

        var links = await db.TicketLinks.IgnoreQueryFilters()
            .Where(l => l.ConfigId == config.Id)
            .ToDictionaryAsync(l => l.TicketKey, StringComparer.OrdinalIgnoreCase, ct);
        var tracked = links.Values.Where(l => l.TrackingEndedAt == null).ToList();
        var started = (await tasksApi.GetTrackerTaskStatesAsync([.. tracked.Select(l => l.TaskId)], ct))
            .ToDictionary(s => s.TaskId, s => s.Started);
        var tracker = TrackerName(connection.Kind);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var router = new ImportContext(config, connection, null);
        var created = 0;

        // Stories first: a Story and its sub-tasks arrive in one report in any order, and a
        // sub-task must become a member of its Story rather than a task of its own.
        var stories = new StoryReport(report.Tickets, links);
        if (config.StoryMode != 0)
            created += await IngestStoriesAsync(router, stories, tracker, ct);

        foreach (var ticket in report.Tickets)
        {
            if (!seen.Add(ticket.Key))
                continue;

            try
            {
                if (links.TryGetValue(ticket.Key, out var link))
                {
                    if (stories.JustImported.Contains(ticket.Key))
                    {
                        // Imported as part of its Story above; nothing more to sync this poll.
                    }
                    else if (link.TrackingEndedAt is null && link.Role == TicketRole.Story)
                    {
                        await ApplyToStoryTicketAsync(link, ticket, tracker, ct);
                    }
                    else if (link.TrackingEndedAt is null)
                    {
                        await ApplyToTrackedTicketAsync(link, ticket, tracker, started.GetValueOrDefault(link.TaskId), ct);
                    }
                    else if (ticket.InIntake && ticket.AssignedToMe && !link.AssignedToOwner)
                    {
                        // Orkka retired it (reassigned, closed before work started, vanished) and
                        // it is back in the developer's To Do: track it again. A task a person
                        // archived (still "assigned to owner") stays archived.
                        link.TrackingEndedAt = null;
                        link.AssignedToOwner = true;
                        await tasksApi.ReviveTrackerTaskAsync(link.TaskId,
                            $"{link.TicketKey} is back in your queue in {tracker}.", ct);
                        await ApplyToTrackedTicketAsync(link, ticket, tracker, false, ct);
                    }
                }
                else if (ticket.InIntake && ticket.AssignedToMe)
                {
                    router.Repos ??= await projectsApi.ListReposAsync(config.ProjectId, ct);
                    if (await ImportAsync(router, ticket, ct) is { } newLink)
                    {
                        db.TicketLinks.Add(newLink);
                        links[ticket.Key] = newLink;
                        created++;
                    }
                }
                // Anything else is a Story's sub-task (handled above), or a Story read only so a
                // sub-task worked on its own can carry its context.
                FillParentContext(links.Values, ticket);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad ticket must not cost the rest of the poll.
                logger.LogWarning(ex, "Could not ingest ticket {Key} for source {ConfigId}", ticket.Key, config.Id);
            }
        }

        // The tracking read asked for every tracked key: one missing from a successful report no
        // longer exists for this account — deleted, moved to another project, or hidden from it.
        foreach (var link in tracked.Where(l => !seen.Contains(l.TicketKey)))
        {
            link.AssignedToOwner = false;
            link.TrackingEndedAt = DateTimeOffset.UtcNow;
            var gone = $"{link.TicketKey} no longer exists in {tracker} for your account (deleted, moved to another project, "
                + "or no longer visible to you).";
            if (link.Role == TicketRole.Story)
            {
                // The Story ticket vanished; its sub-tasks are tracked on their own links and
                // the work already on the shared branch carries on.
                await tasksApi.AddTaskMessageAsync(link.TaskId, "system",
                    $"{gone} Its sub-tasks keep going.", MessageType.SystemEvent, ct: ct);
                continue;
            }
            await RetireLinkAsync(link, gone, ct);
        }

        await db.SaveChangesAsync(ct);

        if (created > 0)
            logger.LogInformation("Imported {Count} ticket(s) for source {ConfigId}", created, config.Id);
        return new TrackerReportAck(true);
    }

    /// <summary>
    /// A ticket Orkka tracks, as it is now. Its status, parent, text and comments always sync —
    /// moving from To Do to In Progress changes nothing else. It is retired only when it is
    /// reassigned (archived if nothing started, else left running with a note) or closed in the
    /// tracker before any work started; reassigned back, it is revived.
    /// </summary>
    private async Task ApplyToTrackedTicketAsync(
        TicketLink link, TrackerTicket ticket, string tracker, bool started, CancellationToken ct)
    {
        link.LastSeenAt = DateTimeOffset.UtcNow;
        link.TicketUrl = ticket.Url;
        link.EpicKey = ticket.EpicKey ?? link.EpicKey;
        link.ParentKey = ticket.ParentKey ?? link.ParentKey;
        if ((ticket.Status ?? ticket.Body?.Status) is { } status)
            link.RemoteStatus = Truncate(status, 200);

        // A Story's sub-task is worked because the Story is the developer's, whoever the sub-task
        // itself is assigned to: only closing it before work started retires its member.
        var subTask = link.Role == TicketRole.SubTask;
        if (subTask)
        {
            if (link.AssignedToOwner && !started && ticket.StatusCategory == "done")
            {
                link.AssignedToOwner = false;
                if (await RetireLinkAsync(link, $"{link.TicketKey} was moved to “{link.RemoteStatus}” in {tracker}.", ct))
                    link.TrackingEndedAt = DateTimeOffset.UtcNow;
            }
        }
        else if (!link.AssignedToOwner && ticket.AssignedToMe)
        {
            link.AssignedToOwner = true;
            await tasksApi.ReviveTrackerTaskAsync(link.TaskId,
                $"{link.TicketKey} is assigned to you again in {tracker}.", ct);
        }
        else if (link.AssignedToOwner && !ticket.AssignedToMe)
        {
            link.AssignedToOwner = false;
            var outcome = await tasksApi.RetireTrackerTaskAsync(link.TaskId,
                $"{link.TicketKey} was reassigned in {tracker} — it is no longer assigned to you.", ct);
            if (outcome != TrackerTaskRetirement.Held)
                link.TrackingEndedAt = DateTimeOffset.UtcNow;
        }
        else if (link.AssignedToOwner && !started && ticket.StatusCategory == "done")
        {
            // Closed in the tracker before anything started. Once work has started a done status
            // is left alone — it may well be Orkka's own write-back.
            link.AssignedToOwner = false;
            var outcome = await tasksApi.RetireTrackerTaskAsync(link.TaskId,
                $"{link.TicketKey} was moved to “{link.RemoteStatus}” in {tracker}.", ct);
            if (outcome != TrackerTaskRetirement.Held)
                link.TrackingEndedAt = DateTimeOffset.UtcNow;
        }

        if (ticket.Body is { } body && ticket.ContentHash != link.ContentHash)
        {
            link.AttachmentsJson = JsonSerializer.Serialize(body.Attachments, Json);
            var title = TaskTitle(ticket.Key, body.Title);
            var description = TaskDescription(body);
            if (!await tasksApi.TryRewriteTrackerTaskAsync(link.TaskId, title, description, ct))
            {
                // Work has started on the old text — the agent gets the change as a note rather
                // than a description rewritten under it.
                await tasksApi.AddTaskMessageAsync(link.TaskId, "system",
                    Truncate($"{ticket.Key} was updated in {tracker}. The ticket now reads:\n\n### {body.Title}\n\n{description}",
                        MaxNoteChars)!,
                    MessageType.SystemEvent, ct: ct);
            }
            link.ContentHash = ticket.ContentHash;
        }

        await AddCommentsAsync(link, ticket, tracker, ct);
    }

    /// <summary>What one report's imports share; the project's repositories are read once, on the
    /// first import.</summary>
    private sealed class ImportContext(ProjectTrackerConfig config, TrackerConnection connection, IReadOnlyList<RepoSummary>? repos)
    {
        public ProjectTrackerConfig Config { get; } = config;
        public TrackerConnection Connection { get; } = connection;
        public IReadOnlyList<RepoSummary>? Repos { get; set; } = repos;
    }

    /// <param name="parent">A sub-task worked on its own after its Story's pull request closed:
    /// the Story, carried as the task's "Parent story context".</param>
    private async Task<TicketLink?> ImportAsync(
        ImportContext context, TrackerTicket ticket, CancellationToken ct,
        (string Key, string? Title, string? Description)? parent = null)
    {
        var config = context.Config;
        var connection = context.Connection;
        // A new ticket always arrives with its body (the runner only omits it for tickets the
        // host already tracks).
        if (ticket.Body is not { } body)
        {
            logger.LogWarning("Ticket {Key} for source {ConfigId} arrived without its text — not imported",
                ticket.Key, config.Id);
            return null;
        }

        var repos = context.Repos ?? [];
        var route = await RouteAsync(config, repos, ticket, body, ct);
        var epicRule = ticket.EpicKey is { } epicKey
            ? config.Epics.FirstOrDefault(e => e.Kind == TrackerRuleKind.Epic
                && string.Equals(e.EpicKey, epicKey, StringComparison.OrdinalIgnoreCase))
            : null;
        var epicLabel = epicRule is { EpicTitle: { Length: > 0 } epicTitle }
            ? $"{epicRule.EpicKey} {epicTitle}"
            : ticket.EpicKey;

        var result = await tasksApi.CreateTrackerTaskAsync(new TrackerTaskSpec(
            config.OrgId, config.ProjectId, route.RepoId, config.OwnerUserId,
            ExternalRef(connection, ticket.Key), ticket.Key, ticket.Url,
            TaskTitle(ticket.Key, body.Title), TaskDescription(body),
            MapType(body.Type), MapPriority(body.Priority),
            [.. body.Labels.Take(10)],
            epicLabel,
            route.Subpath,
            route.SpansRepoIds.Count >= 2 ? route.SpansRepoIds : null), ct);

        var link = new TicketLink
        {
            Id = Ulid.NewUlid(),
            OrgId = config.OrgId,
            ConfigId = config.Id,
            ProjectId = config.ProjectId,
            TaskId = result.TaskId,
            Kind = connection.Kind,
            SiteUrl = connection.SiteUrl,
            TicketKey = ticket.Key,
            TicketId = ticket.Id,
            TicketUrl = ticket.Url,
            EpicKey = ticket.EpicKey,
            ParentKey = parent?.Key ?? ticket.ParentKey,
            Role = TicketRole.Standalone,
            // A sub-task whose Story is not the developer's: the Story is read on the next poll
            // for its context ("" = wanted, not read yet).
            ParentTitle = parent is { } p ? Truncate(p.Title ?? p.Key, 490) : WantsParentContext(ticket) ? "" : null,
            ParentDescription = parent is { } pd ? Truncate(pd.Description, 19_000) : null,
            RepoId = route.SpansRepoIds.Count >= 2 ? null : route.RepoId,
            Subpath = route.Subpath,
            ContentHash = ticket.ContentHash,
            RemoteStatus = Truncate(ticket.Status ?? body.Status, 200),
            AttachmentsJson = JsonSerializer.Serialize(body.Attachments, Json),
            LastSeenAt = DateTimeOffset.UtcNow,
        };

        var tracker = TrackerName(connection.Kind);
        await AddCommentsAsync(link, ticket, tracker, ct);

        if (result.Created)
        {
            if (route.RepoId is null)
            {
                // Nobody could tell where it goes: the developer picks, and the answer continues it.
                await tasksApi.AskTrackerRepoAsync(result.TaskId, [.. repos.Select(r => r.Name)],
                    route.Reason ?? "", config.AutoSubmit, ct);
            }
            else if (config.AutoSubmit)
            {
                var submitted = await tasksApi.SubmitTaskAsync(config.OrgId, result.TaskId, locale: null, ct);
                if (!submitted.Ok)
                    await tasksApi.AddTaskMessageAsync(result.TaskId, "system",
                        $"Imported from {tracker} but not submitted: {submitted.Message}",
                        MessageType.SystemEvent, ct: ct);
            }
        }

        return link;
    }

    /// <summary>Where a new ticket is worked, and how that was decided.</summary>
    internal sealed record TicketRoute(Ulid? RepoId, string? Subpath, IReadOnlyList<Ulid> SpansRepoIds, string Source, string? Reason);

    /// <summary>
    /// Routing, first answer wins: the source's rules (epics, then components / labels / area
    /// paths, each in order); the project's only repository; the source's default repository; the
    /// repo router on the owner's runner (one repository it is sure of, or several = a cross-repo
    /// group). Otherwise no repository, and the reason the task will give when it asks.
    /// </summary>
    internal async Task<TicketRoute> RouteAsync(
        ProjectTrackerConfig config, IReadOnlyList<RepoSummary> repos, TrackerTicket ticket, TrackerTicketBody body, CancellationToken ct)
    {
        var repoIds = repos.Select(r => r.Id).ToHashSet();
        if (MatchRule(config.Epics, ticket, body) is { } rule && rule.RepoId is { } ruleRepo && repoIds.Contains(ruleRepo))
            return new TicketRoute(ruleRepo, rule.Subpath, [], $"rule:{rule.Kind}", null);

        if (repos.Count == 1)
            return new TicketRoute(repos[0].Id, null, [], "single-repo", null);

        if (config.DefaultRepoId is { } defaultRepo && repoIds.Contains(defaultRepo))
            return new TicketRoute(defaultRepo, null, [], "default", null);

        if (repos.Count == 0)
            return new TicketRoute(null, null, [], "none", "This project has no repository yet — add one first.");

        var decision = await repoRouter.RouteAsync(config.OrgId, config.OwnerUserId,
            new RepoRouteTicket(ticket.Key, body.Title, TaskDescription(body), body.Components ?? [], body.Labels, body.AreaPath),
            repos, ct);
        if (decision is null)
            return new TicketRoute(null, null, [], "router-offline",
                "Your runner could not be asked to pick one (offline, or no AI CLI signed in).");

        var picked = decision.RepoIds.Where(repoIds.Contains).Distinct().ToList();
        if (picked.Count >= 2)
            return new TicketRoute(picked[0], null, picked, "router-multi", decision.Reason);
        if (picked.Count == 1 && decision.HighConfidence)
            return new TicketRoute(picked[0], null, [], "router", decision.Reason);
        return new TicketRoute(null, null, [], "router-unsure",
            decision.Reason is { } why ? $"The best guess was not certain: {why}" : null);
    }

    /// <summary>The first rule the ticket matches: epic rules (its epic, or its direct parent),
    /// then component, label and area-path rules (an area path also matches the paths under it),
    /// each group in <see cref="TrackerEpicMapping.Position"/> order. Rules without a repository
    /// only scope intake and never match here.</summary>
    internal static TrackerEpicMapping? MatchRule(IEnumerable<TrackerEpicMapping> rules, TrackerTicket ticket, TrackerTicketBody body)
    {
        var areaPath = body.AreaPath?.Replace('/', '\\').Trim('\\');
        foreach (var rule in rules.Where(r => r.RepoId is not null)
                     .OrderBy(r => r.Kind == TrackerRuleKind.Epic ? 0 : 1).ThenBy(r => r.Position))
        {
            var value = rule.EpicKey.Trim();
            var hit = rule.Kind switch
            {
                TrackerRuleKind.Epic => Same(ticket.EpicKey, value) || Same(ticket.ParentKey, value),
                TrackerRuleKind.Component => (body.Components ?? []).Any(c => Same(c, value)),
                TrackerRuleKind.Label => body.Labels.Any(l => Same(l, value)),
                TrackerRuleKind.AreaPath => areaPath is not null && AreaPathMatches(areaPath, value),
                _ => false,
            };
            if (hit)
                return rule;
        }
        return null;

        static bool Same(string? a, string b) => a is not null && string.Equals(a.Trim(), b, StringComparison.OrdinalIgnoreCase);

        static bool AreaPathMatches(string areaPath, string value)
        {
            var rulePath = value.Replace('/', '\\').Trim('\\');
            return rulePath.Length > 0
                && (string.Equals(areaPath, rulePath, StringComparison.OrdinalIgnoreCase)
                    || areaPath.StartsWith(rulePath + "\\", StringComparison.OrdinalIgnoreCase));
        }
    }

    private async Task AddCommentsAsync(TicketLink link, TrackerTicket ticket, string tracker, CancellationToken ct)
    {
        foreach (var comment in ticket.NewComments.OrderBy(c => c.CreatedAt))
        {
            if (link.LastCommentAt is { } last && comment.CreatedAt <= last)
                continue;

            await tasksApi.AddTaskMessageAsync(link.TaskId, "tracker",
                Truncate($"**{comment.Author}** commented on {ticket.Key} in {tracker} ({comment.CreatedAt:yyyy-MM-dd HH:mm} UTC):\n\n{comment.Body}",
                    MaxNoteChars)!,
                MessageType.ExternalComment, ct: ct);
            link.LastCommentAt = comment.CreatedAt;
        }
    }

    public async Task<TaskTicketContext?> GetTicketContextAsync(Ulid taskId, CancellationToken ct = default)
    {
        // A Story's link points at its first member too; that member's own ticket is its sub-task.
        var link = await db.TicketLinks.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(l => l.TaskId == taskId && l.Role != TicketRole.Story, ct);
        if (link is null)
            return null;

        var config = await db.ProjectConfigs.IgnoreQueryFilters().AsNoTracking()
            .Include(c => c.Epics)
            .FirstOrDefaultAsync(c => c.Id == link.ConfigId, ct);
        var connection = config is null
            ? null
            : await db.Connections.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == config.TrackerConnectionId, ct);
        var epicTitle = config?.Epics
            .FirstOrDefault(e => string.Equals(e.EpicKey, link.EpicKey, StringComparison.OrdinalIgnoreCase))?.EpicTitle;

        IReadOnlyList<TrackerAttachmentMeta> attachments;
        try
        {
            attachments = JsonSerializer.Deserialize<TrackerAttachmentMeta[]>(link.AttachmentsJson, Json) ?? [];
        }
        catch (JsonException)
        {
            attachments = [];
        }

        return new TaskTicketContext(
            link.Kind, link.TicketKey, link.TicketUrl, link.EpicKey, epicTitle, link.Subpath,
            connection?.ConnectionKey ?? "", attachments, config?.BranchTemplate,
            !string.IsNullOrEmpty(link.ParentTitle) ? link.ParentKey : null,
            string.IsNullOrEmpty(link.ParentTitle) ? null : link.ParentTitle,
            string.IsNullOrEmpty(link.ParentTitle) ? null : link.ParentDescription);
    }

    // ── Mapping a ticket onto a task ────────────────────────────────────────

    internal static string TaskTitle(string key, string title) => $"[{key}] {title.Trim()}";

    internal static string TaskDescription(TrackerTicketBody body)
    {
        var sb = new StringBuilder(body.Description.Trim());
        if (!string.IsNullOrWhiteSpace(body.AcceptanceCriteria))
            sb.Append("\n\n## Acceptance criteria\n\n").Append(body.AcceptanceCriteria.Trim());
        if (!string.IsNullOrWhiteSpace(body.ReproSteps))
            sb.Append("\n\n## Steps to reproduce\n\n").Append(body.ReproSteps.Trim());
        if (body.Attachments.Length > 0)
        {
            sb.Append("\n\n## Attachments\n\n");
            foreach (var a in body.Attachments)
                sb.Append("- ").Append(a.FileName)
                  .Append(a.Cached ? "" : " (not downloaded — open the ticket to see it)").Append('\n');
        }
        return sb.ToString().Trim();
    }

    internal static TaskType MapType(string? type) => type?.ToLowerInvariant() switch
    {
        null => TaskType.Feature,
        var t when t.Contains("bug") || t.Contains("defect") || t.Contains("incident") => TaskType.BugFix,
        var t when t.Contains("improvement") || t.Contains("enhancement") => TaskType.Improvement,
        var t when t.Contains("refactor") || t.Contains("tech debt") || t.Contains("technical debt") => TaskType.Refactor,
        _ => TaskType.Feature,
    };

    /// <summary>Jira's names (Highest … Lowest, Blocker/Critical/Major/Minor/Trivial) and Azure
    /// DevOps' numbers (1–4) onto Orkka's four levels.</summary>
    internal static Priority MapPriority(string? priority) => priority?.Trim().ToLowerInvariant() switch
    {
        "highest" or "blocker" or "critical" or "1" => Priority.Critical,
        "high" or "major" or "2" => Priority.High,
        "low" or "lowest" or "minor" or "trivial" or "4" => Priority.Low,
        _ => Priority.Medium,
    };

    /// <summary><c>jira:acme.atlassian.net:SHOP-12</c> / <c>ado:dev.azure.com/acme:4567</c> —
    /// stable per ticket, so a re-reported ticket never becomes a second task.</summary>
    internal static string ExternalRef(TrackerConnection connection, string key)
    {
        var site = connection.SiteUrl;
        var schemeEnd = site.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
            site = site[(schemeEnd + 3)..];
        return $"{connection.Kind}:{site.TrimEnd('/').ToLowerInvariant()}:{key}";
    }

    private static string TrackerName(string kind) => kind == TrackerKinds.AzureDevOps ? "Azure DevOps" : "Jira";

    private static string? Truncate(string? value, int max)
        => value is null || value.Length <= max ? value : value[..max] + "…";
}
