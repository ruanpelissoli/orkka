namespace Orkka.Git;

/// <summary>
/// Turns git's stderr into a <see cref="GitErrorCode"/>. One classifier for every process, so
/// the runner, the Worker and the Job all name the same failure the same way and the ledger's
/// counts mean one thing.
///
/// Order matters: GitHub answers "repository not found" for a private repo the credential
/// cannot see, so authentication patterns are checked first; a ref-lock collision mentions
/// "lock" and must be told apart from a locked index before the generic lock rule runs.
/// </summary>
public static class GitErrorClassifier
{
    private static readonly (GitErrorCode Code, string[] Needles)[] Rules =
    [
        (GitErrorCode.Authentication,
        [
            "authentication failed", "could not read username", "could not read password",
            "terminal prompts disabled", "bad credentials", "invalid username or password",
            "http basic: access denied", "the requested url returned error: 401",
            "the requested url returned error: 403", "authorization failed",
            "permission denied (publickey)", "support for password authentication was removed",
        ]),
        (GitErrorCode.RepositoryNotFound,
        [
            "repository not found", "repository '", "does not appear to be a git repository",
            "the requested url returned error: 404", "not found: the remote",
        ]),
        (GitErrorCode.RefLock,
        [
            "cannot lock ref", "unable to update local ref", "unable to create '",
            "cannot update the ref",
        ]),
        (GitErrorCode.NonFastForward,
        [
            "non-fast-forward", "fetch first", "stale info", "updates were rejected because the remote",
            "updates were rejected because the tip", "rejected because the tip of your current branch",
        ]),
        (GitErrorCode.Rejected,
        [
            "protected branch", "pre-receive hook declined", "permission to ", "push declined",
            "[remote rejected]", "denied to ",
        ]),
        (GitErrorCode.MergeConflict,
        [
            "automatic merge failed", "conflict (", "fix conflicts and then commit",
            "you have unmerged files", "needs merge", "merge conflict",
        ]),
        (GitErrorCode.DirtyWorkingTree,
        [
            "would be overwritten by", "uncommitted changes", "please commit your changes",
            "your local changes to the following files", "untracked working tree files would be overwritten",
            "you have unstaged changes",
        ]),
        (GitErrorCode.Locked,
        [
            "index.lock", "is locked", "is already locked", "another git process seems to be running",
            "is a locked worktree", "already checked out at",
        ]),
        (GitErrorCode.NotARepository,
        [
            "not a git repository", "is not a git repository", "not a valid git repository",
            "is not a working tree", "not a gitdir",
        ]),
        (GitErrorCode.RefNotFound,
        [
            "invalid reference", "unknown revision", "does not match any", "no such ref",
            "couldn't find remote ref", "not a valid object name", "invalid refspec",
            "bad revision", "no such branch", "not something we can merge", "not a commit",
            "remote ref does not exist", "invalid upstream", "ambiguous argument",
        ]),
        (GitErrorCode.IdentityUnknown,
        [
            "author identity unknown", "please tell me who you are", "committer identity unknown",
            "empty ident name",
        ]),
        (GitErrorCode.Network,
        [
            "could not resolve host", "connection timed out", "connection refused", "rpc failed",
            "early eof", "the remote end hung up", "unable to access", "failed to connect",
            "network is unreachable", "operation timed out", "gnutls", "ssl", "tls",
            "transfer closed", "curl ",
        ]),
    ];

    /// <summary>The code for a finished command: <see cref="GitErrorCode.None"/> on exit 0,
    /// otherwise the first rule its stderr matches, else <see cref="GitErrorCode.Unknown"/>.</summary>
    public static GitErrorCode Classify(int exitCode, string? stderr)
    {
        if (exitCode == 0)
            return GitErrorCode.None;

        return Classify(stderr);
    }

    /// <summary>The code for an error text alone, when the exit code is not at hand (a message
    /// that already crossed a wire, an exception). Never <see cref="GitErrorCode.None"/>.</summary>
    public static GitErrorCode Classify(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return GitErrorCode.Unknown;

        // Case-folded once; every needle is lowercase.
        var text = stderr.ToLowerInvariant();

        foreach (var (code, needles) in Rules)
        {
            foreach (var needle in needles)
            {
                if (text.Contains(needle, StringComparison.Ordinal))
                    return code;
            }
        }

        return GitErrorCode.Unknown;
    }

    /// <summary>True for a failure worth retrying as-is: a race or a blip, not a fact about the
    /// repository or the credential.</summary>
    public static bool IsTransient(GitErrorCode code) =>
        code is GitErrorCode.RefLock or GitErrorCode.Locked or GitErrorCode.Network or GitErrorCode.Timeout;

    /// <summary>True when the credential, not the command, is what has to change.</summary>
    public static bool IsCredentialProblem(GitErrorCode code) =>
        code is GitErrorCode.Authentication or GitErrorCode.NoCredential or GitErrorCode.RepositoryNotFound;

    /// <summary>
    /// One plain sentence per code, for the person who has to act on it. Never git's own words:
    /// those describe the subprocess ("terminal prompts disabled"), not what to do.
    /// </summary>
    public static string Describe(GitErrorCode code) => code switch
    {
        GitErrorCode.None => "The operation succeeded.",
        GitErrorCode.GitMissing => "git is not installed on the machine that ran this operation.",
        GitErrorCode.Authentication => "The repository refused Orkka's credential. Re-check the repository's access in project settings.",
        GitErrorCode.RepositoryNotFound => "The remote reports no such repository, or Orkka's credential cannot see it.",
        GitErrorCode.RefNotFound => "A branch or commit this operation needed does not exist yet.",
        GitErrorCode.RefLock => "Two operations raced on the same repository. Retrying usually succeeds.",
        GitErrorCode.NotARepository => "The workspace on disk is not a usable repository and will be rebuilt.",
        GitErrorCode.NonFastForward => "The branch moved on the remote since this workspace last saw it.",
        GitErrorCode.MergeConflict => "The merge has conflicts that need resolving.",
        GitErrorCode.DirtyWorkingTree => "The workspace has uncommitted changes in the way.",
        GitErrorCode.Locked => "The workspace is held by another process. Retrying in a few seconds usually succeeds.",
        GitErrorCode.Network => "The remote could not be reached from the machine that ran this operation.",
        GitErrorCode.Timeout => "The operation took longer than its time budget and was stopped.",
        GitErrorCode.IdentityUnknown => "No commit identity was available for this operation.",
        GitErrorCode.Cancelled => "The operation was cancelled.",
        GitErrorCode.Rejected => "The remote rejected the change: a protected branch, a hook, or a credential that can read but not write.",
        GitErrorCode.NoCredential => "No git credential could reach this repository. Sign git in on the machine running your runner — your credential manager or `gh auth login` for https, an ssh key for ssh.",
        GitErrorCode.Precondition => "The operation's preconditions were not met.",
        _ => "The git operation failed.",
    };
}
