namespace Orkka.LocalRunner.Trackers;

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Orkka.LocalRunner.Contracts;

/// <summary>JQL built from user input. Pure, so the escaping is testable on its own.</summary>
public static partial class JiraQueries
{
    /// <summary>A JQL string literal: quotes and backslashes escaped.</summary>
    public static string Literal(string value)
        => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    public static bool LooksLikeIssueKey(string? text) => text is not null && IssueKey().IsMatch(text.Trim());

    /// <summary>
    /// Recent epics in <paramref name="project"/>; with a search, epics whose summary starts a
    /// word with it — or, when the search is an issue key (or a bare number, read as a key in
    /// this project), exactly that epic.
    /// </summary>
    public static string EpicsJql(string project, string? search)
    {
        var jql = new StringBuilder($"project = {Literal(project)} AND issuetype = Epic");
        var text = search?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            if (text.All(char.IsAsciiDigit))
                text = $"{project}-{text}";
            if (LooksLikeIssueKey(text))
            {
                jql.Append($" AND key = {Literal(text.ToUpperInvariant())}");
            }
            else
            {
                var terms = SearchText(text);
                if (terms.Length > 0)
                    jql.Append($" AND summary ~ {Literal(terms + "*")}");
            }
        }
        return jql.Append(" ORDER BY updated DESC").ToString();
    }

    /// <summary>
    /// The tickets a configuration picks up: open, assigned to the token's owner, directly
    /// under one of the epics (or under one of their children too — <paramref name="childKeys"/>,
    /// fetched first — when descendants are included), plus the project's own condition.
    /// </summary>
    public static string PreviewJql(IEnumerable<string> epicKeys, IEnumerable<string>? childKeys, string? extraClause)
    {
        var parents = epicKeys.Concat(childKeys ?? [])
            .Select(k => k.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .Select(Literal);
        var jql = $"parent in ({string.Join(", ", parents)}) AND assignee = currentUser() AND statusCategory != Done";
        if (!string.IsNullOrWhiteSpace(extraClause))
            jql += $" AND ({extraClause.Trim()})";
        return jql;
    }

    /// <summary>
    /// The intake query: tickets in <paramref name="project"/> assigned to the token's owner
    /// in one of <paramref name="intakeStatuses"/> (none = the "To Do" status category),
    /// directly under one of <paramref name="epicKeys"/> (or one of their children,
    /// <paramref name="childKeys"/>) when the source is limited to epics, plus the project's own
    /// condition. No "not done" clause: the statuses say exactly where a ticket is picked up.
    /// </summary>
    public static string IntakeJql(string project, IReadOnlyCollection<string>? intakeStatuses,
        IReadOnlyCollection<string>? epicKeys, IEnumerable<string>? childKeys, string? extraClause)
    {
        var jql = new StringBuilder($"project = {Literal(project.Trim())} AND assignee = currentUser()");
        var statuses = (intakeStatuses ?? [])
            .Select(s => s.Trim()).Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        jql.Append(statuses.Count > 0
            ? $" AND status in ({string.Join(", ", statuses.Select(Literal))})"
            : " AND statusCategory = \"To Do\"");
        if (epicKeys is { Count: > 0 })
        {
            var parents = epicKeys.Concat(childKeys ?? [])
                .Select(k => k.Trim().ToUpperInvariant())
                .Distinct(StringComparer.Ordinal)
                .Select(Literal);
            jql.Append($" AND parent in ({string.Join(", ", parents)})");
        }
        if (!string.IsNullOrWhiteSpace(extraClause))
            jql.Append($" AND ({extraClause.Trim()})");
        return jql.ToString();
    }

    /// <summary>The tracking read: these keys, whatever their status or assignee.</summary>
    public static string TrackingJql(IEnumerable<string> keys)
        => $"key in ({string.Join(", ", keys.Select(k => Literal(k.Trim().ToUpperInvariant())))})";

    /// <summary>The issue keys a JQL error says do not exist ("An issue with key 'SHOP-9' does
    /// not exist for field 'key'.") — a deleted ticket, one moved to another project, or one
    /// this account can no longer see. Jira fails the whole query for one of them.</summary>
    public static IReadOnlyList<string> MissingKeys(string? error)
        => string.IsNullOrEmpty(error)
            ? []
            : [.. QuotedKey().Matches(error).Select(m => m.Groups[1].Value.ToUpperInvariant()).Distinct(StringComparer.Ordinal)];

    public static string ChildrenJql(IEnumerable<string> epicKeys)
        => $"parent in ({string.Join(", ", epicKeys.Select(k => Literal(k.Trim().ToUpperInvariant())))})";

    /// <summary>Jira text search is Lucene underneath: its operators in a title search would
    /// change the meaning (or fail the query), so they become plain spaces.</summary>
    internal static string SearchText(string text)
        => Spaces().Replace(LuceneSpecial().Replace(text, " "), " ").Trim();

    public static string StatusCategory(string? jiraCategoryKey) => jiraCategoryKey switch
    {
        "new" => "todo",
        "indeterminate" => "inprogress",
        "done" => "done",
        _ => "todo",
    };

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*-\d+$")]
    private static partial Regex IssueKey();

    [GeneratedRegex(@"[+\-&|!(){}\[\]^~*?:\\/""']")]
    private static partial Regex LuceneSpecial();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"'([A-Za-z][A-Za-z0-9_]*-\d+)'")]
    private static partial Regex QuotedKey();
}

/// <summary>Jira Cloud REST v3 with the developer's email + API token (Basic auth).</summary>
public sealed partial class JiraCloudClient(HttpClient http) : ITrackerClient, ITrackerTicketSource, ITrackerStatusWriter
{
    private const string Name = "Jira";
    private const int MaxProjectPages = 5;
    private const int MaxChildPages = 5;
    private const int ChildPageSize = 100;
    private const int SampleSize = 5;
    private const int TicketPageSize = 100;
    private const int MaxTicketPages = 20;
    internal const string AcceptanceCriteriaFieldName = "Acceptance Criteria";

    private const int TrackingBatchSize = 100;
    private const int MaxMissingKeyRetries = 10;
    private static readonly TimeSpan MyselfCacheFor = TimeSpan.FromHours(1);

    private static readonly string[] TicketFields =
        ["summary", "issuetype", "status", "priority", "labels", "description", "attachment", "updated", "parent",
         "subtasks", "assignee", "components"];

    // Site + email → the token owner's accountId and when it was asked.
    private readonly ConcurrentDictionary<string, (string? AccountId, DateTimeOffset At)> _myself = new(StringComparer.OrdinalIgnoreCase);

    // Site → the id of its "Acceptance Criteria" custom field (null: the site has none).
    // Discovered once per site per process; a failed lookup is not cached.
    private readonly ConcurrentDictionary<string, string?> _acceptanceCriteriaField = new(StringComparer.OrdinalIgnoreCase);

    public async Task<TrackerIdentity> TestAsync(TrackerCredential c, CancellationToken ct)
    {
        var me = await GetAsync(c, "/rest/api/3/myself", "your account", ct);
        return new TrackerIdentity(me.Str("accountId"), me.Str("displayName"));
    }

    public async Task<TrackerScope[]> ScopesAsync(TrackerCredential c, CancellationToken ct)
    {
        var scopes = new List<TrackerScope>();
        var startAt = 0;
        for (var page = 0; page < MaxProjectPages; page++)
        {
            var doc = await GetAsync(c, $"/rest/api/3/project/search?maxResults=100&startAt={startAt}&orderBy=name", "projects", ct);
            var values = doc.Prop("values").Items().ToList();
            foreach (var p in values)
            {
                if (p.Str("key") is { } key)
                    scopes.Add(new TrackerScope(key, p.Str("name") ?? key));
            }
            var isLast = doc.Prop("isLast") is { ValueKind: JsonValueKind.True } || values.Count == 0;
            if (isLast)
                break;
            startAt += values.Count;
        }
        return [.. scopes];
    }

    public async Task<TrackerEpic[]> EpicsAsync(TrackerCredential c, string scope, string? search, CancellationToken ct)
    {
        var jql = JiraQueries.EpicsJql(scope, search);
        JsonElement doc;
        try
        {
            doc = await SearchAsync(c, jql, ["summary", "status"], 20, null, ct);
        }
        catch (TrackerException) when (IsKeySearch(scope, search))
        {
            // Jira fails the whole query when `key = X` names an issue that does not exist
            // (or that this account cannot see) — for a search box that just means no match.
            return [];
        }
        return [.. doc.Prop("issues").Items().Select(i => new TrackerEpic(
            i.Str("key") ?? "",
            i.Prop("fields").Str("summary") ?? "",
            i.Prop("fields").Prop("status").Str("name"),
            $"{c.SiteUrl}/browse/{i.Str("key")}"))];
    }

    public async Task<TrackerStatus[]> StatusesAsync(TrackerCredential c, string scope, CancellationToken ct)
    {
        var doc = await GetAsync(c, $"/rest/api/3/project/{Uri.EscapeDataString(scope)}/statuses", $"project {scope}", ct);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var statuses = new List<TrackerStatus>();
        foreach (var type in doc.Items())
        {
            // Epic statuses are not where a ticket moves; the epic is the container.
            if (string.Equals(type.Str("name"), "Epic", StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var s in type.Prop("statuses").Items())
            {
                if (s.Str("name") is { } name && seen.Add(name))
                    statuses.Add(new TrackerStatus(name, JiraQueries.StatusCategory(s.Prop("statusCategory").Str("key"))));
            }
        }
        return [.. statuses];
    }

    public async Task<TrackerPreview> PreviewAsync(TrackerCredential c, string scope, string[] epicKeys,
        string? extraClause, bool includeDescendants, string[]? intakeStatuses, string? scopeMode, CancellationToken ct)
    {
        var epicsMode = TrackerScopeModes.IsEpics(scopeMode);
        var epics = epicsMode ? ValidEpics(scope, epicKeys, "preview") : [];

        List<string>? children = null;
        if (epicsMode && includeDescendants)
            children = [.. (await ChildrenAsync(c, epics, ct)).Children.Select(ch => ch.Key)];

        var jql = JiraQueries.IntakeJql(scope, intakeStatuses, epicsMode ? epics : null, children, extraClause);
        var count = await PostAsync(c, "/rest/api/3/search/approximate-count", new { jql }, "the ticket query", ct);
        var sample = await SearchAsync(c, jql + " ORDER BY updated DESC", ["summary", "status", "issuetype"], SampleSize, null, ct);

        return new TrackerPreview(
            count.Prop("count") is { ValueKind: JsonValueKind.Number } n ? n.GetInt32() : 0,
            [.. sample.Prop("issues").Items().Select(i => new TrackerTicketPreview(
                i.Str("key") ?? "",
                i.Prop("fields").Str("summary") ?? "",
                i.Prop("fields").Prop("issuetype").Str("name"),
                i.Prop("fields").Prop("status").Str("name"),
                $"{c.SiteUrl}/browse/{i.Str("key")}"))],
            jql);
    }

    // ── Polling ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Two queries. <b>Intake</b>: every ticket in the scope assigned to the token's owner in an
    /// intake status (under the epics when the source is limited to them), paged to the end with
    /// <c>nextPageToken</c>. <b>Tracking</b>: the keys the host tracks plus the sub-tasks of the
    /// intake tickets, read by key whatever their status (<c>key in (…)</c>, 100 at a time) — a
    /// key Jira says does not exist is dropped from the batch and the batch asked again, so one
    /// deleted ticket never fails the poll. Each ticket's <see cref="RawTicket.EpicKey"/> is the
    /// configured epic it sits under (its parent, or with descendants its parent's parent), or its
    /// parent when that parent is an epic.
    /// </summary>
    public async Task<IReadOnlyList<RawTicket>> FetchTicketsAsync(TrackerCredential c, TrackerPollAssignment a, CancellationToken ct)
    {
        var epicsMode = TrackerScopeModes.IsEpics(a.ScopeMode);
        // Epics mode: the epics ARE the scope, so a malformed one is an error. All mode: they only
        // name the epic a ticket sits under, for routing — a malformed one is skipped.
        var epics = epicsMode
            ? ValidEpics(a.Scope, a.EpicKeys, "poll")
            : [.. a.EpicKeys.Select(k => k.Trim()).Where(JiraQueries.LooksLikeIssueKey)];
        var epicSet = epics.Select(k => k.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);

        List<string>? children = null;
        var parentOfChild = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (a.IncludeDescendants && epics.Length > 0)
        {
            var (list, truncated) = await ChildrenAsync(c, epics, ct);
            if (truncated && epicsMode)
                throw new TrackerException(
                    $"The epics have more than {MaxChildPages * ChildPageSize} child issues — too many to poll. Pick narrower epics or turn off descendants.");
            children = [.. list.Select(ch => ch.Key)];
            foreach (var (key, parent) in list)
            {
                if (parent is not null)
                    parentOfChild[key] = parent.ToUpperInvariant();
            }
        }

        var me = await MyAccountIdAsync(c, ct);
        var acceptanceField = await AcceptanceCriteriaFieldAsync(c, ct);
        string[] fields = acceptanceField is null ? TicketFields : [.. TicketFields, acceptanceField];
        var context = new ParseContext(c, acceptanceField, epicSet, parentOfChild, me);

        var jql = JiraQueries.IntakeJql(a.Scope, a.IntakeStatuses, epicsMode ? epics : null, epicsMode ? children : null,
            a.ExtraClause) + " ORDER BY key ASC";
        var intake = new List<RawTicket>();
        await SearchAllAsync(c, jql, fields, MaxTicketPages,
            $"More than {MaxTicketPages * TicketPageSize} tickets match — too many to poll. Narrow the intake statuses, the epics or the extra condition.",
            issue =>
            {
                // An epic is a container, never a task — whoever it is assigned to.
                if (IsContainer(issue))
                    return;
                if (ParseIssue(context, issue, inIntake: true) is { } ticket)
                    intake.Add(ticket);
            }, ct);

        var have = intake.Select(t => t.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var trackKeys = a.Known.Select(k => k.Key)
            .Concat(intake.SelectMany(t => t.ChildKeys))
            .Select(k => k.Trim().ToUpperInvariant())
            .Where(k => JiraQueries.LooksLikeIssueKey(k) && !have.Contains(k))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var tracked = await FetchByKeysAsync(context, trackKeys, fields, ct);
        return [.. intake, .. tracked];
    }

    /// <summary>The tracking read, 100 keys a query. Jira fails a whole <c>key in (…)</c> query
    /// when one key does not exist (deleted, moved, no longer visible): those keys are dropped
    /// and the rest asked again — their absence from the report is how the host learns.</summary>
    private async Task<List<RawTicket>> FetchByKeysAsync(ParseContext context, List<string> keys, string[] fields, CancellationToken ct)
    {
        var tickets = new List<RawTicket>();
        foreach (var chunk in keys.Chunk(TrackingBatchSize))
        {
            var batch = chunk.ToList();
            for (var attempt = 0; batch.Count > 0; attempt++)
            {
                try
                {
                    var found = new List<RawTicket>();
                    await SearchAllAsync(context.Credential, JiraQueries.TrackingJql(batch), fields, MaxTicketPages,
                        "Too many tracked tickets to read.",
                        issue =>
                        {
                            if (ParseIssue(context, issue, inIntake: false) is { } ticket)
                                found.Add(ticket);
                        }, ct);
                    tickets.AddRange(found);
                    break;
                }
                catch (TrackerException ex) when (attempt < MaxMissingKeyRetries)
                {
                    var missing = JiraQueries.MissingKeys(ex.Message).ToHashSet(StringComparer.Ordinal);
                    if (missing.Count == 0 || batch.RemoveAll(missing.Contains) == 0)
                        throw;
                }
            }
        }
        return tickets;
    }

    private async Task SearchAllAsync(TrackerCredential c, string jql, string[] fields, int maxPages, string tooMany,
        Action<JsonElement> onIssue, CancellationToken ct)
    {
        string? token = null;
        for (var page = 0; ; page++)
        {
            if (page >= maxPages)
                throw new TrackerException(tooMany);
            var doc = await PostAsync(c, "/rest/api/3/search/jql",
                token is null
                    ? new { jql, fields, maxResults = TicketPageSize, expand = "renderedFields" }
                    : (object)new { jql, fields, maxResults = TicketPageSize, nextPageToken = token, expand = "renderedFields" },
                "the ticket query", ct);
            foreach (var issue in doc.Prop("issues").Items())
                onIssue(issue);
            token = doc.Str("nextPageToken");
            if (token is null || doc.Prop("isLast") is { ValueKind: JsonValueKind.True })
                return;
        }
    }

    /// <summary>The token owner's accountId — what "assigned to me" is compared with for tracked
    /// tickets (the intake query says <c>currentUser()</c> itself). Asked once an hour per account.</summary>
    internal async Task<string?> MyAccountIdAsync(TrackerCredential c, CancellationToken ct)
    {
        var cacheKey = $"{c.SiteUrl}|{c.Email}";
        if (_myself.TryGetValue(cacheKey, out var cached) && cached.At > DateTimeOffset.UtcNow - MyselfCacheFor)
            return cached.AccountId;
        var me = await GetAsync(c, "/rest/api/3/myself", "your account", ct);
        var accountId = me.Str("accountId");
        _myself[cacheKey] = (accountId, DateTimeOffset.UtcNow);
        return accountId;
    }

    /// <summary>An epic (hierarchy level 1 or above) — a container of work, not a ticket.</summary>
    private static bool IsContainer(JsonElement issue)
    {
        var type = issue.Prop("fields").Prop("issuetype");
        return type.Prop("hierarchyLevel") is { ValueKind: JsonValueKind.Number } level
            ? level.GetInt32() >= 1
            : string.Equals(type.Str("name"), "Epic", StringComparison.OrdinalIgnoreCase);
    }
    /// <summary>The newest comments first (<c>orderBy=-created</c>), so one page of
    /// <paramref name="max"/> holds exactly the ones that matter; returned oldest first.</summary>
    public async Task<IReadOnlyList<RawComment>> FetchCommentsAsync(TrackerCredential c, RawTicket ticket,
        DateTimeOffset? after, int max, CancellationToken ct)
    {
        var doc = await GetAsync(c,
            $"/rest/api/3/issue/{Uri.EscapeDataString(ticket.Key)}/comment?orderBy=-created&maxResults={max.ToString(CultureInfo.InvariantCulture)}&expand=renderedBody",
            $"the comments of {ticket.Key}", ct);
        return [.. doc.Prop("comments").Items()
            .Select(cm => new RawComment(
                cm.Str("id") ?? "",
                cm.Prop("author").Str("displayName") ?? "Unknown",
                ParseDate(cm.Str("created")) ?? DateTimeOffset.MinValue,
                cm.Str("renderedBody") ?? AdfAsHtml(cm.Prop("body"))))
            .Where(cm => after is null || cm.CreatedAt > after)
            .OrderBy(cm => cm.CreatedAt)
            .TakeLast(max)];
    }

    /// <summary>The site's "Acceptance Criteria" field id, looked up once per site.</summary>
    internal async Task<string?> AcceptanceCriteriaFieldAsync(TrackerCredential c, CancellationToken ct)
    {
        if (_acceptanceCriteriaField.TryGetValue(c.SiteUrl, out var cached))
            return cached;
        var doc = await GetAsync(c, "/rest/api/3/field", "the field list", ct);
        var id = doc.Items()
            .FirstOrDefault(f => string.Equals(f.Str("name")?.Trim(), AcceptanceCriteriaFieldName, StringComparison.OrdinalIgnoreCase))
            .Str("id");
        _acceptanceCriteriaField[c.SiteUrl] = id;
        return id;
    }

    private sealed record ParseContext(
        TrackerCredential Credential,
        string? AcceptanceField,
        HashSet<string> EpicSet,
        Dictionary<string, string> ParentOfChild,
        string? MyAccountId);

    internal static RawTicket? ParseIssue(TrackerCredential c, JsonElement issue, string? acceptanceField,
        HashSet<string> epicSet, Dictionary<string, string> parentOfChild, string? myAccountId = null, bool inIntake = false)
        => ParseIssue(new ParseContext(c, acceptanceField, epicSet, parentOfChild, myAccountId), issue, inIntake);

    private static RawTicket? ParseIssue(ParseContext context, JsonElement issue, bool inIntake)
    {
        if (issue.Str("key") is not { } key)
            return null;
        var c = context.Credential;
        var acceptanceField = context.AcceptanceField;
        var f = issue.Prop("fields");
        var rendered = issue.Prop("renderedFields");

        var parent = f.Prop("parent");
        var parentKey = parent.Str("key")?.ToUpperInvariant();
        var parentType = parent.Prop("fields").Prop("issuetype");
        var parentIsEpic = parentType.Prop("hierarchyLevel") is { ValueKind: JsonValueKind.Number } level
            ? level.GetInt32() >= 1
            : string.Equals(parentType.Str("name"), "Epic", StringComparison.OrdinalIgnoreCase);
        string? epicKey = null;
        if (parentKey is not null)
        {
            if (context.EpicSet.Contains(parentKey))
                epicKey = parentKey;
            else if (context.ParentOfChild.TryGetValue(parentKey, out var grandparent) && context.EpicSet.Contains(grandparent))
                epicKey = grandparent;
            else if (parentIsEpic)
                epicKey = parentKey;
        }

        string? acceptance = null;
        if (acceptanceField is not null)
        {
            acceptance = rendered.Str(acceptanceField);
            if (string.IsNullOrWhiteSpace(acceptance))
                acceptance = AdfAsHtml(f.Prop(acceptanceField));
        }

        var description = rendered.Str("description");
        if (string.IsNullOrWhiteSpace(description))
            description = AdfAsHtml(f.Prop("description"));

        var assignee = f.Prop("assignee").Str("accountId");
        var assignedToMe = inIntake
            || (assignee is not null && context.MyAccountId is not null
                && string.Equals(assignee, context.MyAccountId, StringComparison.Ordinal));

        var status = f.Prop("status");
        return new RawTicket(
            key,
            issue.Str("id") ?? key,
            $"{c.SiteUrl}/browse/{key}",
            epicKey,
            f.Str("summary") ?? "",
            f.Prop("issuetype").Str("name"),
            status.Str("name"),
            status.Prop("statusCategory").Str("key") is { } category ? JiraQueries.StatusCategory(category) : null,
            f.Prop("priority").Str("name"),
            description,
            string.IsNullOrWhiteSpace(acceptance) ? null : acceptance,
            null,
            [.. f.Prop("labels").Items().Where(l => l.ValueKind == JsonValueKind.String).Select(l => l.GetString()!)],
            [.. f.Prop("attachment").Items()
                .Where(at => at.Str("id") is not null && at.Str("content") is not null)
                .Select(at => new RawAttachment(
                    at.Str("id")!,
                    at.Str("filename") ?? "attachment",
                    at.Prop("size") is { ValueKind: JsonValueKind.Number } size ? size.GetInt64() : 0,
                    at.Str("mimeType"),
                    at.Str("content")!))],
            ParseDate(f.Str("updated")))
        {
            ParentKey = parentKey,
            ParentType = parentKey is null ? null : parentType.Str("name"),
            // Jira lists a story's sub-tasks in their board order.
            ChildKeys = [.. f.Prop("subtasks").Items().Select(s => s.Str("key")).OfType<string>()],
            AssignedToMe = assignedToMe,
            InIntake = inIntake,
            Components = [.. f.Prop("components").Items().Select(cp => cp.Str("name")).OfType<string>()],
        };
    }

    /// <summary>Jira writes offsets without a colon (<c>2026-10-01T09:30:00.000+0000</c>).</summary>
    internal static DateTimeOffset? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var normalized = CompactOffset().Replace(text.Trim(), "$1:$2");
        return DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;
    }

    /// <summary>A plain-text or Atlassian Document Format value as minimal HTML — only for
    /// the rare field Jira did not render.</summary>
    internal static string? AdfAsHtml(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
            return WebUtility.HtmlEncode(value.GetString());
        if (value.ValueKind != JsonValueKind.Object)
            return null;
        var html = new StringBuilder();
        AppendAdf(value, html);
        return html.Length == 0 ? null : html.ToString();
    }

    private static void AppendAdf(JsonElement node, StringBuilder html)
    {
        switch (node.Str("type"))
        {
            case "text":
                html.Append(WebUtility.HtmlEncode(node.Str("text")));
                return;
            case "hardBreak":
                html.Append("<br>");
                return;
            case "paragraph" or "heading" or "listItem" or "codeBlock" or "blockquote":
                html.Append("<p>");
                foreach (var child in node.Prop("content").Items())
                    AppendAdf(child, html);
                html.Append("</p>");
                return;
            default:
                foreach (var child in node.Prop("content").Items())
                    AppendAdf(child, html);
                return;
        }
    }

    private static string[] ValidEpics(string scope, string[] epicKeys, string what)
    {
        var epics = epicKeys.Select(k => k.Trim()).Where(k => k.Length > 0).ToArray();
        if (epics.Length == 0)
            throw new TrackerException($"Pick at least one epic to {what}.");
        if (epics.FirstOrDefault(k => !JiraQueries.LooksLikeIssueKey(k)) is { } bad)
            throw new TrackerException($"'{bad}' is not a Jira issue key (like {scope}-12).");
        return epics;
    }

    /// <summary>The epics' direct children with their parent, and whether the paging cap cut
    /// the list short.</summary>
    private async Task<(List<(string Key, string? Parent)> Children, bool Truncated)> ChildrenAsync(
        TrackerCredential c, string[] epics, CancellationToken ct)
    {
        var children = new List<(string Key, string? Parent)>();
        string? token = null;
        for (var page = 0; page < MaxChildPages; page++)
        {
            var doc = await SearchAsync(c, JiraQueries.ChildrenJql(epics), ["issuetype", "parent"], ChildPageSize, token, ct);
            children.AddRange(doc.Prop("issues").Items()
                .Where(i => i.Str("key") is not null)
                .Select(i => (i.Str("key")!, i.Prop("fields").Prop("parent").Str("key"))));
            token = doc.Str("nextPageToken");
            if (token is null || doc.Prop("isLast") is { ValueKind: JsonValueKind.True })
                return (children, false);
        }
        return (children, true);
    }

    [GeneratedRegex(@"([+-]\d{2})(\d{2})$")]
    private static partial Regex CompactOffset();

    // ── Status write-back ───────────────────────────────────────────────────

    /// <summary>
    /// Moves the issue through the workflow transition whose destination status is
    /// <paramref name="targetStatus"/> (falling back to a transition with that name). A ticket
    /// already in that status is left alone, so a retried move is harmless.
    /// </summary>
    public async Task<bool> TransitionAsync(TrackerCredential c, string ticketKey, string ticketId, string targetStatus, CancellationToken ct)
    {
        var key = Uri.EscapeDataString(ticketKey);
        var issue = TrackerHttp.Parse(
            await WriteBackAsync(c, TrackerHttp.Request(HttpMethod.Get, $"{c.SiteUrl}/rest/api/3/issue/{key}?fields=status", c), ticketKey, ct),
            Name);
        var current = issue.Prop("fields").Prop("status").Str("name") ?? "";
        if (string.Equals(current.Trim(), targetStatus.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        var transitions = TrackerHttp.Parse(
            await WriteBackAsync(c, TrackerHttp.Request(HttpMethod.Get, $"{c.SiteUrl}/rest/api/3/issue/{key}/transitions", c), ticketKey, ct),
            Name).Prop("transitions").Items().ToList();
        var pick = transitions.FirstOrDefault(t => Same(t.Prop("to").Str("name"), targetStatus));
        if (pick.ValueKind == JsonValueKind.Undefined)
            pick = transitions.FirstOrDefault(t => Same(t.Str("name"), targetStatus));
        if (pick.ValueKind == JsonValueKind.Undefined || pick.Str("id") is not { } transitionId)
        {
            var available = transitions
                .Select(t => t.Prop("to").Str("name") ?? t.Str("name"))
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            throw new TrackerException(
                $"No transition to '{targetStatus}' from '{current}' in this workflow — available: "
                + (available.Count > 0 ? string.Join(", ", available) : "none") + ".");
        }

        await WriteBackAsync(c, TrackerHttp.Request(HttpMethod.Post, $"{c.SiteUrl}/rest/api/3/issue/{key}/transitions", c,
            new { transition = new { id = transitionId } }), ticketKey, ct);
        return true;
    }

    private static bool Same(string? a, string b)
        => a is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>A write-back call: the transition POST answers 204 with no body, so success
    /// is the status code alone; 403 and 404 say what they mean for a ticket.</summary>
    private async Task<string> WriteBackAsync(TrackerCredential c, HttpRequestMessage request, string ticketKey, CancellationToken ct)
    {
        var (status, body, _) = await TrackerHttp.SendAsync(http, request, Name, ct);
        return (int)status switch
        {
            >= 200 and < 300 => body,
            403 => throw new TrackerException($"Your Jira account cannot move this ticket ({ticketKey}).{Suffix(ErrorText(body))}"),
            404 => throw new TrackerException($"Ticket not found: {ticketKey} on {c.SiteUrl}."),
            _ => throw new TrackerException(DescribeError(c, status, body, $"ticket {ticketKey}")),
        };
    }

    private static bool IsKeySearch(string scope, string? search)
    {
        var text = search?.Trim();
        return !string.IsNullOrEmpty(text)
            && (text.All(char.IsAsciiDigit) || JiraQueries.LooksLikeIssueKey(text));
    }

    private Task<JsonElement> SearchAsync(TrackerCredential c, string jql, string[] fields, int maxResults,
        string? nextPageToken, CancellationToken ct)
        => PostAsync(c, "/rest/api/3/search/jql",
            nextPageToken is null
                ? new { jql, fields, maxResults }
                : (object)new { jql, fields, maxResults, nextPageToken },
            "the ticket query", ct);

    private Task<JsonElement> GetAsync(TrackerCredential c, string path, string what, CancellationToken ct)
        => SendAsync(c, TrackerHttp.Request(HttpMethod.Get, c.SiteUrl + path, c), what, ct);

    private Task<JsonElement> PostAsync(TrackerCredential c, string path, object body, string what, CancellationToken ct)
        => SendAsync(c, TrackerHttp.Request(HttpMethod.Post, c.SiteUrl + path, c, body), what, ct);

    private async Task<JsonElement> SendAsync(TrackerCredential c, HttpRequestMessage request, string what, CancellationToken ct)
    {
        var (status, body, mediaType) = await TrackerHttp.SendAsync(http, request, Name, ct);
        if ((int)status is >= 200 and < 300)
        {
            if (mediaType is not null && !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
                throw new TrackerException($"{c.SiteUrl} did not answer like a Jira Cloud site — check the site address.");
            return TrackerHttp.Parse(body, Name);
        }
        throw new TrackerException(DescribeError(c, status, body, what));
    }

    internal static string DescribeError(TrackerCredential c, HttpStatusCode status, string body, string what)
    {
        var detail = ErrorText(body);
        return status switch
        {
            HttpStatusCode.Unauthorized =>
                $"Jira rejected the email + API token for {c.SiteUrl} — the token may be revoked or expired, or belong to another email. "
                + "Create a new one at https://id.atlassian.com/manage-profile/security/api-tokens and run `./orkka tracker add jira` again.",
            HttpStatusCode.Forbidden =>
                $"Jira refused access to {what} — this account does not have permission.{Suffix(detail)}",
            HttpStatusCode.NotFound =>
                $"Jira could not find {what} on {c.SiteUrl}.{Suffix(detail)}",
            HttpStatusCode.BadRequest =>
                detail is not null ? $"Jira rejected the query: {detail}" : "Jira rejected the query.",
            HttpStatusCode.TooManyRequests =>
                "Jira is rate limiting this account — try again in a minute.",
            _ => $"Jira answered HTTP {(int)status} for {what}.{Suffix(detail)}",
        };
    }

    /// <summary>Jira's own words: <c>errorMessages</c> plus the per-field <c>errors</c>.</summary>
    internal static string? ErrorText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var messages = root.Prop("errorMessages").Items()
                .Where(m => m.ValueKind == JsonValueKind.String)
                .Select(m => m.GetString()!)
                .ToList();
            if (root.Prop("errors") is { ValueKind: JsonValueKind.Object } errors)
                messages.AddRange(errors.EnumerateObject()
                    .Where(p => p.Value.ValueKind == JsonValueKind.String)
                    .Select(p => p.Value.GetString()!));
            if (root.Str("message") is { } message)
                messages.Add(message);
            return messages.Count > 0 ? string.Join(" ", messages) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Suffix(string? detail) => detail is null ? "" : $" ({detail})";
}
