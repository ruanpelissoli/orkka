namespace Orkka.LocalRunner.Trackers;

using System.Globalization;
using System.Net;
using System.Text.Json;
using Orkka.LocalRunner.Contracts;

/// <summary>WIQL built from user input. Pure, so the escaping is testable on its own.</summary>
public static class AdoQueries
{
    public const string HierarchyForward = "System.LinkTypes.Hierarchy-Forward";

    /// <summary>A WIQL string literal: single quotes doubled.</summary>
    public static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>A work item id typed into a search box: <c>12</c> or <c>#12</c>.</summary>
    public static int? AsWorkItemId(string? text)
    {
        var t = text?.Trim().TrimStart('#');
        return int.TryParse(t, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : null;
    }

    /// <summary>Recent epics in the project in the URL (<c>@project</c>); with a search, the
    /// epic with that id, or epics whose title contains the text.</summary>
    public static string EpicsWiql(string? search)
    {
        var wiql = "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND [System.WorkItemType] = 'Epic'";
        var text = search?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            wiql += AsWorkItemId(text) is { } id
                ? $" AND [System.Id] = {id}"
                : $" AND [System.Title] CONTAINS {Literal(text)}";
        }
        return wiql + " ORDER BY [System.ChangedDate] DESC";
    }

    /// <summary>
    /// The work items under the epics: direct children (<c>MustContain</c>), or the whole tree
    /// below them (<c>Recursive</c>). No target filter here on purpose — recursive link
    /// queries restrict which conditions they accept, and a tree query keeps a filtered-out
    /// parent to hold its matching children, so the filters run in a second, flat query
    /// (<see cref="PreviewWiql"/>) where they mean exactly what they say.
    /// </summary>
    public static string DescendantsWiql(IEnumerable<int> epicIds, bool recursive)
        => "SELECT [System.Id] FROM WorkItemLinks WHERE "
            + $"[Source].[System.Id] IN ({string.Join(", ", epicIds)}) "
            + $"AND [System.Links.LinkType] = '{HierarchyForward}' "
            + (recursive ? "MODE (Recursive)" : "MODE (MustContain)");

    /// <summary>
    /// The tickets a configuration picks up: among the epics' descendants, assigned to the
    /// token's owner, not in a done/removed state (WIQL has no state-category field, so the
    /// names come from the project's work item types), plus the project's own condition.
    /// </summary>
    public static string PreviewWiql(IEnumerable<int> candidateIds, IEnumerable<string> doneStates, string? extraClause)
    {
        var wiql = $"SELECT [System.Id] FROM WorkItems WHERE [System.Id] IN ({string.Join(", ", candidateIds)}) "
            + "AND [System.AssignedTo] = @Me";
        var done = doneStates.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
        if (done.Count > 0)
            wiql += $" AND [System.State] NOT IN ({string.Join(", ", done.Select(Literal))})";
        if (!string.IsNullOrWhiteSpace(extraClause))
            wiql += $" AND ({extraClause.Trim()})";
        return wiql;
    }

    /// <summary>
    /// The intake query: work items in the project (<c>@project</c>) assigned to the token's
    /// owner in one of <paramref name="intakeStates"/>, not a container type (Epic, Feature),
    /// among <paramref name="candidateIds"/> when the source is limited to epics, plus the
    /// project's own condition.
    /// </summary>
    public static string IntakeWiql(IEnumerable<string> intakeStates, IEnumerable<string> containerTypes,
        IEnumerable<int>? candidateIds, string? extraClause, string? boardCondition = null)
    {
        var states = intakeStates.Select(s => s.Trim()).Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (states.Count == 0)
            throw new TrackerException("Pick at least one intake state.");
        var wiql = "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND [System.AssignedTo] = @Me"
            + $" AND [System.State] IN ({string.Join(", ", states.Select(Literal))})";
        var containers = containerTypes.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
        if (containers.Count > 0)
            wiql += $" AND [System.WorkItemType] NOT IN ({string.Join(", ", containers.Select(Literal))})";
        if (candidateIds is not null)
            wiql += $" AND [System.Id] IN ({string.Join(", ", candidateIds)})";
        if (!string.IsNullOrWhiteSpace(boardCondition))
            wiql += $" AND {boardCondition.Trim()}";
        if (!string.IsNullOrWhiteSpace(extraClause))
            wiql += $" AND ({extraClause.Trim()})";
        return wiql;
    }

    /// <summary>
    /// A team's board as a WIQL condition: the team's field values (normally area paths) from
    /// <c>teamsettings/teamfieldvalues</c> — <c>[field] UNDER 'x'</c> for a value that includes its
    /// children, <c>[field] = 'y'</c> otherwise, OR-ed and parenthesized. The team's default value
    /// stands in when it lists none. Null when it has nothing at all.
    /// </summary>
    public static string? TeamFieldCondition(string? fieldReferenceName,
        IEnumerable<(string Value, bool IncludeChildren)> values, string? defaultValue = null)
    {
        var field = string.IsNullOrWhiteSpace(fieldReferenceName) ? "System.AreaPath" : fieldReferenceName.Trim();
        // A reference name is letters, digits, dots and underscores; anything else would be
        // WIQL text taken from the response, not a field.
        if (!field.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_'))
            throw new TrackerException($"Azure DevOps named an unexpected team field '{field}'.");
        var terms = values
            .Where(v => !string.IsNullOrWhiteSpace(v.Value))
            .Select(v => (Value: v.Value.Trim(), v.IncludeChildren))
            .DistinctBy(v => (v.Value.ToUpperInvariant(), v.IncludeChildren))
            .Select(v => v.IncludeChildren ? $"[{field}] UNDER {Literal(v.Value)}" : $"[{field}] = {Literal(v.Value)}")
            .ToList();
        if (terms.Count == 0 && !string.IsNullOrWhiteSpace(defaultValue))
            terms.Add($"[{field}] UNDER {Literal(defaultValue.Trim())}");
        return terms.Count == 0 ? null : $"({string.Join(" OR ", terms)})";
    }

    /// <summary><see cref="TeamFieldCondition(string?, IEnumerable{ValueTuple{string, bool}}, string?)"/>
    /// from a <c>teamfieldvalues</c> response.</summary>
    public static string? TeamFieldCondition(JsonElement teamFieldValues)
        => TeamFieldCondition(
            teamFieldValues.Prop("field").Str("referenceName"),
            teamFieldValues.Prop("values").Items()
                .Select(v => (v.Str("value") ?? "", v.Prop("includeChildren").ValueKind == JsonValueKind.True)),
            teamFieldValues.Str("defaultValue"));

    /// <summary>Children in backlog order: StackRank / BacklogPriority ascending (unranked
    /// last), then id.</summary>
    public static IEnumerable<int> OrderChildren(IEnumerable<(int Id, double? Rank)> children)
        => children.OrderBy(ch => ch.Rank ?? double.MaxValue).ThenBy(ch => ch.Id).Select(ch => ch.Id);

    /// <summary>Azure DevOps state categories onto the three the status map uses; null for
    /// Removed, which is not a place a ticket is moved to.</summary>
    public static string? StateCategory(string? category) => category switch
    {
        "Proposed" => "todo",
        "InProgress" or "Resolved" => "inprogress",
        "Completed" => "done",
        "Removed" => null,
        _ => "todo",
    };
}

/// <summary>Azure DevOps Services REST 7.1 with the developer's personal access token.</summary>
public sealed class AzureDevOpsClient(HttpClient http) : ITrackerClient, ITrackerTicketSource, ITrackerStatusWriter
{
    private const string CommentsApiVersion = "api-version=7.1-preview.4";
    private const int BatchSize = 100;
    private static readonly TimeSpan MyselfCacheFor = TimeSpan.FromHours(1);

    // Organization → the token owner's identity id and account name, and when it was asked.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string? Id, string? Account, DateTimeOffset At)> _myself =
        new(StringComparer.OrdinalIgnoreCase);
    private const string AttachedFile = "AttachedFile";
    private const string HierarchyReverse = "System.LinkTypes.Hierarchy-Reverse";

    private const string Name = "Azure DevOps";
    private const string ApiVersion = "api-version=7.1";
    private const int SampleSize = 5;
    // Bounds the id list in the flat preview query: WIQL text is capped at 32K characters.
    private const int MaxCandidates = 2000;

    // The work item categories whose types are tickets an agent works — not the containers.
    private static readonly string[] TicketCategories =
        ["Microsoft.RequirementCategory", "Microsoft.TaskCategory", "Microsoft.BugCategory"];
    private static readonly string[] ContainerCategories =
        ["Microsoft.EpicCategory", "Microsoft.FeatureCategory", "Microsoft.HiddenCategory"];

    public async Task<TrackerIdentity> TestAsync(TrackerCredential c, CancellationToken ct)
    {
        // connectionData is unversioned; it answers for anonymous callers too, so an empty
        // identity is treated as a rejected token.
        var doc = await GetAsync(c, $"{c.SiteUrl}/_apis/connectionData", "your account", ct);
        var user = doc.Prop("authenticatedUser");
        var id = user.Str("id");
        var name = user.Str("providerDisplayName") ?? user.Str("customDisplayName");
        if (id is null || string.Equals(name, "Anonymous", StringComparison.OrdinalIgnoreCase))
            throw new TrackerException(BadToken(c));
        return new TrackerIdentity(id, name);
    }

    public async Task<TrackerScope[]> ScopesAsync(TrackerCredential c, CancellationToken ct)
    {
        var doc = await GetAsync(c, $"{c.SiteUrl}/_apis/projects?$top=200&{ApiVersion}", "projects", ct);
        return [.. doc.Prop("value").Items()
            .Select(p => p.Str("name"))
            .OfType<string>()
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(n => new TrackerScope(n, n))];
    }

    public async Task<TrackerEpic[]> EpicsAsync(TrackerCredential c, string scope, string? search, CancellationToken ct)
    {
        var ids = await WiqlIdsAsync(c, scope, AdoQueries.EpicsWiql(search), top: 20, ct);
        var items = await BatchAsync(c, ids, ct);
        return [.. items.Select(i => new TrackerEpic(
            i.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            i.Title, i.State, WorkItemUrl(c, i.Project ?? scope, i.Id)))];
    }

    public async Task<TrackerStatus[]> StatusesAsync(TrackerCredential c, string scope, CancellationToken ct,
        TrackerBoardRef? board = null, string? statusField = null)
    {
        var states = await StatesAsync(c, scope, ct);
        return states.Statuses;
    }

    /// <summary>A board in Azure DevOps belongs to a team: the project's teams, by name.</summary>
    public async Task<TrackerBoard[]> BoardsAsync(TrackerCredential c, string scope, CancellationToken ct)
    {
        var doc = await GetAsync(c, $"{c.SiteUrl}/_apis/projects/{Uri.EscapeDataString(scope)}/teams?$top=500&{ApiVersion}",
            $"the teams of {scope}", ct);
        return [.. doc.Prop("value").Items()
            .Select(t => (Id: t.Str("id"), Name: t.Str("name")))
            .Where(t => t.Id is not null && t.Name is not null)
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => new TrackerBoard(t.Id!, t.Name!))];
    }

    /// <summary>
    /// The WIQL condition for the team's board, read live from the team's settings so a change to
    /// its area paths applies on the next poll. A team that is gone fails — the source is never
    /// widened to the whole project.
    /// </summary>
    internal async Task<string?> BoardConditionAsync(TrackerCredential c, string scope, TrackerBoardRef? board, CancellationToken ct)
    {
        if (board is null)
            return null;
        var url = $"{c.SiteUrl}/{Uri.EscapeDataString(scope)}/{Uri.EscapeDataString(board.Id)}/_apis/work/teamsettings/teamfieldvalues?{ApiVersion}";
        var doc = await GetOrNullAsync(c, url, $"the board '{board.Label}'", ct) ?? throw board.NotFound(scope);
        return AdoQueries.TeamFieldCondition(doc)
            ?? throw new TrackerException(
                $"The board '{board.Label}' in {scope} has no area paths — give the team its area paths in Azure DevOps, or pick another board in the ticket source settings.");
    }

    public async Task<TrackerPreview> PreviewAsync(TrackerCredential c, string scope, string[] epicKeys,
        string? extraClause, bool includeDescendants, string[]? intakeStatuses, string? scopeMode, CancellationToken ct,
        TrackerBoardRef? board = null, string? statusField = null, string[]? repoFilter = null)
    {
        var epicsMode = TrackerScopeModes.IsEpics(scopeMode);
        var boardCondition = await BoardConditionAsync(c, scope, board, ct);
        string? linkQuery = null;
        List<int>? candidates = null;
        if (epicsMode)
        {
            var epics = ValidEpics(epicKeys, "preview");
            linkQuery = AdoQueries.DescendantsWiql(epics.Distinct(), includeDescendants);
            var links = await PostAsync(c, $"{c.SiteUrl}/{Uri.EscapeDataString(scope)}/_apis/wit/wiql?{ApiVersion}",
                new { query = linkQuery }, "the ticket query", ct);
            var epicSet = epics.ToHashSet();
            candidates = links.Prop("workItemRelations").Items()
                .Where(r => r.Prop("source").ValueKind == JsonValueKind.Object)
                .Select(r => r.Prop("target").Prop("id"))
                .Where(t => t.ValueKind == JsonValueKind.Number)
                .Select(t => t.GetInt32())
                .Where(id => !epicSet.Contains(id))
                .Distinct()
                .Take(MaxCandidates)
                .ToList();
            if (candidates.Count == 0)
                return new TrackerPreview(0, [], linkQuery);
        }

        var states = await StatesAsync(c, scope, ct);
        var wiql = AdoQueries.IntakeWiql(IntakeStates(intakeStatuses, states), states.ContainerTypes, candidates, extraClause,
            boardCondition) + " ORDER BY [System.ChangedDate] DESC";
        var matches = await WiqlIdsAsync(c, scope, wiql, top: null, ct);
        var sample = await BatchAsync(c, matches.Take(SampleSize).ToList(), ct);
        return new TrackerPreview(
            matches.Count,
            [.. sample.Select(i => new TrackerTicketPreview(
                i.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                i.Title, i.Type, i.State, WorkItemUrl(c, i.Project ?? scope, i.Id)))],
            // Both steps when the epics narrow it, so the settings page shows what really ran.
            linkQuery is null ? wiql : $"{linkQuery}\n{wiql}");
    }

    /// <summary>The configured intake states, or the project's Proposed states.</summary>
    private static string[] IntakeStates(string[]? configured, ProjectStates states)
    {
        var picked = (configured ?? []).Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        if (picked.Length > 0)
            return picked;
        return states.ProposedStates.Length > 0
            ? states.ProposedStates
            : throw new TrackerException("This project has no Proposed (to do) states — pick the intake states in the ticket source settings.");
    }

    // ── Status write-back ───────────────────────────────────────────────────

    /// <summary>Sets <c>System.State</c> to <paramref name="targetStatus"/> with a JSON Patch;
    /// a work item already in that state is left alone.</summary>
    public async Task<bool> TransitionAsync(TrackerCredential c, string ticketKey, string ticketId, string targetStatus, CancellationToken ct)
    {
        var id = AdoQueries.AsWorkItemId(ticketId) ?? AdoQueries.AsWorkItemId(ticketKey)
            ?? throw new TrackerException($"'{ticketKey}' is not an Azure DevOps work item id.");
        var url = $"{c.SiteUrl}/_apis/wit/workitems/{id}";

        var item = TrackerHttp.Parse(
            await WriteBackAsync(c, TrackerHttp.Request(HttpMethod.Get, $"{url}?fields=System.State&{ApiVersion}", c), id, targetStatus, ct),
            Name);
        var current = item.Prop("fields").Str("System.State") ?? "";
        if (string.Equals(current.Trim(), targetStatus.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        var patch = TrackerHttp.Request(HttpMethod.Patch, $"{url}?{ApiVersion}", c);
        patch.Content = new StringContent(
            JsonSerializer.Serialize(new[] { new { op = "add", path = "/fields/System.State", value = targetStatus } }),
            System.Text.Encoding.UTF8);
        patch.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json-patch+json");
        await WriteBackAsync(c, patch, id, targetStatus, ct);
        return true;
    }

    private async Task<string> WriteBackAsync(TrackerCredential c, HttpRequestMessage request, int id, string targetStatus, CancellationToken ct)
    {
        var (status, body, mediaType) = await TrackerHttp.SendAsync(http, request, Name, ct);
        if (status == HttpStatusCode.NonAuthoritativeInformation || (int)status is >= 300 and < 400)
            throw new TrackerException(BadToken(c));
        return (int)status switch
        {
            >= 200 and < 300 when mediaType is null || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) => body,
            >= 200 and < 300 => throw new TrackerException(BadToken(c)),
            400 => throw new TrackerException(ErrorText(body) is { } detail
                ? $"Azure DevOps refused the state '{targetStatus}' for work item {id}: {detail}"
                : $"Azure DevOps refused the state '{targetStatus}' for work item {id}."),
            403 => throw new TrackerException($"Your Azure DevOps account cannot move this work item ({id}).{Suffix(ErrorText(body))}"),
            404 => throw new TrackerException($"Work item not found: {id} in {c.SiteUrl}."),
            _ => throw new TrackerException(DescribeError(c, status, body, $"work item {id}")),
        };
    }

    public static string WorkItemUrl(TrackerCredential c, string project, int id)
        => $"{c.SiteUrl}/{Uri.EscapeDataString(project)}/_workitems/edit/{id}";

    // ── Polling ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Two reads. <b>Intake</b>: the work items in the project assigned to the token's owner in an
    /// intake state (default: the Proposed states), containers excluded, among the epics'
    /// descendants when the source is limited to them. <b>Tracking</b>: the ids the host tracks
    /// plus the children of the intake items, read by id whatever their state — a deleted or
    /// moved id is simply omitted (<c>errorPolicy=Omit</c>), which is how the host learns it is
    /// gone. Each ticket's <see cref="RawTicket.EpicKey"/> is the configured epic reached by
    /// walking up the hierarchy, or its parent when that parent is an Epic; its children are
    /// ordered by backlog rank (StackRank / BacklogPriority), then id.
    /// </summary>
    public async Task<IReadOnlyList<RawTicket>> FetchTicketsAsync(TrackerCredential c, TrackerPollAssignment a, CancellationToken ct)
    {
        var epicsMode = TrackerScopeModes.IsEpics(a.ScopeMode);
        // Epics mode: the epics ARE the scope, so a malformed one is an error. All mode: they only
        // name the epic a ticket sits under, for routing — a malformed one is skipped.
        var epics = epicsMode
            ? ValidEpics(a.EpicKeys, "poll")
            : [.. a.EpicKeys.Select(AdoQueries.AsWorkItemId).OfType<int>()];
        var epicSet = epics.ToHashSet();
        var scope = a.Scope;
        // Intake only, like the extra clause: tracked tickets are read by id below, on the board or not.
        var boardCondition = await BoardConditionAsync(c, scope, TrackerBoardRef.From(a.BoardId, a.BoardName), ct);

        var parentOf = new Dictionary<int, int>();
        List<int>? candidates = null;
        if (epics.Count > 0)
        {
            var linkQuery = AdoQueries.DescendantsWiql(epics.Distinct(), a.IncludeDescendants);
            var links = await PostAsync(c, $"{c.SiteUrl}/{Uri.EscapeDataString(scope)}/_apis/wit/wiql?{ApiVersion}",
                new { query = linkQuery }, "the ticket query", ct);
            foreach (var relation in links.Prop("workItemRelations").Items())
            {
                if (relation.Prop("source").Prop("id") is { ValueKind: JsonValueKind.Number } source
                    && relation.Prop("target").Prop("id") is { ValueKind: JsonValueKind.Number } target)
                    parentOf.TryAdd(target.GetInt32(), source.GetInt32());
            }
            if (epicsMode)
            {
                candidates = parentOf.Keys.Where(id => !epicSet.Contains(id)).Distinct().ToList();
                if (candidates.Count > MaxCandidates)
                    throw new TrackerException(
                        $"The epics hold more than {MaxCandidates} work items — too many to poll. Pick narrower epics or turn off descendants.");
            }
        }

        var states = await StatesAsync(c, scope, ct);
        var me = await MyIdentityAsync(c, ct);

        List<int> intakeIds = [];
        if (candidates is not { Count: 0 })
        {
            var wiql = AdoQueries.IntakeWiql(IntakeStates(a.IntakeStatuses, states), states.ContainerTypes, candidates, a.ExtraClause,
                boardCondition) + " ORDER BY [System.Id]";
            intakeIds = await WiqlIdsAsync(c, scope, wiql, top: null, ct);
        }

        var items = new Dictionary<int, JsonElement>();
        await FetchItemsAsync(c, intakeIds, expandRelations: true, items, ct);
        var intakeSet = intakeIds.Where(items.ContainsKey).ToHashSet();

        var trackIds = a.Known.Select(k => AdoQueries.AsWorkItemId(k.Key)).OfType<int>()
            .Concat(intakeSet.SelectMany(id => RelatedIds(items[id], AdoQueries.HierarchyForward)))
            .Where(id => !intakeSet.Contains(id))
            .Distinct()
            .ToList();
        await FetchItemsAsync(c, trackIds, expandRelations: true, items, ct);

        // Parents and children not read above: their type (is the parent an Epic?) and their rank
        // (children are reported in backlog order). All fields — a named rank field the process
        // lacks would fail the whole batch.
        var related = items.Values
            .SelectMany(i => RelatedIds(i, AdoQueries.HierarchyForward).Concat(RelatedIds(i, HierarchyReverse)))
            .Where(id => !items.ContainsKey(id))
            .Distinct()
            .ToList();
        var others = new Dictionary<int, JsonElement>();
        await FetchItemsAsync(c, related, expandRelations: false, others, ct);
        WorkItemInfo Info(int id)
            => items.TryGetValue(id, out var i) || others.TryGetValue(id, out i) ? InfoOf(i) : new WorkItemInfo(null, null);

        var tickets = new List<RawTicket>(items.Count);
        foreach (var id in intakeIds.Where(intakeSet.Contains).Concat(trackIds.Where(items.ContainsKey)))
        {
            if (ParseWorkItem(c, scope, items[id], epicSet, parentOf, states.Categories, me, intakeSet.Contains(id), Info) is { } ticket)
                tickets.Add(ticket);
        }
        return tickets;
    }

    /// <summary>Newest first (<c>order=desc</c>) so one page holds the ones that matter;
    /// returned oldest first. Deleted comments are skipped.</summary>
    public async Task<IReadOnlyList<RawComment>> FetchCommentsAsync(TrackerCredential c, RawTicket ticket,
        DateTimeOffset? after, int max, CancellationToken ct)
    {
        var marker = ticket.Url.LastIndexOf("/_workitems/edit/", StringComparison.Ordinal);
        if (marker < 0)
            throw new TrackerException($"Cannot read the comments of work item {ticket.Key}: unexpected URL.");
        var url = $"{ticket.Url[..marker]}/_apis/wit/workItems/{Uri.EscapeDataString(ticket.Id)}/comments"
            + $"?$top={max.ToString(CultureInfo.InvariantCulture)}&order=desc&$expand=renderedText&{CommentsApiVersion}";
        var doc = await GetAsync(c, url, $"the comments of work item {ticket.Key}", ct);
        return [.. doc.Prop("comments").Items()
            .Where(cm => cm.Prop("isDeleted") is not { ValueKind: JsonValueKind.True })
            .Select(cm => new RawComment(
                cm.Prop("id") is { ValueKind: JsonValueKind.Number } id ? id.GetRawText() : cm.Str("id") ?? "",
                cm.Prop("createdBy").Str("displayName") ?? "Unknown",
                ParseDate(cm.Str("createdDate")) ?? DateTimeOffset.MinValue,
                cm.Str("renderedText") ?? cm.Str("text") ?? ""))
            .Where(cm => after is null || cm.CreatedAt > after)
            .OrderBy(cm => cm.CreatedAt)
            .TakeLast(max)];
    }

    /// <summary>Reads work items by id, <see cref="BatchSize"/> at a time; ids that no longer
    /// exist (or that this account cannot see) are omitted, not an error.</summary>
    private async Task FetchItemsAsync(TrackerCredential c, IReadOnlyCollection<int> ids, bool expandRelations,
        Dictionary<int, JsonElement> into, CancellationToken ct)
    {
        foreach (var chunk in ids.Where(id => !into.ContainsKey(id)).Distinct().Chunk(BatchSize))
        {
            var body = new Dictionary<string, object> { ["ids"] = chunk, ["errorPolicy"] = "Omit" };
            // Relations carry the attachments, the parent and the children. The API refuses
            // fields together with an expand, so every field comes back.
            if (expandRelations)
                body["$expand"] = "Relations";
            var doc = await PostAsync(c, $"{c.SiteUrl}/_apis/wit/workitemsbatch?{ApiVersion}", body, "work items", ct);
            foreach (var item in doc.Prop("value").Items())
            {
                if (item.Prop("id") is { ValueKind: JsonValueKind.Number } id)
                    into[id.GetInt32()] = item;
            }
        }
    }

    internal sealed record WorkItemInfo(string? Type, double? Rank);

    internal static WorkItemInfo InfoOf(JsonElement item)
    {
        var f = item.Prop("fields");
        double? rank = null;
        foreach (var field in (string[])["Microsoft.VSTS.Common.StackRank", "Microsoft.VSTS.Common.BacklogPriority"])
        {
            if (f.Prop(field) is { ValueKind: JsonValueKind.Number } n)
            {
                rank = n.GetDouble();
                break;
            }
        }
        return new WorkItemInfo(f.Str("System.WorkItemType"), rank);
    }

    /// <summary>The ids an item links to with <paramref name="rel"/> (the hierarchy links).</summary>
    internal static IEnumerable<int> RelatedIds(JsonElement item, string rel)
    {
        foreach (var relation in item.Prop("relations").Items())
        {
            if (relation.Str("rel") != rel || relation.Str("url") is not { } url)
                continue;
            var trimmed = url.TrimEnd('/');
            if (int.TryParse(trimmed[(trimmed.LastIndexOf('/') + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                yield return id;
        }
    }

    /// <summary>The token owner's identity — what "assigned to me" is compared with for tracked
    /// work items (the intake query says <c>@Me</c> itself). Asked once an hour per organization.</summary>
    internal async Task<(string? Id, string? Account)> MyIdentityAsync(TrackerCredential c, CancellationToken ct)
    {
        if (_myself.TryGetValue(c.SiteUrl, out var cached) && cached.At > DateTimeOffset.UtcNow - MyselfCacheFor)
            return (cached.Id, cached.Account);
        var doc = await GetAsync(c, $"{c.SiteUrl}/_apis/connectionData", "your account", ct);
        var user = doc.Prop("authenticatedUser");
        var id = user.Str("id");
        var account = user.Prop("properties").Prop("Account").Str("$value");
        _myself[c.SiteUrl] = (id, account, DateTimeOffset.UtcNow);
        return (id, account);
    }

    internal static RawTicket? ParseWorkItem(TrackerCredential c, string scope, JsonElement item, HashSet<int> epicSet,
        Dictionary<int, int> parentOf, Dictionary<string, string?> categories,
        (string? Id, string? Account) me = default, bool inIntake = false, Func<int, WorkItemInfo>? info = null)
    {
        if (item.Prop("id") is not { ValueKind: JsonValueKind.Number } idElement)
            return null;
        var id = idElement.GetInt32();
        var f = item.Prop("fields");
        info ??= _ => new WorkItemInfo(null, null);

        var attachments = new List<RawAttachment>();
        foreach (var relation in item.Prop("relations").Items())
        {
            if (relation.Str("rel") != AttachedFile || relation.Str("url") is not { } url)
                continue;
            var attributes = relation.Prop("attributes");
            var path = url.Split('?')[0].TrimEnd('/');
            attachments.Add(new RawAttachment(
                path[(path.LastIndexOf('/') + 1)..],
                attributes.Str("name") ?? "attachment",
                attributes.Prop("resourceSize") is { ValueKind: JsonValueKind.Number } size ? size.GetInt64() : 0,
                null,
                url));
        }
        int? directParent = RelatedIds(item, HierarchyReverse).Select(p => (int?)p).FirstOrDefault();

        // Walk up the hierarchy the link query returned; the item's own parent relation
        // covers a direct child the tree did not list.
        int? epicKey = null;
        var current = id;
        for (var hop = 0; hop < 16; hop++)
        {
            int? parent = parentOf.TryGetValue(current, out var p) ? p : hop == 0 ? directParent : null;
            if (parent is not { } next)
                break;
            if (epicSet.Contains(next))
            {
                epicKey = next;
                break;
            }
            current = next;
        }
        var parentType = directParent is { } dp ? info(dp).Type : null;
        if (epicKey is null && directParent is not null
            && string.Equals(parentType, "Epic", StringComparison.OrdinalIgnoreCase))
            epicKey = directParent;

        var children = RelatedIds(item, AdoQueries.HierarchyForward)
            .Distinct()
            .Select(child => (Id: child, info(child).Rank))
            .ToList();

        var type = f.Str("System.WorkItemType");
        var state = f.Str("System.State");
        var project = f.Str("System.TeamProject") ?? scope;
        var priority = f.Prop("Microsoft.VSTS.Common.Priority") switch
        {
            { ValueKind: JsonValueKind.Number } n => n.GetRawText(),
            { ValueKind: JsonValueKind.String } t => t.GetString(),
            _ => null,
        };
        var assignee = f.Prop("System.AssignedTo");
        var assignedToMe = inIntake
            || (assignee.ValueKind == JsonValueKind.Object
                && ((me.Id is not null && string.Equals(assignee.Str("id"), me.Id, StringComparison.OrdinalIgnoreCase))
                    || (me.Account is not null && string.Equals(assignee.Str("uniqueName"), me.Account, StringComparison.OrdinalIgnoreCase))));

        return new RawTicket(
            id.ToString(CultureInfo.InvariantCulture),
            id.ToString(CultureInfo.InvariantCulture),
            WorkItemUrl(c, project, id),
            epicKey?.ToString(CultureInfo.InvariantCulture),
            f.Str("System.Title") ?? "",
            type,
            state,
            type is not null && state is not null && categories.TryGetValue(CategoryKey(type, state), out var category)
                ? AdoQueries.StateCategory(category)
                : null,
            priority,
            f.Str("System.Description"),
            f.Str("Microsoft.VSTS.Common.AcceptanceCriteria"),
            f.Str("Microsoft.VSTS.TCM.ReproSteps"),
            [.. (f.Str("System.Tags") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
            [.. attachments],
            ParseDate(f.Str("System.ChangedDate")))
        {
            ParentKey = directParent?.ToString(CultureInfo.InvariantCulture),
            ParentType = parentType,
            ChildKeys = [.. AdoQueries.OrderChildren(children).Select(ch => ch.ToString(CultureInfo.InvariantCulture))],
            AssignedToMe = assignedToMe,
            InIntake = inIntake,
            AreaPath = f.Str("System.AreaPath"),
        };
    }

    private static DateTimeOffset? ParseDate(string? text)
        => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    private static string CategoryKey(string type, string state) => $"{type}\n{state}".ToUpperInvariant();

    private static List<int> ValidEpics(string[] epicKeys, string what)
    {
        var epics = new List<int>();
        foreach (var key in epicKeys.Where(k => !string.IsNullOrWhiteSpace(k)))
        {
            if (AdoQueries.AsWorkItemId(key) is not { } id)
                throw new TrackerException($"'{key}' is not an Azure DevOps work item id — epics are picked by number.");
            epics.Add(id);
        }
        if (epics.Count == 0)
            throw new TrackerException($"Pick at least one epic to {what}.");
        return epics;
    }

    private sealed record ProjectStates(
        TrackerStatus[] Statuses,
        string[] DoneStates,
        Dictionary<string, string?> Categories,
        // The Proposed-category states of the ticket types — the default intake.
        string[] ProposedStates,
        // Epic / Feature / hidden types: containers of work, never picked up as tickets.
        string[] ContainerTypes);

    /// <summary>The selectable statuses (ticket types only), every done/removed state name in
    /// the project — what a preview has to exclude — the Proposed states (the default intake)
    /// and the container types.</summary>
    private async Task<ProjectStates> StatesAsync(
        TrackerCredential c, string scope, CancellationToken ct)
    {
        var project = Uri.EscapeDataString(scope);
        var typesDoc = await GetAsync(c, $"{c.SiteUrl}/{project}/_apis/wit/workitemtypes?{ApiVersion}", $"project {scope}", ct);
        var categoriesDoc = await GetAsync(c, $"{c.SiteUrl}/{project}/_apis/wit/workitemtypecategories?{ApiVersion}", $"project {scope}", ct);

        var byCategory = categoriesDoc.Prop("value").Items()
            .Where(cat => cat.Str("referenceName") is not null)
            .ToDictionary(
                cat => cat.Str("referenceName")!,
                cat => cat.Prop("workItemTypes").Items().Select(t => t.Str("name")).OfType<string>().ToList());
        var ticketTypes = TicketCategories.SelectMany(k => byCategory.GetValueOrDefault(k) ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var containerTypes = ContainerCategories.SelectMany(k => byCategory.GetValueOrDefault(k) ?? [])
            .Concat(["Epic", "Feature"])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var statuses = new List<TrackerStatus>();
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var proposed = new List<string>();
        var categories = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var type in typesDoc.Prop("value").Items())
        {
            var typeName = type.Str("name") ?? "";
            var states = type.Prop("states").Items().ToList();
            foreach (var s in states)
            {
                if (s.Str("category") is "Completed" or "Removed" && s.Str("name") is { } doneName)
                    done.Add(doneName);
                if (s.Str("name") is { } stateName)
                    categories.TryAdd(CategoryKey(typeName, stateName), s.Str("category"));
            }

            // Categories present: ticket categories decide. Absent (an old or odd process):
            // everything but the containers and hidden types.
            var isTicketType = ticketTypes.Count > 0 ? ticketTypes.Contains(typeName) : !containerTypes.Contains(typeName);
            if (!isTicketType || type.Prop("isDisabled") is { ValueKind: JsonValueKind.True })
                continue;
            foreach (var s in states)
            {
                if (s.Str("name") is not { } name || AdoQueries.StateCategory(s.Str("category")) is not { } category)
                    continue;
                if (seen.Add(name))
                {
                    statuses.Add(new TrackerStatus(name, category, s.Str("category")));
                    if (s.Str("category") == "Proposed")
                        proposed.Add(name);
                }
            }
        }
        return new ProjectStates([.. statuses], [.. done], categories, [.. proposed], [.. containerTypes]);
    }

    private async Task<List<int>> WiqlIdsAsync(TrackerCredential c, string scope, string wiql, int? top, CancellationToken ct)
    {
        var url = $"{c.SiteUrl}/{Uri.EscapeDataString(scope)}/_apis/wit/wiql?{(top is { } t ? $"$top={t}&" : "")}{ApiVersion}";
        var doc = await PostAsync(c, url, new { query = wiql }, "the ticket query", ct);
        return [.. doc.Prop("workItems").Items()
            .Select(w => w.Prop("id"))
            .Where(id => id.ValueKind == JsonValueKind.Number)
            .Select(id => id.GetInt32())];
    }

    private sealed record WorkItem(int Id, string Title, string? State, string? Type, string? Project);

    /// <summary>Titles and states for ids, in the order asked (WIQL's sort order).</summary>
    private async Task<List<WorkItem>> BatchAsync(TrackerCredential c, IReadOnlyList<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return [];
        var doc = await PostAsync(c, $"{c.SiteUrl}/_apis/wit/workitemsbatch?{ApiVersion}", new
        {
            ids = ids.Take(200).ToArray(),
            fields = new[] { "System.Id", "System.Title", "System.State", "System.WorkItemType", "System.TeamProject" },
            errorPolicy = "Omit",
        }, "work items", ct);

        var byId = new Dictionary<int, WorkItem>();
        foreach (var item in doc.Prop("value").Items())
        {
            if (item.Prop("id") is not { ValueKind: JsonValueKind.Number } idElement)
                continue;
            var f = item.Prop("fields");
            var id = idElement.GetInt32();
            byId[id] = new WorkItem(id, f.Str("System.Title") ?? "", f.Str("System.State"),
                f.Str("System.WorkItemType"), f.Str("System.TeamProject"));
        }
        return [.. ids.Where(byId.ContainsKey).Select(id => byId[id])];
    }

    private Task<JsonElement> GetAsync(TrackerCredential c, string url, string what, CancellationToken ct)
        => SendAsync(c, TrackerHttp.Request(HttpMethod.Get, url, c), what, ct);

    private Task<JsonElement> PostAsync(TrackerCredential c, string url, object body, string what, CancellationToken ct)
        => SendAsync(c, TrackerHttp.Request(HttpMethod.Post, url, c, body), what, ct);

    /// <summary>A GET whose 404 means "not there" (null) rather than an error.</summary>
    private async Task<JsonElement?> GetOrNullAsync(TrackerCredential c, string url, string what, CancellationToken ct)
    {
        var (status, body, mediaType) = await TrackerHttp.SendAsync(http, TrackerHttp.Request(HttpMethod.Get, url, c), Name, ct);
        return status == HttpStatusCode.NotFound ? null : Interpret(c, status, body, mediaType, what);
    }

    private async Task<JsonElement> SendAsync(TrackerCredential c, HttpRequestMessage request, string what, CancellationToken ct)
    {
        var (status, body, mediaType) = await TrackerHttp.SendAsync(http, request, Name, ct);
        return Interpret(c, status, body, mediaType, what);
    }

    private static JsonElement Interpret(TrackerCredential c, HttpStatusCode status, string body, string? mediaType, string what)
    {
        // A bad or missing PAT is answered with 203 + the HTML sign-in page, or a redirect to it.
        if (status == HttpStatusCode.NonAuthoritativeInformation || (int)status is >= 300 and < 400)
            throw new TrackerException(BadToken(c));
        if ((int)status is >= 200 and < 300)
        {
            if (mediaType is not null && !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
                throw new TrackerException(BadToken(c));
            return TrackerHttp.Parse(body, Name);
        }
        throw new TrackerException(DescribeError(c, status, body, what));
    }

    internal static string DescribeError(TrackerCredential c, HttpStatusCode status, string body, string what)
    {
        var detail = ErrorText(body);
        return status switch
        {
            HttpStatusCode.Unauthorized => BadToken(c),
            HttpStatusCode.Forbidden =>
                $"Azure DevOps refused access to {what} — the token needs the Work Items (Read & write) and Project and Team (Read) scopes.{Suffix(detail)}",
            HttpStatusCode.NotFound =>
                $"Azure DevOps could not find {what} in {c.SiteUrl}.{Suffix(detail)}",
            HttpStatusCode.BadRequest =>
                detail is not null ? $"Azure DevOps rejected the query: {detail}" : "Azure DevOps rejected the query.",
            HttpStatusCode.TooManyRequests =>
                "Azure DevOps is rate limiting this account — try again in a minute.",
            _ => $"Azure DevOps answered HTTP {(int)status} for {what}.{Suffix(detail)}",
        };
    }

    internal static string BadToken(TrackerCredential c) =>
        $"Azure DevOps rejected the personal access token for {c.SiteUrl} — it may be expired, revoked, or made for another organization. "
        + "Create one under User settings → Personal access tokens with the Work Items (Read & write) and Project and Team (Read) scopes and run `./orkka tracker add ado` again.";

    internal static string? ErrorText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Str("message");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Suffix(string? detail) => detail is null ? "" : $" ({detail})";
}
