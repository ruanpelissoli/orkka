namespace Orkka.Git;

/// <summary>URL helpers shared by every git caller.</summary>
public static class GitUrls
{
    /// <summary>The URL with any embedded <c>user:password@</c> removed — what every
    /// <c>origin</c> Orkka writes must look like. Non-http URLs (folders, ssh) are returned
    /// unchanged.</summary>
    public static string Clean(string url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http"))
        {
            return url;
        }

        return string.IsNullOrEmpty(uri.UserInfo)
            ? url
            : $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : $":{uri.Port}")}{uri.PathAndQuery}";
    }

    /// <summary>True for a remote that lives on a forge, false for a folder on the executing
    /// machine.</summary>
    public static bool IsRemote(string? url) =>
        !string.IsNullOrEmpty(url)
        && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("git@", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A non-remote URL is a folder on the executing machine (Orkka Local's local-folder
    /// repos). One that does not exist here lives on ANOTHER machine — a hybrid project's
    /// folder sits on the user's PC while the platform reads on Orkka's compute — and git must
    /// not be asked to guess: on Linux <c>C:\Code\app</c> parses as the scp host "C" and the
    /// failure reads "cannot run ssh", which explains nothing. Returns the precondition
    /// message (with the <see cref="GitErrorCode.Precondition"/> prefix the ledger parses) for
    /// such a folder, null for a remote or for a folder that is here.
    /// </summary>
    public static string? UnreachableLocalFolder(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || IsRemote(url))
            return null;
        var path = url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            ? Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.LocalPath : url
            : url;
        if (Directory.Exists(path))
            return null;
        return $"{GitErrorCode.Precondition}: The folder '{url}' is not on this machine — it lives "
            + "on the Orkka Local runner that hosts it, and only that runner can read or write it.";
    }
}
