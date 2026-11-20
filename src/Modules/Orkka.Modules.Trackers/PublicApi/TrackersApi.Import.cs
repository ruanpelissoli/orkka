namespace Orkka.Modules.Trackers.PublicApi;

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Trackers.Contracts;
using Orkka.Modules.Trackers.Domain;

/// <summary>
/// Importing a ticket by its key — for work the tracker does not hand Orkka on its own (assigned
/// to someone else or nobody, not in an intake status, sub-tasks never linked the way intake
/// expects). The project's ticket source says which tracker and which of the developer's
/// connections; the preview reads the ticket and its sub-tasks by key on the owner's runner
/// (<see cref="TrackerPollAssignment.KeysOnly"/>); the import is queued as a
/// <see cref="TrackerImport"/> and made by the next poll — the same reads as intake, so the
/// description, attachments (cached on the machine) and comments all come with it. Imported
/// links are pinned: a reassignment or a teammate's sub-task never takes them off the board.
/// </summary>
internal sealed partial class TrackersApi
{
    /// <summary>Reports that may come back without the ticket before the import gives up — the
    /// first poll after the request always asks for it by key.</summary>
    internal const int MaxImportAttempts = 3;

    // ── Keys ────────────────────────────────────────────────────────────

    public string? NormalizeTicketKey(string kind, string input) => NormalizeKey(kind, input);

    internal static string? NormalizeKey(string kind, string? input)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0 || text.Length > 500)
            return null;
        switch (kind)
        {
            case TrackerKinds.Jira:
            {
                // https://acme.atlassian.net/browse/SHOP-10, …/issues/?selectedIssue=SHOP-10, or the key.
                var match = JiraKey().Match(text);
                return match.Success ? match.Value.ToUpperInvariant() : null;
            }
            case TrackerKinds.AzureDevOps:
            {
                // …/_workitems/edit/4567, #4567 or 4567.
                var edit = AdoEditUrl().Match(text);
                var id = edit.Success ? edit.Groups[1].Value : text.TrimStart('#');
                return int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
                    ? n.ToString(CultureInfo.InvariantCulture)
                    : null;
            }
            case TrackerKinds.GitHub:
            {
                // https://github.com/owner/repo/issues/12 or owner/repo#12.
                var url = GitHubIssueUrl().Match(text);
                if (url.Success)
                    return $"{url.Groups[1].Value}/{url.Groups[2].Value}#{url.Groups[3].Value}";
                var key = GitHubKey().Match(text);
                return key.Success ? $"{key.Groups[1].Value}/{key.Groups[2].Value}#{key.Groups[3].Value}" : null;
            }
            default:
                return null;
        }
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Za-z][A-Za-z0-9_]+-[0-9]+(?![0-9])")]
    private static partial Regex JiraKey();

    [GeneratedRegex(@"/_workitems/edit/([0-9]+)", RegexOptions.IgnoreCase)]
    private static partial Regex AdoEditUrl();

    [GeneratedRegex(@"^https?://[^/]+/([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+)/issues/([0-9]+)(?:[/?#].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubIssueUrl();

    [GeneratedRegex(@"^([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+)#([0-9]+)$")]
    private static partial Regex GitHubKey();

    // ── Preview ─────────────────────────────────────────────────────────

    public async Task<string?> SourceKindAsync(Ulid projectId, CancellationToken ct = default)
    {
        var connectionId = await db.ProjectConfigs.AsNoTracking()
            .Where(c => c.ProjectId == projectId)
            .Select(c => (Ulid?)c.TrackerConnectionId)
            .FirstOrDefaultAsync(ct);
        return connectionId is null
            ? null
            : await db.Connections.AsNoTracking().Where(c => c.Id == connectionId).Select(c => c.Kind).FirstOrDefaultAsync(ct);
    }

    public async Task<TrackerPreviewAssignment?> BuildImportAssignmentAsync(
        Ulid projectId, IReadOnlyList<string> keys, CancellationToken ct = default)
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

        var epicRules = config.Epics.Where(e => e.Kind == TrackerRuleKind.Epic);
        var assignment = new TrackerPollAssignment(
            config.Id.ToString(), connection.ConnectionKey, connection.Kind, config.Scope,
            [.. epicRules.OrderBy(e => e.Position).Select(e => e.EpicKey)], null, false,
            [.. keys.Distinct(StringComparer.OrdinalIgnoreCase).Select(k => new TrackerKnownTicket(k, null, null))],
            BoardId: config.BoardId, BoardName: config.BoardName, StatusField: config.StatusField,
            KeysOnly: true);
        return new TrackerPreviewAssignment(config.OwnerUserId, connection.Id, assignment);
    }

    public async Task<TrackerImportPreview?> PreviewImportAsync(
        Ulid projectId, string key, IReadOnlyList<TrackerTicket> tickets, CancellationToken ct = default)
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

        var byKey = tickets.GroupBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        if (!byKey.TryGetValue(key, out var ticket) || ticket.Body is not { } body)
            return null;

        var links = await db.TicketLinks.AsNoTracking()
            .Where(l => l.ConfigId == config.Id && l.TrackingEndedAt == null)
            .Select(l => new { l.TicketKey, l.TaskId })
            .ToListAsync(ct);
        var onBoard = links.GroupBy(l => l.TicketKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().TaskId, StringComparer.OrdinalIgnoreCase);

        var subTasks = (ticket.ChildKeys ?? []).Select(k =>
        {
            byKey.TryGetValue(k, out var child);
            Ulid? existing = onBoard.TryGetValue(k, out var taskId) ? taskId : null;
            return new TrackerImportSubTask(
                k,
                child?.Body?.Title.Trim() ?? k,
                child?.Status ?? child?.Body?.Status,
                child?.StatusCategory,
                child?.AssignedToMe ?? false,
                Selected: child is not null && existing is null && child.StatusCategory != "done",
                Found: child is not null,
                existing);
        }).ToArray();

        var repos = await projectsApi.ListReposAsync(config.ProjectId, ct);
        var route = await RouteAsync(config, repos, ticket, body, ct, askRouter: false);
        var repoName = route.RepoId is { } repoId ? repos.FirstOrDefault(r => r.Id == repoId)?.Name : null;

        return new TrackerImportPreview(
            connection.Kind,
            ticket.Key,
            body.Title.Trim(),
            body.Type,
            ticket.Status ?? body.Status,
            ticket.Url,
            LevelName(ticket.Level),
            ticket.AssignedToMe,
            Truncate(body.Description, 4000) ?? "",
            body.Attachments,
            subTasks,
            repoName,
            route.Source,
            ticket.ParentKey,
            onBoard.TryGetValue(ticket.Key, out var existingTask) ? existingTask : null,
            subTasks.Any(s => s.Selected) ? "story" : "task");
    }

    // ── Request ─────────────────────────────────────────────────────────

    public async Task<TrackerImportResult> RequestImportAsync(Ulid projectId, string userId, string key,
        IReadOnlyList<string> subTaskKeys, CancellationToken ct = default)
    {
        var config = await db.ProjectConfigs.FirstOrDefaultAsync(c => c.ProjectId == projectId, ct);
        if (config is null)
            return new TrackerImportResult(null, "This project has no ticket source yet — set one up first.", 404);
        if (config.OwnerUserId != userId)
            return new TrackerImportResult(null,
                "Only the ticket source's owner can import tickets — their runner holds the tracker token.", 403);
        if ((await projectsApi.GetSetupPendingProjectIdsAsync([projectId], ct))?.Contains(projectId) is true)
            return new TrackerImportResult(null, SetupPendingError, 409);
        var kind = await db.Connections.AsNoTracking()
            .Where(c => c.Id == config.TrackerConnectionId).Select(c => c.Kind).FirstOrDefaultAsync(ct);
        if (kind is null)
            return new TrackerImportResult(null, "The ticket source's connection is gone.", 409);
        if (NormalizeKey(kind, key) is not { } ticketKey)
            return new TrackerImportResult(null, $"'{key}' is not a {TrackerName(kind)} ticket key.", 400);
        var subs = subTaskKeys.Select(k => NormalizeKey(kind, k)).OfType<string>()
            .Where(k => !string.Equals(k, ticketKey, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToArray();

        var now = DateTimeOffset.UtcNow;
        var link = await db.TicketLinks.FirstOrDefaultAsync(l => l.ConfigId == config.Id && l.TicketKey == ticketKey, ct);
        if (link is { TrackingEndedAt: null })
        {
            // Already on the board: from now on it stays whoever has it.
            link.Pinned = true;
            var done = new TrackerImport
            {
                Id = Ulid.NewUlid(), OrgId = config.OrgId, ConfigId = config.Id, ProjectId = projectId,
                TicketKey = ticketKey, SubTaskKeysJson = JsonSerializer.Serialize(subs, Json),
                RequestedByUserId = userId, RequestedAt = now, CompletedAt = now, TaskId = link.TaskId,
            };
            db.Imports.Add(done);
            await db.SaveChangesAsync(ct);
            return new TrackerImportResult(ToStatus(done));
        }

        var pending = await db.Imports.FirstOrDefaultAsync(
            i => i.ConfigId == config.Id && i.TicketKey == ticketKey && i.CompletedAt == null, ct);
        if (pending is null)
        {
            pending = new TrackerImport
            {
                Id = Ulid.NewUlid(), OrgId = config.OrgId, ConfigId = config.Id, ProjectId = projectId,
                TicketKey = ticketKey, RequestedByUserId = userId, RequestedAt = now,
            };
            db.Imports.Add(pending);
        }
        pending.SubTaskKeysJson = JsonSerializer.Serialize(subs, Json);
        pending.Attempts = 0;
        // Due now: the runner picks it up on its next cycle (every 30 s) instead of waiting out
        // the source's poll interval.
        config.LastPolledAt = null;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Import of {Key} ({Count} sub-task(s)) queued for source {ConfigId}", ticketKey, subs.Length, config.Id);
        return new TrackerImportResult(ToStatus(pending), StatusCode: 202);
    }

    public async Task<TrackerImportStatus?> GetImportAsync(Ulid projectId, Ulid importId, CancellationToken ct = default)
    {
        var import = await db.Imports.AsNoTracking().FirstOrDefaultAsync(i => i.Id == importId && i.ProjectId == projectId, ct);
        return import is null ? null : ToStatus(import);
    }

    private static TrackerImportStatus ToStatus(TrackerImport i) => new(
        i.Id, i.TicketKey, ParseKeys(i.SubTaskKeysJson),
        i.CompletedAt is null ? "pending" : i.Error is null ? "done" : "failed",
        i.TaskId, i.Error, i.RequestedAt, i.CompletedAt);

    // ── The poll ────────────────────────────────────────────────────────

    /// <summary>
    /// The report's part for imports by key, before intake: each pending import whose ticket the
    /// report carries becomes a pinned task — or, with picked sub-tasks, a pinned Story with
    /// exactly those members, the unticked ones excluded. A ticket already linked is pinned (and
    /// revived when Orkka had let it go). Returns how many tickets were imported.
    /// </summary>
    private async Task<int> ProcessImportsAsync(ImportContext router, StoryReport stories, string tracker, CancellationToken ct)
    {
        var config = router.Config;
        var pending = await db.Imports.IgnoreQueryFilters()
            .Where(i => i.ConfigId == config.Id && i.CompletedAt == null)
            .OrderBy(i => i.RequestedAt)
            .ToListAsync(ct);
        var created = 0;
        foreach (var import in pending)
        {
            try
            {
                created += await ProcessImportAsync(router, stories, tracker, import, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not import {Key} for source {ConfigId}", import.TicketKey, config.Id);
                import.Attempts++;
                if (import.Attempts >= MaxImportAttempts)
                    Fail(import, $"{import.TicketKey} could not be imported: {ex.Message}");
            }
        }
        return created;
    }

    private async Task<int> ProcessImportAsync(
        ImportContext router, StoryReport stories, string tracker, TrackerImport import, CancellationToken ct)
    {
        var key = import.TicketKey;
        var links = stories.Links;

        if (links.TryGetValue(key, out var existing))
        {
            existing.Pinned = true;
            if (existing.TrackingEndedAt is not null)
            {
                // Orkka (or a person) let it go; asked for again by name, it comes back.
                existing.TrackingEndedAt = null;
                existing.AssignedToOwner = true;
                await tasksApi.ReviveTrackerTaskAsync(existing.TaskId, $"{key} was imported again from {tracker}.", ct);
            }
            Complete(import, existing.TaskId);
            return 0;
        }

        if (!stories.ByKey.TryGetValue(key, out var ticket) || ticket.Body is null)
        {
            // Asked for by key on this poll: absent means it does not exist for this account.
            import.Attempts++;
            if (import.Attempts >= MaxImportAttempts)
                Fail(import, $"{key} was not found in {tracker} with your account (deleted, moved, or not visible to you).");
            return 0;
        }
        if (ticket.Level == TicketLevel.Above)
        {
            Fail(import, $"{key} is above a story (an epic or a feature) — import its stories instead.");
            return 0;
        }

        router.Repos ??= await projectsApi.ListReposAsync(router.Config.ProjectId, ct);
        var picked = ParseKeys(import.SubTaskKeysJson).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var members = (ticket.ChildKeys ?? [])
            .Where(picked.Contains)
            .Where(k => !links.TryGetValue(k, out var l) || l.TrackingEndedAt is not null)
            .Select(k => stories.ByKey.TryGetValue(k, out var c) ? c : null)
            .OfType<TrackerTicket>()
            .Where(c => c.Body is not null)
            .ToList();

        if (members.Count > 0)
        {
            var excluded = (ticket.ChildKeys ?? []).Where(k => !members.Any(m => string.Equals(m.Key, k, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            if (!await ImportStoryAsync(router, ticket, members, stories, tracker, ct, excluded)
                || !links.TryGetValue(key, out var storyLink))
            {
                Fail(import, $"{key} could not be imported as a story — its sub-tasks are already being worked on their own.");
                return 0;
            }
            storyLink.Pinned = true;
            foreach (var member in members)
            {
                if (links.TryGetValue(member.Key, out var memberLink))
                    memberLink.Pinned = true;
            }
            Complete(import, storyLink.TaskId);
            return 1;
        }

        if (await ImportAsync(router, ticket, ct) is not { } link)
        {
            Fail(import, $"{key} could not be imported.");
            return 0;
        }
        link.Pinned = true;
        db.TicketLinks.Add(link);
        links[key] = link;
        stories.JustImported.Add(key);
        await tasksApi.AddTaskMessageAsync(link.TaskId, "system",
            $"{key} was imported from {tracker} by its key. Orkka keeps it in step with the ticket (text, comments, status) "
            + "whoever it is assigned to.",
            MessageType.SystemEvent, ct: ct);
        Complete(import, link.TaskId);
        return 1;
    }

    private static void Complete(TrackerImport import, Ulid taskId)
    {
        import.TaskId = taskId;
        import.CompletedAt = DateTimeOffset.UtcNow;
        import.Error = null;
    }

    private static void Fail(TrackerImport import, string error)
    {
        import.Error = Truncate(error, 2000);
        import.CompletedAt = DateTimeOffset.UtcNow;
    }
}
