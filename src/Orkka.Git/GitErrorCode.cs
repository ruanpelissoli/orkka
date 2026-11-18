namespace Orkka.Git;

/// <summary>
/// Every way a git operation can fail, as a code the pipeline can branch on and the ledger
/// can count. Classified from git's stderr by <see cref="GitErrorClassifier"/>. Serialized by
/// name wherever it crosses a wire or lands in a table: an ordinal would silently re-point
/// if anyone inserted a value.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum GitErrorCode
{
    /// <summary>The command succeeded — present so a ledger row can carry "no error".</summary>
    None = 0,

    /// <summary>git is not installed or not on PATH for this process.</summary>
    GitMissing,

    /// <summary>The remote refused the credential: 401/403, "Authentication failed", a
    /// credential prompt that could not be answered.</summary>
    Authentication,

    /// <summary>The remote reports no such repository. GitHub answers this way for a private
    /// repo the credential cannot see, so it may mean "no access" rather than "does not
    /// exist" — the order in <see cref="GitErrorClassifier"/> puts authentication first.</summary>
    RepositoryNotFound,

    /// <summary>A ref the command needed does not exist: "invalid reference", "unknown
    /// revision", a missing remote-tracking branch, "src refspec … does not match any".</summary>
    RefNotFound,

    /// <summary>Two git processes raced on the same ref store ("cannot lock ref", "unable to
    /// update local ref", "Unable to create … .lock").</summary>
    RefLock,

    /// <summary>The directory is not a repository (a half-written clone, a deleted mirror).</summary>
    NotARepository,

    /// <summary>The push was rejected because the remote moved ("fetch first",
    /// "non-fast-forward", "stale info").</summary>
    NonFastForward,

    /// <summary>A merge stopped on conflicts ("CONFLICT", "Automatic merge failed").</summary>
    MergeConflict,

    /// <summary>The working tree has changes the command refuses to touch ("would be
    /// overwritten", "uncommitted changes", "Please commit your changes").</summary>
    DirtyWorkingTree,

    /// <summary>A worktree or index is held by another process ("is locked", "index.lock").</summary>
    Locked,

    /// <summary>The remote could not be reached or the transfer died ("Could not resolve
    /// host", "Connection timed out", "RPC failed", "early EOF").</summary>
    Network,

    /// <summary>The command exceeded the deadline the caller gave it and was killed.</summary>
    Timeout,

    /// <summary>A commit was attempted with no author identity at all ("Author identity
    /// unknown"). Should be impossible once every process carries <see cref="GitIdentity"/>.</summary>
    IdentityUnknown,

    /// <summary>The caller cancelled before git finished.</summary>
    Cancelled,

    /// <summary>The remote rejected the write for a policy reason: protected branch, a
    /// pre-receive hook, "permission denied to <user>" on a push the credential can read
    /// but not write.</summary>
    Rejected,

    /// <summary>A repository this machine has no credential for. No longer raised by the
    /// engine (git always runs with the developer's own sign-in); kept so recorded ledger rows
    /// and older runners' messages still parse.</summary>
    NoCredential,

    /// <summary>The operation's preconditions were not met (missing workspace, bad argument).
    /// Raised by the engine, never by git.</summary>
    Precondition,

    /// <summary>Non-zero exit that matched nothing above. Counted so it can be classified
    /// next time.</summary>
    Unknown,
}
