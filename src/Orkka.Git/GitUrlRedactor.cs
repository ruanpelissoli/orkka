namespace Orkka.Git;

using System.Text.RegularExpressions;

/// <summary>
/// Removes embedded credentials from git URLs/command strings before they are logged or surfaced
/// in error messages. Git remotes are authenticated by injecting a token into the URL
/// (<c>https://x-access-token:&lt;token&gt;@github.com/...</c>); without redaction that token leaks
/// into Debug logs and any log-shipping pipeline. See SECURITY.md.
/// </summary>
public static partial class GitUrlRedactor
{
    // Matches the "userinfo@" portion of a URL: scheme://<user>[:<password>]@host
    // Replaces the whole userinfo with a fixed marker so neither user nor secret survives.
    [GeneratedRegex(@"(?<scheme>[a-zA-Z][a-zA-Z0-9+.-]*://)[^/\s:@]+(?::[^/\s@]*)?@",
        RegexOptions.Compiled)]
    private static partial Regex CredentialInUrl();

    /// <summary>Returns <paramref name="text"/> with any URL-embedded credentials replaced by <c>***</c>.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        return CredentialInUrl().Replace(text, "${scheme}***@");
    }
}
