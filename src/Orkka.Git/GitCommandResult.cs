namespace Orkka.Git;

/// <summary>
/// What one git command produced. <see cref="Error"/> is stderr with every URL credential
/// redacted, trimmed — safe to log, safe to show. <see cref="Code"/> is the classified failure,
/// <see cref="GitErrorCode.None"/> on success.
/// </summary>
public sealed record GitCommandResult(int ExitCode, string Stdout, string Error, GitErrorCode Code)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>Stdout, trimmed — the shape most callers want.</summary>
    public string Output => Stdout.Trim();

    /// <summary>A one-line failure description in the engine's own words, prefixed by the
    /// code so a reader can tell an auth failure from a missing ref without git's text.</summary>
    public string Describe() =>
        Succeeded ? "ok" : $"{Code}: {(string.IsNullOrWhiteSpace(Error) ? $"git exited {ExitCode}" : Error)}";

    /// <summary>An engine-raised failure that never reached git.</summary>
    public static GitCommandResult Failed(GitErrorCode code, string error) => new(-1, "", error, code);
}
