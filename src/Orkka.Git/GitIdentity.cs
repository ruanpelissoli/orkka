namespace Orkka.Git;

/// <summary>
/// The author and committer of a commit Orkka makes, or that an agent makes in a workspace
/// Orkka prepared, on the developer's machine: the identity the runner workspace was
/// configured with, or none at all, leaving it to their own git config — the work lands in
/// their employer's repositories under their name, like everything else they push.
///
/// Delivered as <c>GIT_AUTHOR_*</c>/<c>GIT_COMMITTER_*</c> on every process the engine spawns
/// (those beat every config file) and written into a workspace's local config for the commits
/// an agent CLI makes on its own.
/// </summary>
public sealed record GitIdentity(string Name, string Email)
{
    /// <summary>The configured identity when both halves are present, else null: "nothing
    /// configured" means "use my own git config". A half-set identity is worse than none —
    /// git would invent the missing half from the hostname.</summary>
    public static GitIdentity? FromConfigurationOrNull(string? name, string? email) =>
        string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email)
            ? null
            : new GitIdentity(name.Trim(), email.Trim());

    /// <summary>A <c>Co-authored-by</c> trailer for the person whose request this commit
    /// fulfils, or null when nothing is known about them.</summary>
    public static string? CoAuthorTrailer(string? name, string? email) =>
        string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email)
            ? null
            : $"Co-authored-by: {name.Trim()} <{email.Trim()}>";

    internal IEnumerable<(string Key, string Value)> EnvironmentEntries()
    {
        yield return ("GIT_AUTHOR_NAME", Name);
        yield return ("GIT_AUTHOR_EMAIL", Email);
        yield return ("GIT_COMMITTER_NAME", Name);
        yield return ("GIT_COMMITTER_EMAIL", Email);
    }
}
