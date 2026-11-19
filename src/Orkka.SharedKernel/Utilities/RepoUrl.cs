namespace Orkka.SharedKernel.Utilities;

/// <summary>Where a repository's working copy lives, as far as Orkka is concerned.</summary>
public enum RepoLocation
{
    /// <summary>A git remote (https, ssh, scp-style) cloned with the developer's own credentials.</summary>
    Remote,

    /// <summary>A folder on the developer's machine; it exists on exactly one computer.</summary>
    LocalFolder,
}

/// <summary>The forge a remote lives on — what decides which CLI opens a pull request.</summary>
public enum ForgeKind
{
    Other,
    GitHub,
    AzureDevOps,
    GitLab,
    Bitbucket,
}

/// <summary>
/// A remote's identity on its forge. <see cref="Owner"/> is the GitHub/Bitbucket owner, the
/// GitLab namespace (subgroups included, "group/sub"), or the Azure DevOps organization;
/// <see cref="Project"/> is set only for Azure DevOps, whose repos live inside a project.
/// </summary>
public sealed record ForgeRepoRef(string Host, string Owner, string Name, string? Project = null)
{
    /// <summary>"owner/name" — what <c>gh</c> and <c>glab</c> take as <c>--repo</c>.</summary>
    public string Slug => $"{Owner}/{Name}";
}

/// <summary>
/// Reads a repository URL the way a developer types it — https, ssh://, scp-style
/// (<c>git@host:org/repo.git</c>, including host aliases from ~/.ssh/config such as
/// <c>git@github-work:org/repo.git</c>), or a folder path — and answers where it lives and
/// which forge serves it. Pure string work: nothing here touches the network or the disk.
///
/// The one rule that matters most: an ssh remote is NOT a folder. Treating every non-http URL
/// as a local path sent ssh remotes down the local-folder code path.
/// </summary>
public static class RepoUrl
{
    public static RepoLocation Classify(string? url)
        => IsRemote(url) ? RepoLocation.Remote : RepoLocation.LocalFolder;

    /// <summary>True for a git remote; false for a folder path (or nothing at all).</summary>
    public static bool IsRemote(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        var value = url.Trim();
        if (value.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return false;

        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd > 0)
            return true;

        return ScpParts(value) is not null;
    }

    /// <summary>The forge serving <paramref name="url"/>, from its host. A host alias from
    /// ~/.ssh/config (<c>github-work</c>) reads as <see cref="ForgeKind.Other"/> — callers let
    /// the user override it.</summary>
    public static ForgeKind DetectForge(string? url)
    {
        var host = Host(url);
        if (host is null)
            return ForgeKind.Other;

        // GHE.com data residency (acme.ghe.com) is GitHub too: gh and the API work the same.
        if (host is "github.com" || host.EndsWith(".github.com", StringComparison.Ordinal)
            || host.EndsWith(".ghe.com", StringComparison.Ordinal))
            return ForgeKind.GitHub;
        if (host is "dev.azure.com" or "ssh.dev.azure.com" or "vs-ssh.visualstudio.com"
            || host.EndsWith(".visualstudio.com", StringComparison.Ordinal))
            return ForgeKind.AzureDevOps;
        if (host is "gitlab.com" || host.StartsWith("gitlab.", StringComparison.Ordinal))
            return ForgeKind.GitLab;
        if (host is "bitbucket.org")
            return ForgeKind.Bitbucket;
        return ForgeKind.Other;
    }

    /// <summary>The lower-cased host of a remote, or null for a folder or an unparseable URL.</summary>
    public static string? Host(string? url)
    {
        if (!IsRemote(url))
            return null;

        var value = url!.Trim();
        if (value.Contains("://", StringComparison.Ordinal))
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Host.Length > 0
                ? uri.Host.ToLowerInvariant()
                : null;

        return ScpParts(value)?.Host.ToLowerInvariant();
    }

    /// <summary>
    /// True when an http(s) URL carries a secret: a password (<c>user:token@</c>) or a user
    /// name that is itself an access token (<c>ghp_…@github.com</c>). A plain user name is not
    /// one — Azure DevOps hands out <c>https://{org}@dev.azure.com/…</c> clone URLs. A stored
    /// URL with a secret in it would be written into every clone's config.
    /// </summary>
    public static bool HasEmbeddedCredential(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http")
            || string.IsNullOrEmpty(uri.UserInfo))
            return false;

        var user = Uri.UnescapeDataString(uri.UserInfo);
        return user.Contains(':')
            || TokenPrefixes.Any(p => user.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly string[] TokenPrefixes =
        ["ghp_", "gho_", "ghs_", "ghu_", "github_pat_", "glpat-", "x-access-token", "oauth2"];

    /// <summary>
    /// The repository's identity on its forge, or null when the path does not have the shape
    /// the forge uses. Azure DevOps understands both https
    /// (<c>dev.azure.com/{org}/{project}/_git/{repo}</c>, <c>{org}.visualstudio.com/{project}/_git/{repo}</c>)
    /// and ssh (<c>ssh.dev.azure.com:v3/{org}/{project}/{repo}</c>).
    /// </summary>
    public static ForgeRepoRef? ParseRef(string? url, ForgeKind? forge = null)
    {
        var host = Host(url);
        var path = Path(url);
        if (host is null || path is null)
            return null;

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();
        if (segments.Length > 0)
            segments[^1] = StripGitSuffix(segments[^1]);

        return (forge ?? DetectForge(url)) switch
        {
            ForgeKind.AzureDevOps => ParseAzureDevOps(host, segments),
            _ => segments.Length >= 2
                ? new ForgeRepoRef(host, string.Join('/', segments[..^1]), segments[^1])
                : null,
        };
    }

    private static ForgeRepoRef? ParseAzureDevOps(string host, string[] s)
    {
        // ssh: v3/{org}/{project}/{repo}
        if (s.Length == 4 && s[0] == "v3")
            return new ForgeRepoRef(host, s[1], s[3], s[2]);

        var git = Array.IndexOf(s, "_git");
        if (git < 1 || git != s.Length - 2)
            return null;

        // dev.azure.com/{org}/{project}/_git/{repo}
        if (host == "dev.azure.com" && git == 2)
            return new ForgeRepoRef(host, s[0], s[3], s[1]);

        // {org}.visualstudio.com[/DefaultCollection]/{project}/_git/{repo}
        if (host.EndsWith(".visualstudio.com", StringComparison.Ordinal))
        {
            var org = host[..host.IndexOf('.')];
            return new ForgeRepoRef(host, org, s[git + 1], s[git - 1]);
        }

        return null;
    }

    private static string? Path(string? url)
    {
        if (!IsRemote(url))
            return null;

        var value = url!.Trim();
        if (value.Contains("://", StringComparison.Ordinal))
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.AbsolutePath : null;

        return ScpParts(value)?.Path;
    }

    private static string StripGitSuffix(string name)
        => name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    /// <summary>
    /// <c>[user@]host:path</c> — git's scp-like syntax. Not a folder: a Windows drive letter
    /// (<c>C:\x</c>, <c>C:/x</c>) is one character before the colon, and git itself treats a
    /// path with a slash before the first colon as local.
    /// </summary>
    private static (string Host, string Path)? ScpParts(string value)
    {
        var colon = value.IndexOf(':');
        if (colon <= 0 || colon == value.Length - 1)
            return null;

        var before = value[..colon];
        if (before.Contains('/') || before.Contains('\\'))
            return null;

        var at = before.LastIndexOf('@');
        var host = at >= 0 ? before[(at + 1)..] : before;
        if (host.Length <= 1)
            return null;

        return (host, value[(colon + 1)..]);
    }
}
