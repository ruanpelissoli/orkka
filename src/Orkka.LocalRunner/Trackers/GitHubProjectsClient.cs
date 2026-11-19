namespace Orkka.LocalRunner.Trackers;

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Orkka.LocalRunner.Contracts;

/// <summary>
/// Pure helpers for GitHub Projects: ticket keys (<c>owner/repo#12</c>), status names and their
/// category, and the intake rule. Testable on their own.
/// </summary>
public static partial class GitHubProjects
{
    public const string DefaultStatusField = "Status";

    /// <summary><c>acme/shop#12</c>.</summary>
    public static string Key(string nameWithOwner, int number) => $"{nameWithOwner}#{number}";

    public static bool TryParseKey(string? key, out string owner, out string repo, out int number)
    {
        owner = repo = "";
        number = 0;
        var m = KeyPattern().Match(key?.Trim() ?? "");
        if (!m.Success || !int.TryParse(m.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out number))
            return false;
        owner = m.Groups["owner"].Value;
        repo = m.Groups["repo"].Value;
        return true;
    }

    /// <summary>"To Do", "Todo" and "todo" are one status: case and spaces do not count — a
    /// GitHub project says "Todo" where Jira says "To Do".</summary>
    public static bool SameStatus(string? a, string? b)
        => a is not null && b is not null
            && string.Equals(Squash(a), Squash(b), StringComparison.OrdinalIgnoreCase);

    private static string Squash(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c)));

    /// <summary>
    /// "todo", "inprogress" or "done", for the not-started rule and retirement: a closed issue is
    /// done; no status, an intake status or a to-do-like name (Todo, Ready, Backlog, New, Triage)
    /// is todo; a done-like name (Done, Closed, Complete, Shipped, Released) is done; anything
    /// else is under way.
    /// </summary>
    public static string Category(string? status, bool closed, IReadOnlyCollection<string>? intakeStatuses = null)
    {
        if (closed)
            return "done";
        if (string.IsNullOrWhiteSpace(status) || SameStatus(status, "No Status"))
            return "todo";
        if (intakeStatuses is not null && intakeStatuses.Any(s => SameStatus(s, status)))
            return "todo";
        if (DoneName().IsMatch(status))
            return "done";
        return TodoName().IsMatch(status) ? "todo" : "inprogress";
    }

    /// <summary>The statuses an issue must be in to be picked up: the configured ones, or — none
    /// set — the to-do-like ones.</summary>
    public static bool IsIntakeStatus(string? status, IReadOnlyCollection<string>? intakeStatuses)
    {
        var configured = (intakeStatuses ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (configured.Count > 0)
            return status is not null && configured.Any(s => SameStatus(s, status));
        return status is not null && Category(status, closed: false) == "todo" && !SameStatus(status, "No Status");
    }

    /// <summary>The repository filter: empty = every repository on the project.</summary>
    public static bool InRepoFilter(string nameWithOwner, IReadOnlyCollection<string>? repoFilter)
        => repoFilter is not { Count: > 0 }
            || repoFilter.Any(r => string.Equals(r.Trim(), nameWithOwner, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"^(?<owner>[A-Za-z0-9](?:[A-Za-z0-9-]{0,38}))/(?<repo>[A-Za-z0-9._-]{1,100})#(?<n>[0-9]{1,9})$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"\b(to\s*do|ready|backlog|new|triage|open|icebox|planned)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TodoName();

    [GeneratedRegex(@"\b(done|closed|complete|completed|shipped|released|resolved|merged)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DoneName();
}

/// <summary>
/// GitHub Projects (v2) as a ticket source, through GitHub's GraphQL API
/// (<c>https://api.github.com/graphql</c>, <c>https://api.{sub}.ghe.com/graphql</c>):
/// <list type="bullet">
/// <item>a scope is an owner (the developer or one of their organizations) and a board is one of
/// its projects — the source is always one project;</item>
/// <item>statuses are the options of the project's single-select status field ("Status" unless
/// the source names another), and a status move sets that field
/// (<c>updateProjectV2ItemFieldValue</c>);</item>
/// <item>tickets are the project's ISSUE items — draft issues (no repository, no assignee, no
/// sub-issues; nothing to branch from) and pull requests (already code) are skipped;</item>
/// <item>an issue with sub-issues is a Story whose members are its sub-issues, in GitHub's order.</item>
/// </list>
/// Every read is complete or fails: a project bigger than the paging cap throws rather than
/// report a partial set. Rate limits: a secondary limit with a short <c>Retry-After</c> is waited
/// out once; anything longer fails the call with the wait in the message. SAML SSO refusals say
/// to authorize the token (or re-run <c>gh auth login</c>).
/// </summary>
public sealed partial class GitHubProjectsClient(HttpClient http, TimeProvider? timeProvider = null)
    : ITrackerClient, ITrackerTicketSource, ITrackerStatusWriter
{
    private const string Name = "GitHub";
    internal const int ItemsPageSize = 50;
    internal const int MaxItemPages = 100;
    private const int MaxOwnerPages = 10;
    private const int MaxProjectPages = 10;
    internal const int KeyBatchSize = 25;
    internal static readonly TimeSpan MaxRetryWait = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    // Issue fields every read needs. Sub-issues come in GitHub's own (priority) order.
    private const string IssueFields = """
        fragment IssueFields on Issue {
          id number title url body state updatedAt
          repository { nameWithOwner url }
          assignees(first: 20) { nodes { login } }
          labels(first: 30) { nodes { name } }
          issueType { name }
          parent { number repository { nameWithOwner } }
          subIssues(first: 50) { nodes { number repository { nameWithOwner } } }
        }
        """;

    // ── Settings queries ────────────────────────────────────────────────────

    public async Task<TrackerIdentity> TestAsync(TrackerCredential c, CancellationToken ct)
    {
        var (data, headers) = await GraphQLWithHeadersAsync(c, "query { viewer { login name } }", null, "your account", ct);
        CheckScopes(c, headers);
        var viewer = data.Prop("viewer");
        var login = viewer.Str("login") ?? throw new TrackerException("GitHub did not say who this token belongs to.");
        return new TrackerIdentity(login, viewer.Str("name") is { Length: > 0 } name ? $"{name} ({login})" : login);
    }

    /// <summary>The owners whose projects the source can pick: the developer, then their
    /// organizations.</summary>
    public async Task<TrackerScope[]> ScopesAsync(TrackerCredential c, CancellationToken ct)
    {
        var scopes = new List<TrackerScope>();
        string? after = null;
        for (var page = 0; page < MaxOwnerPages; page++)
        {
            var data = await GraphQLAsync(c, """
                query($after: String) {
                  viewer { login name organizations(first: 100, after: $after) {
                    pageInfo { hasNextPage endCursor } nodes { login name } } }
                }
                """, new { after }, "your organizations", ct);
            var viewer = data.Prop("viewer");
            if (page == 0 && viewer.Str("login") is { } login)
                scopes.Add(new TrackerScope(login, viewer.Str("name") is { Length: > 0 } name ? $"{name} (you)" : $"{login} (you)"));
            var orgs = viewer.Prop("organizations");
            foreach (var o in orgs.Prop("nodes").Items())
            {
                if (o.Str("login") is { } orgLogin)
                    scopes.Add(new TrackerScope(orgLogin, o.Str("name") is { Length: > 0 } orgName ? orgName : orgLogin));
            }
            if (!HasNext(orgs, out after))
                break;
        }
        return [.. scopes.DistinctBy(s => s.Key, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>GitHub has no epics: sub-issues make a Story, and intake is by status and
    /// repository.</summary>
    public Task<TrackerEpic[]> EpicsAsync(TrackerCredential c, string scope, string? search, CancellationToken ct)
        => Task.FromResult<TrackerEpic[]>([]);

    /// <summary>The owner's open projects, by title.</summary>
    public async Task<TrackerBoard[]> BoardsAsync(TrackerCredential c, string scope, CancellationToken ct)
    {
        var boards = new List<TrackerBoard>();
        string? after = null;
        for (var page = 0; page < MaxProjectPages; page++)
        {
            var data = await GraphQLAsync(c, """
                query($login: String!, $after: String) {
                  repositoryOwner(login: $login) {
                    ... on ProjectV2Owner { projectsV2(first: 100, after: $after) {
                      pageInfo { hasNextPage endCursor } nodes { id number title closed url } } }
                  }
                }
                """, new { login = scope, after }, $"the projects of {scope}", ct);
            var owner = data.Prop("repositoryOwner");
            if (owner.ValueKind != JsonValueKind.Object)
                throw new TrackerException($"GitHub has no user or organization '{scope}' that this account can see.");
            var projects = owner.Prop("projectsV2");
            foreach (var p in projects.Prop("nodes").Items())
            {
                if (p.Prop("closed").ValueKind == JsonValueKind.True || p.Str("id") is not { } id)
                    continue;
                boards.Add(new TrackerBoard(id, p.Str("title") ?? id, null, IntOrNull(p.Prop("number")), p.Str("url")));
            }
            if (!HasNext(projects, out after))
                break;
        }
        return [.. boards.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<TrackerStatus[]> StatusesAsync(TrackerCredential c, string scope, CancellationToken ct,
        TrackerBoardRef? board = null, string? statusField = null)
    {
        var project = await ProjectAsync(c, scope, board, ct);
        var field = project.StatusField(statusField);
        return [.. field.Options.Select(o => new TrackerStatus(o.Name, GitHubProjects.Category(o.Name, closed: false)))];
    }

    /// <summary>The project's single-select fields — which one holds the status.</summary>
    public async Task<TrackerField[]> FieldsAsync(TrackerCredential c, string scope, TrackerBoardRef? board, CancellationToken ct)
    {
        var project = await ProjectAsync(c, scope, board, ct);
        return [.. project.Fields.Select(f => new TrackerField(f.Id, f.Name, [.. f.Options.Select(o => o.Name)]))];
    }

    /// <summary>The repositories whose issues are on the project, for the repository filter.</summary>
    public async Task<string[]> RepositoriesAsync(TrackerCredential c, string scope, TrackerBoardRef? board, CancellationToken ct)
    {
        var project = await ProjectAsync(c, scope, board, ct);
        var items = await ItemsAsync(c, project, GitHubProjects.DefaultStatusField, ct);
        return [.. items.Select(i => i.Issue.Repository).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<TrackerPreview> PreviewAsync(TrackerCredential c, string scope, string[] epicKeys,
        string? extraClause, bool includeDescendants, string[]? intakeStatuses, string? scopeMode, CancellationToken ct,
        TrackerBoardRef? board = null, string? statusField = null, string[]? repoFilter = null)
    {
        var project = await ProjectAsync(c, scope, board, ct);
        var field = project.StatusField(statusField);
        var items = await ItemsAsync(c, project, field.Name, ct);
        var intake = items.Where(i => i.InIntake(intakeStatuses, repoFilter)).ToList();
        return new TrackerPreview(intake.Count,
            [.. intake.Take(5).Select(i => new TrackerTicketPreview(i.Issue.Key, i.Issue.Title, i.Issue.Type, i.Status, i.Issue.Url))],
            DescribeIntake(project.Title, field.Name, intakeStatuses, repoFilter));
    }

    /// <summary>What the settings page shows as "the query": GitHub has no query language for
    /// project items, so it is a sentence.</summary>
    internal static string DescribeIntake(string projectTitle, string field, string[]? intakeStatuses, string[]? repoFilter)
    {
        var statuses = (intakeStatuses ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        var sb = new StringBuilder($"project:\"{projectTitle}\" is:issue is:open assignee:@me");
        sb.Append(statuses.Count > 0
            ? $" {field.ToLowerInvariant()}:{string.Join(",", statuses.Select(s => $"\"{s}\""))}"
            : $" {field.ToLowerInvariant()}:<to do>");
        if (repoFilter is { Length: > 0 })
            sb.Append(" repo:").Append(string.Join(",", repoFilter));
        return sb.ToString();
    }

    // ── Polling ─────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<RawTicket>> FetchTicketsAsync(
        TrackerCredential c, TrackerPollAssignment assignment, CancellationToken ct)
    {
        var board = TrackerBoardRef.From(assignment.BoardId, assignment.BoardName)
            ?? throw new TrackerException("This ticket source has no GitHub project — pick one in the ticket source settings.");
        var project = await ProjectAsync(c, assignment.Scope, board, ct);
        var field = project.StatusField(assignment.StatusField);
        var intakeStatuses = assignment.IntakeStatuses ?? [];
        var items = await ItemsAsync(c, project, field.Name, ct);
        var viewer = items.Viewer;

        var tickets = new Dictionary<string, RawTicket>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.Where(i => i.InIntake(intakeStatuses, assignment.RepoFilter)))
            tickets[item.Issue.Key] = ToRaw(item.Issue, item.Status, viewer, intakeStatuses, inIntake: true);

        // Tracked keys and the intake tickets' sub-issues, whatever their status or project.
        var wanted = assignment.Known.Select(k => k.Key)
            .Concat(tickets.Values.SelectMany(t => t.ChildKeys))
            .Where(k => !tickets.ContainsKey(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var issue in await IssuesByKeyAsync(c, wanted, project.Id, field.Name, ct))
        {
            var inIntake = issue.Item is { } item && item.InIntake(intakeStatuses, assignment.RepoFilter);
            tickets[issue.Issue.Key] = ToRaw(issue.Issue, issue.Item?.Status, viewer, intakeStatuses, inIntake);
        }
        return [.. tickets.Values];
    }

    public async Task<IReadOnlyList<RawComment>> FetchCommentsAsync(TrackerCredential c, RawTicket ticket,
        DateTimeOffset? after, int max, CancellationToken ct)
    {
        var data = await GraphQLAsync(c, """
            query($id: ID!, $n: Int!) {
              node(id: $id) { ... on Issue { comments(last: $n) { nodes { id author { login } createdAt body } } } }
            }
            """, new { id = ticket.Id, n = Math.Clamp(max, 1, 100) }, $"the comments of {ticket.Key}", ct);
        return [.. data.Prop("node").Prop("comments").Prop("nodes").Items()
            .Select(n => (Node: n, At: DateOrNull(n.Str("createdAt"))))
            .Where(x => x.At is { } at && (after is null || at > after) && x.Node.Str("id") is not null)
            .OrderBy(x => x.At)
            .TakeLast(max)
            .Select(x => new RawComment(x.Node.Str("id")!, x.Node.Prop("author").Str("login") ?? "ghost", x.At!.Value,
                x.Node.Str("body") ?? "") { Markdown = true })];
    }

    private static RawTicket ToRaw(Issue issue, string? status, string viewer, IReadOnlyCollection<string> intakeStatuses, bool inIntake)
        => new(issue.Key, issue.Id, issue.Url, null, issue.Title, issue.Type, status,
            GitHubProjects.Category(status, issue.Closed, intakeStatuses), null,
            issue.Body, null, null, issue.Labels, [], issue.UpdatedAt)
        {
            ParentKey = issue.ParentKey,
            ParentType = issue.ParentKey is null ? null : "Issue",
            ChildKeys = issue.ChildKeys,
            AssignedToMe = issue.Assignees.Contains(viewer, StringComparer.OrdinalIgnoreCase),
            InIntake = inIntake && !issue.Closed && issue.Assignees.Contains(viewer, StringComparer.OrdinalIgnoreCase),
            Repository = issue.RepositoryUrl,
            Markdown = true,
        };

    // ── Status write-back ───────────────────────────────────────────────────

    public Task<bool> TransitionAsync(TrackerCredential c, string ticketKey, string ticketId, string targetStatus, CancellationToken ct)
        => throw new TrackerException($"Orkka did not say which GitHub project to move {ticketKey} on — update Orkka.");

    /// <summary>
    /// Sets the issue's status field on the source's project to the option named
    /// <see cref="TrackerTransition.TargetStatus"/>. A sub-issue that is not on the project yet is
    /// added to it first (that is where its status lives). Already there = false, nothing written.
    /// </summary>
    public async Task<bool> TransitionAsync(TrackerCredential c, TrackerTransition t, CancellationToken ct)
    {
        var board = TrackerBoardRef.From(t.BoardId, null)
            ?? throw new TrackerException($"Orkka did not say which GitHub project to move {t.TicketKey} on — update Orkka.");
        var project = await ProjectAsync(c, "", board, ct);
        var field = project.StatusField(t.StatusField);
        var option = field.Options.FirstOrDefault(o => GitHubProjects.SameStatus(o.Name, t.TargetStatus))
            ?? throw new TrackerException(
                $"The project's {field.Name} field has no option '{t.TargetStatus}' — available: {string.Join(", ", field.Options.Select(o => o.Name))}.");

        var data = await GraphQLAsync(c, """
            query($id: ID!, $field: String!) {
              node(id: $id) { ... on Issue { projectItems(first: 50, includeArchived: true) { nodes {
                id project { id }
                fieldValueByName(name: $field) { ... on ProjectV2ItemFieldSingleSelectValue { optionId } } } } } }
            }
            """, new { id = t.TicketId, field = field.Name }, t.TicketKey, ct, allowNotFound: true);
        var node = data.Prop("node");
        if (node.ValueKind != JsonValueKind.Object)
            throw new TrackerException($"Issue not found: {t.TicketKey} (deleted, transferred, or no longer visible to this account).");
        var item = node.Prop("projectItems").Prop("nodes").Items()
            .FirstOrDefault(i => i.Prop("project").Str("id") == project.Id);
        string itemId;
        if (item.ValueKind == JsonValueKind.Object && item.Str("id") is { } existing)
        {
            if (item.Prop("fieldValueByName").Str("optionId") == option.Id)
                return false;
            itemId = existing;
        }
        else
        {
            var added = await GraphQLAsync(c, """
                mutation($project: ID!, $content: ID!) {
                  addProjectV2ItemById(input: { projectId: $project, contentId: $content }) { item { id } }
                }
                """, new { project = project.Id, content = t.TicketId }, t.TicketKey, ct);
            itemId = added.Prop("addProjectV2ItemById").Prop("item").Str("id")
                ?? throw new TrackerException($"GitHub did not add {t.TicketKey} to the project '{project.Title}'.");
        }

        await GraphQLAsync(c, """
            mutation($project: ID!, $item: ID!, $field: ID!, $option: String!) {
              updateProjectV2ItemFieldValue(input: { projectId: $project, itemId: $item, fieldId: $field,
                value: { singleSelectOptionId: $option } }) { projectV2Item { id } }
            }
            """, new { project = project.Id, item = itemId, field = field.Id, option = option.Id }, t.TicketKey, ct);
        return true;
    }

    // ── Reads ───────────────────────────────────────────────────────────────

    internal sealed record Option(string Id, string Name);

    internal sealed record SingleSelect(string Id, string Name, Option[] Options);

    internal sealed record Project(string Id, string Title, SingleSelect[] Fields)
    {
        public SingleSelect StatusField(string? name)
        {
            var wanted = string.IsNullOrWhiteSpace(name) ? GitHubProjects.DefaultStatusField : name.Trim();
            return Fields.FirstOrDefault(f => string.Equals(f.Name, wanted, StringComparison.OrdinalIgnoreCase))
                ?? throw new TrackerException(
                    $"The project '{Title}' has no single-select field '{wanted}' — pick the field that holds the status in the ticket source settings"
                    + (Fields.Length > 0 ? $" ({string.Join(", ", Fields.Select(f => f.Name))})." : "."));
        }
    }

    internal sealed record Issue(
        string Id, string Key, string Repository, string RepositoryUrl, string Title, string Url, string? Body,
        bool Closed, DateTimeOffset? UpdatedAt, string[] Assignees, string[] Labels, string? Type,
        string? ParentKey, string[] ChildKeys);

    internal sealed record Item(string Id, string? Status, Issue Issue, string Viewer)
    {
        public bool InIntake(IReadOnlyCollection<string>? intakeStatuses, IReadOnlyCollection<string>? repoFilter)
            => !Issue.Closed
                && Issue.Assignees.Contains(Viewer, StringComparer.OrdinalIgnoreCase)
                && GitHubProjects.IsIntakeStatus(Status, intakeStatuses)
                && GitHubProjects.InRepoFilter(Issue.Repository, repoFilter);
    }

    internal sealed class Items(string viewer) : List<Item>
    {
        public string Viewer { get; } = viewer;
    }

    internal sealed record KeyedIssue(Issue Issue, Item? Item);

    private async Task<Project> ProjectAsync(TrackerCredential c, string scope, TrackerBoardRef? board, CancellationToken ct)
    {
        if (board is null)
            throw new TrackerException("Pick a GitHub project first.");
        var data = await GraphQLAsync(c, """
            query($id: ID!) {
              node(id: $id) { ... on ProjectV2 { id title closed fields(first: 50) { nodes {
                ... on ProjectV2SingleSelectField { id name options { id name } } } } } }
            }
            """, new { id = board.Id }, $"the project '{board.Label}'", ct, allowNotFound: true);
        var node = data.Prop("node");
        if (node.Str("id") is not { } id)
            throw board.NotFound(string.IsNullOrWhiteSpace(scope) ? "GitHub" : scope);
        var fields = node.Prop("fields").Prop("nodes").Items()
            .Where(f => f.Str("id") is not null && f.Str("name") is not null && f.Prop("options").ValueKind == JsonValueKind.Array)
            .Select(f => new SingleSelect(f.Str("id")!, f.Str("name")!,
                [.. f.Prop("options").Items().Where(o => o.Str("id") is not null)
                    .Select(o => new Option(o.Str("id")!, o.Str("name") ?? ""))]))
            .ToArray();
        return new Project(id, node.Str("title") ?? board.Label, fields);
    }

    /// <summary>Every issue item of the project (draft issues and pull requests skipped), all
    /// pages — or a failure, never part of them.</summary>
    private async Task<Items> ItemsAsync(TrackerCredential c, Project project, string field, CancellationToken ct)
    {
        Items? items = null;
        string? after = null;
        for (var page = 0; ; page++)
        {
            if (page >= MaxItemPages)
                throw new TrackerException(
                    $"The project '{project.Title}' has more than {(MaxItemPages * ItemsPageSize).ToString("N0", CultureInfo.InvariantCulture)} items — Orkka reads the whole project on every poll. "
                    + "Archive finished items, or point the source at a smaller project.");
            var data = await GraphQLAsync(c, $$"""
                query($id: ID!, $after: String, $field: String!) {
                  viewer { login }
                  node(id: $id) { ... on ProjectV2 { items(first: {{ItemsPageSize}}, after: $after) {
                    pageInfo { hasNextPage endCursor }
                    nodes { id type isArchived
                      fieldValueByName(name: $field) { ... on ProjectV2ItemFieldSingleSelectValue { name } }
                      content { ... on Issue { ...IssueFields } } } } } }
                }
                {{IssueFields}}
                """, new { id = project.Id, after, field }, $"the items of '{project.Title}'", ct);
            items ??= new Items(data.Prop("viewer").Str("login") ?? throw new TrackerException("GitHub did not say who this token belongs to."));
            var connection = data.Prop("node").Prop("items");
            foreach (var n in connection.Prop("nodes").Items())
            {
                // ISSUE only: a DRAFT_ISSUE has no repository to branch from, a PULL_REQUEST is
                // already code, REDACTED is something this account cannot see.
                if (n.Str("type") != "ISSUE" || n.Prop("isArchived").ValueKind == JsonValueKind.True
                    || ParseIssue(n.Prop("content")) is not { } issue)
                    continue;
                items.Add(new Item(n.Str("id") ?? "", n.Prop("fieldValueByName").Str("name"), issue, items.Viewer));
            }
            if (!HasNext(connection, out after))
                return items;
        }
    }

    /// <summary>Issues read by key, any status, on the project or not; a key GitHub does not know
    /// (deleted, transferred, hidden) is simply absent.</summary>
    private async Task<List<KeyedIssue>> IssuesByKeyAsync(TrackerCredential c, IReadOnlyList<string> keys,
        string projectId, string field, CancellationToken ct)
    {
        var result = new List<KeyedIssue>();
        var parsed = keys
            .Select(k => GitHubProjects.TryParseKey(k, out var owner, out var repo, out var number) ? (owner, repo, number) : default)
            .Where(k => k.owner is { Length: > 0 })
            .ToList();
        foreach (var batch in parsed.Chunk(KeyBatchSize))
        {
            var q = new StringBuilder("query($field: String!) {\n  viewer { login }\n");
            for (var i = 0; i < batch.Length; i++)
            {
                // Owner and repository names were checked against GitHub's own character set by
                // TryParseKey, so they sit safely in a string literal.
                q.Append(CultureInfo.InvariantCulture, $"  i{i}: repository(owner: \"{batch[i].owner}\", name: \"{batch[i].repo}\") {{ issue(number: {batch[i].number}) {{ ...IssueFields projectItems(first: 20, includeArchived: false) {{ nodes {{ id project {{ id }} fieldValueByName(name: $field) {{ ... on ProjectV2ItemFieldSingleSelectValue {{ name }} }} }} }} }} }}\n");
            }
            q.Append("}\n").Append(IssueFields);
            var data = await GraphQLAsync(c, q.ToString(), new { field }, "the tracked issues", ct, allowNotFound: true);
            var viewer = data.Prop("viewer").Str("login") ?? "";
            for (var i = 0; i < batch.Length; i++)
            {
                var node = data.Prop($"i{i}").Prop("issue");
                if (ParseIssue(node) is not { } issue)
                    continue;
                var projectItem = node.Prop("projectItems").Prop("nodes").Items()
                    .FirstOrDefault(n => n.Prop("project").Str("id") == projectId);
                var item = projectItem.ValueKind == JsonValueKind.Object
                    ? new Item(projectItem.Str("id") ?? "", projectItem.Prop("fieldValueByName").Str("name"), issue, viewer)
                    : null;
                result.Add(new KeyedIssue(issue, item));
            }
        }
        return result;
    }

    internal static Issue? ParseIssue(JsonElement n)
    {
        if (n.ValueKind != JsonValueKind.Object || n.Str("id") is not { } id
            || n.Prop("repository").Str("nameWithOwner") is not { } repo
            || IntOrNull(n.Prop("number")) is not { } number)
            return null;
        var labels = n.Prop("labels").Prop("nodes").Items().Select(l => l.Str("name")).OfType<string>().ToArray();
        var parent = n.Prop("parent");
        var parentKey = parent.Prop("repository").Str("nameWithOwner") is { } parentRepo && IntOrNull(parent.Prop("number")) is { } parentNumber
            ? GitHubProjects.Key(parentRepo, parentNumber)
            : null;
        string[] children = [.. n.Prop("subIssues").Prop("nodes").Items()
            .Select(s => s.Prop("repository").Str("nameWithOwner") is { } r && IntOrNull(s.Prop("number")) is { } num
                ? GitHubProjects.Key(r, num) : null)
            .OfType<string>()];
        // GitHub's issue type when the organization uses them, else a "bug" label.
        var type = n.Prop("issueType").Str("name")
            ?? (labels.Any(l => l.Contains("bug", StringComparison.OrdinalIgnoreCase)) ? "Bug" : null);
        return new Issue(id, GitHubProjects.Key(repo, number), repo,
            n.Prop("repository").Str("url") ?? "", n.Str("title") ?? "", n.Str("url") ?? "", n.Str("body"),
            string.Equals(n.Str("state"), "CLOSED", StringComparison.OrdinalIgnoreCase),
            DateOrNull(n.Str("updatedAt")),
            [.. n.Prop("assignees").Prop("nodes").Items().Select(a => a.Str("login")).OfType<string>()],
            labels, type, parentKey, children);
    }

    // ── GraphQL plumbing ────────────────────────────────────────────────────

    private async Task<JsonElement> GraphQLAsync(TrackerCredential c, string query, object? variables, string what,
        CancellationToken ct, bool allowNotFound = false)
        => (await GraphQLWithHeadersAsync(c, query, variables, what, ct, allowNotFound)).Data;

    private async Task<(JsonElement Data, HttpResponseHeaders Headers)> GraphQLWithHeadersAsync(
        TrackerCredential c, string query, object? variables, string what, CancellationToken ct, bool allowNotFound = false)
    {
        var url = $"{TrackerSites.GitHubApiBase(c.SiteUrl)}/graphql";
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = c.Authorization();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("orkka");
            // Sub-issues and issue types were previews on some instances; harmless where GA.
            request.Headers.TryAddWithoutValidation("GraphQL-Features", "sub_issues,issue_types");
            request.Content = new StringContent(JsonSerializer.Serialize(new { query, variables }, TrackerHttp.Json),
                Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new TrackerException($"Could not reach GitHub at {request.RequestUri?.GetLeftPart(UriPartial.Authority)}: {ex.Message}");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TrackerException($"GitHub did not answer in time ({request.RequestUri?.GetLeftPart(UriPartial.Authority)}).");
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                var status = (int)response.StatusCode;
                if (RateLimitWait(response, body) is { } wait)
                {
                    if (attempt == 0 && wait <= MaxRetryWait)
                    {
                        await Task.Delay(wait, _time, ct);
                        continue;
                    }
                    throw new TrackerException(RateLimited(wait));
                }
                if (status is < 200 or >= 300)
                    throw new TrackerException(DescribeHttpError(c, response.StatusCode, response.Headers, body, what));

                var root = TrackerHttp.Parse(body, Name);
                var errors = root.Prop("errors").Items().ToList();
                var blocking = errors.Where(e => !(allowNotFound && e.Str("type") == "NOT_FOUND")).ToList();
                if (blocking.Count > 0)
                    throw new TrackerException(DescribeGraphQLErrors(c, blocking, what));
                var data = root.Prop("data");
                if (data.ValueKind != JsonValueKind.Object)
                    throw new TrackerException($"GitHub answered without data for {what}.");
                return (data, response.Headers);
            }
        }
    }

    /// <summary>How long GitHub asks to wait, when it is rate limiting: a secondary limit's
    /// <c>Retry-After</c>, or a spent primary limit's <c>x-ratelimit-reset</c>.</summary>
    private TimeSpan? RateLimitWait(HttpResponseMessage response, string body)
    {
        var status = (int)response.StatusCode;
        if (status is not (403 or 429) && !(status == 200 && body.Contains("\"RATE_LIMITED\"", StringComparison.Ordinal)))
            return null;
        if (response.Headers.RetryAfter is { } retry)
        {
            if (retry.Delta is { } delta)
                return delta;
            if (retry.Date is { } date)
                return date - _time.GetUtcNow() is var d && d > TimeSpan.Zero ? d : TimeSpan.Zero;
        }
        if (Header(response.Headers, "x-ratelimit-remaining") == "0"
            && long.TryParse(Header(response.Headers, "x-ratelimit-reset"), NumberStyles.None, CultureInfo.InvariantCulture, out var reset))
        {
            var d = DateTimeOffset.FromUnixTimeSeconds(reset) - _time.GetUtcNow();
            return d > TimeSpan.Zero ? d : TimeSpan.Zero;
        }
        if (status == 429 || body.Contains("rate limit", StringComparison.OrdinalIgnoreCase) || body.Contains("\"RATE_LIMITED\"", StringComparison.Ordinal))
            return TimeSpan.FromMinutes(1);
        return null;
    }

    internal static string RateLimited(TimeSpan wait)
        => wait.TotalSeconds < 90
            ? $"GitHub is rate limiting this account — Orkka tries again in about {Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds))} seconds."
            : $"GitHub is rate limiting this account — Orkka tries again in about {(int)Math.Ceiling(wait.TotalMinutes)} minutes.";

    internal static string DescribeHttpError(TrackerCredential c, HttpStatusCode status, HttpResponseHeaders headers, string body, string what)
    {
        var detail = ErrorMessage(body);
        if (Header(headers, "x-github-sso") is not null || LooksLikeSso(detail))
            return Sso(c);
        return status switch
        {
            HttpStatusCode.Unauthorized => c.GhCliHost is { } host
                ? $"GitHub rejected your gh login for {host} — run `gh auth login -h {host}` again."
                : $"GitHub rejected the token for {Host(c)} — it may be revoked or expired. Create a new one and add the connection again (`./orkka tracker add github --token`).",
            HttpStatusCode.Forbidden => $"GitHub refused access to {what}.{Suffix(detail)}{ScopeHint(c)}",
            HttpStatusCode.NotFound => $"GitHub could not find {what} on {Host(c)}.{Suffix(detail)}",
            _ => $"GitHub answered HTTP {(int)status} for {what}.{Suffix(detail)}",
        };
    }

    internal static string DescribeGraphQLErrors(TrackerCredential c, IReadOnlyList<JsonElement> errors, string what)
    {
        var messages = errors.Select(e => e.Str("message")).OfType<string>().Distinct().ToList();
        var text = string.Join(" ", messages);
        if (LooksLikeSso(text))
            return Sso(c);
        if (errors.Any(e => e.Str("type") == "INSUFFICIENT_SCOPES"))
            return MissingScope(c, "project");
        if (errors.Any(e => e.Str("type") == "FORBIDDEN"))
            return $"GitHub refused access to {what}.{Suffix(text)}{ScopeHint(c)}";
        if (errors.Any(e => e.Str("type") == "NOT_FOUND"))
            return $"GitHub could not find {what}.{Suffix(text)}";
        return $"GitHub rejected the request for {what}.{Suffix(text)}";
    }

    /// <summary>A classic token or a gh login lists its scopes in <c>X-OAuth-Scopes</c>; a
    /// fine-grained token does not (its permissions show up as refusals instead).</summary>
    private static void CheckScopes(TrackerCredential c, HttpResponseHeaders headers)
    {
        if (Header(headers, "x-oauth-scopes") is not { } raw)
            return;
        var scopes = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!scopes.Contains("project"))
            throw new TrackerException(MissingScope(c, "project"));
        if (!scopes.Contains("repo"))
            throw new TrackerException(MissingScope(c, "repo"));
    }

    internal static string MissingScope(TrackerCredential c, string scope)
        => c.GhCliHost is { } host
            ? $"Your gh login lacks the '{scope}' scope Orkka needs to read and move project items — run `gh auth refresh -h {host} -s {scope}`."
            : $"The token lacks the '{scope}' scope — create one with the project and repo scopes (classic), or with Projects and Issues read & write on the organization (fine-grained), and add the connection again.";

    private static string ScopeHint(TrackerCredential c)
        => c.GhCliHost is { } host
            ? $" If this is a scope problem, run `gh auth refresh -h {host} -s project`."
            : " Check the token's scopes (project and repo) or, for a fine-grained token, its Projects and Issues permissions on the organization.";

    internal static string Sso(TrackerCredential c)
        => c.GhCliHost is { } host
            ? $"GitHub refused access: the organization enforces SAML single sign-on and your gh login is not authorized for it — run `gh auth login -h {host}` again and authorize the organization when the browser asks."
            : "GitHub refused access: the organization enforces SAML single sign-on and this token is not authorized for it — authorize it for the organization (GitHub → Settings → Developer settings → Personal access tokens → Configure SSO), then try again.";

    private static bool LooksLikeSso(string? text)
        => text is not null && (text.Contains("SAML", StringComparison.OrdinalIgnoreCase)
            || text.Contains("single sign-on", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(text, @"\bSSO\b"));

    private static string? ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.Str("message") is { } message)
                return message;
            var errors = root.Prop("errors").Items().Select(e => e.Str("message")).OfType<string>().ToList();
            return errors.Count > 0 ? string.Join(" ", errors) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Header(HttpResponseHeaders headers, string name)
        => headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static string Host(TrackerCredential c)
        => Uri.TryCreate(c.SiteUrl, UriKind.Absolute, out var uri) ? uri.Authority : c.SiteUrl;

    private static string Suffix(string? detail) => string.IsNullOrWhiteSpace(detail) ? "" : $" ({detail})";

    private static bool HasNext(JsonElement connection, out string? after)
    {
        var info = connection.Prop("pageInfo");
        after = info.Str("endCursor");
        return info.Prop("hasNextPage").ValueKind == JsonValueKind.True && after is not null;
    }

    private static int? IntOrNull(JsonElement e)
        => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v) ? v : null;

    private static DateTimeOffset? DateOrNull(string? s)
        => DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : null;
}
