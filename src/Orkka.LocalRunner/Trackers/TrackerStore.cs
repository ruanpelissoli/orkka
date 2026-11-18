namespace Orkka.LocalRunner.Trackers;

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Orkka.LocalRunner.Contracts;

/// <summary>Where a workspace profile's files live. Only "default" exists today; workspaces
/// per job will pass their own profile name.</summary>
public static class TrackerPaths
{
    public const string DefaultProfile = "default";

    public static string WorkspaceDir(string orkkaRoot, string profile = DefaultProfile)
        => Path.Combine(orkkaRoot, "workspaces", profile);

    public static string TrackersFile(string orkkaRoot, string profile = DefaultProfile)
        => Path.Combine(WorkspaceDir(orkkaRoot, profile), "trackers.json");

    public static string SecretsDir(string orkkaRoot, string profile = DefaultProfile)
        => Path.Combine(WorkspaceDir(orkkaRoot, profile), "secrets");

    /// <summary>Cached ticket attachments: <c>tickets/{connectionId}/{ticketKey}/</c>.</summary>
    public static string TicketsDir(string orkkaRoot, string profile = DefaultProfile)
        => Path.Combine(WorkspaceDir(orkkaRoot, profile), "tickets");
}

/// <summary>One tracker connection as this machine stores it — everything except the token,
/// which is in the secret store under <see cref="Id"/>.</summary>
public sealed record TrackerConnectionEntry
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string SiteUrl { get; init; }
    /// <summary>Jira only: the Atlassian account email the API token belongs to.</summary>
    public string? Email { get; init; }
    public string? AccountId { get; init; }
    public string? DisplayName { get; init; }
    public bool Healthy { get; init; }
    public string? HealthDetail { get; init; }
    public DateTimeOffset? CheckedAt { get; init; }
    public DateTimeOffset AddedAt { get; init; }

    /// <summary>What the Hello carries: metadata only. The email stays on this machine too —
    /// the host has no use for it.</summary>
    public RunnerTrackerConnection ToMetadata() =>
        new(Id, Kind, SiteUrl, AccountId, DisplayName, Healthy, HealthDetail, CheckedAt);
}

/// <summary>
/// The non-secret tracker settings for one profile, in
/// <c>~/.orkka/workspaces/{profile}/trackers.json</c>. The CLI writes it while a running
/// runner reads it, so every write is a whole-file replace (temp + move): a reader sees the
/// old file or the new one, never half of one.
/// </summary>
public sealed class TrackerStore(string orkkaRoot, string profile = TrackerPaths.DefaultProfile)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Lock _gate = new();

    public string Profile => profile;
    public string FilePath => TrackerPaths.TrackersFile(orkkaRoot, profile);

    public IReadOnlyList<TrackerConnectionEntry> List()
    {
        lock (_gate)
            return Read();
    }

    public TrackerConnectionEntry? Get(string id)
        => List().FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds or replaces the entry with the same id.</summary>
    public void Upsert(TrackerConnectionEntry entry)
    {
        lock (_gate)
        {
            var all = Read().Where(e => !string.Equals(e.Id, entry.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            all.Add(entry);
            Write(all);
        }
    }

    /// <summary>Applies <paramref name="update"/> to the entry when it still exists — a
    /// background health check must not resurrect an entry the CLI removed meanwhile.</summary>
    public void Update(string id, Func<TrackerConnectionEntry, TrackerConnectionEntry> update)
    {
        lock (_gate)
        {
            var all = Read().ToList();
            var index = all.FindIndex(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return;
            all[index] = update(all[index]);
            Write(all);
        }
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            var all = Read().ToList();
            var removed = all.RemoveAll(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
                Write(all);
            return removed > 0;
        }
    }

    /// <summary>The existing entry for the same tracker account, so adding it twice replaces
    /// the token instead of creating a second connection.</summary>
    public TrackerConnectionEntry? FindSame(string kind, string siteUrl, string? email)
        => List().FirstOrDefault(e => e.Kind == kind
            && string.Equals(e.SiteUrl, siteUrl, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.Email ?? "", email ?? "", StringComparison.OrdinalIgnoreCase));

    /// <summary>A fresh id for a new connection: <c>{kind}-{site}</c>, suffixed when taken.</summary>
    public string NextId(string kind, string siteUrl)
        => TrackerSites.UniqueId(kind, siteUrl, List().Select(e => e.Id));

    private List<TrackerConnectionEntry> Read()
    {
        if (!File.Exists(FilePath))
            return [];
        var text = File.ReadAllText(FilePath);
        if (string.IsNullOrWhiteSpace(text))
            return [];
        var doc = JsonSerializer.Deserialize<TrackerFile>(text, Json);
        return doc?.Trackers?.Where(e => !string.IsNullOrWhiteSpace(e.Id)).ToList() ?? [];
    }

    private void Write(List<TrackerConnectionEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = $"{FilePath}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(
            new TrackerFile(1, [.. entries.OrderBy(e => e.AddedAt).ThenBy(e => e.Id, StringComparer.Ordinal)]), Json));
        File.Move(temp, FilePath, overwrite: true);
    }

    private sealed record TrackerFile(int Version, TrackerConnectionEntry[]? Trackers);
}

/// <summary>Site URL normalization and connection ids.</summary>
public static partial class TrackerSites
{
    /// <summary>
    /// Jira Cloud: <c>acme</c>, <c>acme.atlassian.net</c> or any URL on the site (a board,
    /// an issue) becomes <c>https://acme.atlassian.net</c>. A custom domain is kept as its
    /// https origin. Null when the input is not a usable site.
    /// </summary>
    public static string? NormalizeJiraSite(string? input)
    {
        if (LoopbackTestSite(input) is { } testSite)
            return testSite;
        var host = HostOf(input);
        if (host is null)
            return null;
        if (!host.Contains('.'))
            host += ".atlassian.net";
        return $"https://{host}";
    }

    /// <summary>
    /// Azure DevOps Services: <c>acme</c>, <c>https://dev.azure.com/acme</c> (with or without
    /// a project path) or the legacy <c>https://acme.visualstudio.com</c> all become
    /// <c>https://dev.azure.com/acme</c>. Null for anything else — Azure DevOps Server
    /// (on-premises) is not supported.
    /// </summary>
    public static string? NormalizeAdoOrg(string? input)
    {
        if (LoopbackTestSite(input) is { } testSite)
            return testSite;
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;

        if (OrgName().IsMatch(text))
            return $"https://dev.azure.com/{text}";

        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return null;

        var host = uri.Host.ToLowerInvariant();
        if (host == "dev.azure.com")
        {
            var org = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return org is not null && OrgName().IsMatch(Uri.UnescapeDataString(org))
                ? $"https://dev.azure.com/{Uri.UnescapeDataString(org)}"
                : null;
        }
        if (host.EndsWith(".visualstudio.com", StringComparison.Ordinal))
        {
            var org = host[..^".visualstudio.com".Length];
            return OrgName().IsMatch(org) ? $"https://dev.azure.com/{org}" : null;
        }
        return null;
    }

    /// <summary><c>jira-acme</c> for acme.atlassian.net, <c>ado-contoso</c> for
    /// dev.azure.com/contoso; <c>-2</c>, <c>-3</c>… when the id is taken.</summary>
    public static string UniqueId(string kind, string siteUrl, IEnumerable<string> taken)
    {
        var baseId = $"{kind}-{SiteSlug(kind, siteUrl)}";
        var used = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(baseId))
            return baseId;
        for (var n = 2; ; n++)
        {
            var candidate = $"{baseId}-{n}";
            if (!used.Contains(candidate))
                return candidate;
        }
    }

    internal static string SiteSlug(string kind, string siteUrl)
    {
        var name = siteUrl;
        if (Uri.TryCreate(siteUrl, UriKind.Absolute, out var uri))
        {
            name = kind == TrackerKinds.AzureDevOps
                ? uri.AbsolutePath.Trim('/')
                : uri.Host.EndsWith(".atlassian.net", StringComparison.OrdinalIgnoreCase)
                    ? uri.Host[..^".atlassian.net".Length]
                    : uri.Host;
        }
        var slug = NonSlug().Replace(name.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > 40)
            slug = slug[..40].TrimEnd('-');
        return slug.Length == 0 ? "site" : slug;
    }

    /// <summary><c>ORKKA_TRACKER_ALLOW_INSECURE_SITE=1</c> lets a test point a tracker at a fake
    /// server on this machine.</summary>
    public const string AllowInsecureSiteVariable = "ORKKA_TRACKER_ALLOW_INSECURE_SITE";

    /// <summary>
    /// With <see cref="AllowInsecureSiteVariable"/> set, a loopback URL (http or https, any port,
    /// path kept — an ADO org is the path) is taken verbatim. Loopback only: the switch can never
    /// send a real token in clear text across a network.
    /// </summary>
    private static string? LoopbackTestSite(string? input)
    {
        if (Environment.GetEnvironmentVariable(AllowInsecureSiteVariable) != "1"
            || !Uri.TryCreate(input?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !uri.IsLoopback)
            return null;
        return $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath.TrimEnd('/')}";
    }

    private static string? HostOf(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http") || string.IsNullOrEmpty(uri.Host))
            return null;
        return uri.Host.ToLowerInvariant();
    }

    // Azure DevOps organization names: letters, digits, hyphens; no leading/trailing hyphen.
    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,48}[A-Za-z0-9])?$")]
    private static partial Regex OrgName();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();
}
