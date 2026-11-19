namespace Orkka.SharedKernel.Utilities;

using System.Text.RegularExpressions;

/// <summary>
/// The line that makes GitHub link a pull request to the issue it works on — and close the issue
/// when the pull request merges: <c>Closes acme/shop#12</c>. Only for an issue in the pull
/// request's own repository: GitHub closes an issue in another repository only for a pull request
/// into its default branch, and a reference to one is better left to the developer.
/// </summary>
public static partial class GitHubIssueLinks
{
    /// <summary><c>Closes owner/repo#n</c>, or null when <paramref name="issueUrl"/> is not a
    /// GitHub issue in the repository <paramref name="repoGitUrl"/> (same owner and name, on the
    /// same host or an ssh host alias).</summary>
    public static string? ClosingReference(string? issueUrl, string? repoGitUrl)
    {
        if (string.IsNullOrWhiteSpace(issueUrl) || IssueUrl().Match(issueUrl.Trim()) is not { Success: true } m)
            return null;
        if (RepoUrl.ParseRef(repoGitUrl, ForgeKind.GitHub) is not { } repo)
            return null;
        var host = m.Groups["host"].Value;
        var owner = m.Groups["owner"].Value;
        var name = m.Groups["repo"].Value;
        var sameRepo = string.Equals(repo.Owner, owner, StringComparison.OrdinalIgnoreCase)
            && string.Equals(repo.Name, name, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(repo.Host, host, StringComparison.OrdinalIgnoreCase) || !repo.Host.Contains('.'));
        return sameRepo ? $"Closes {owner}/{name}#{m.Groups["n"].Value}" : null;
    }

    /// <summary>The closing lines for several issues, each once, in order; empty when none apply.</summary>
    public static IReadOnlyList<string> ClosingReferences(IEnumerable<string?> issueUrls, string? repoGitUrl)
        => [.. issueUrls.Select(u => ClosingReference(u, repoGitUrl)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary><paramref name="body"/> with the closing lines appended (once).</summary>
    public static string AppendClosing(string body, IReadOnlyList<string> references)
    {
        var missing = references.Where(r => !body.Contains(r, StringComparison.OrdinalIgnoreCase)).ToList();
        return missing.Count == 0 ? body : $"{body.TrimEnd()}\n\n{string.Join("\n", missing)}";
    }

    [GeneratedRegex(@"^https?://(?<host>[^/\s]+)/(?<owner>[A-Za-z0-9-]+)/(?<repo>[A-Za-z0-9._-]+)/issues/(?<n>[0-9]+)/?$")]
    private static partial Regex IssueUrl();
}
