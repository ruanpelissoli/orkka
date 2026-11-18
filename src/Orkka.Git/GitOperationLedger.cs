namespace Orkka.Git;

using System.Diagnostics;
using Microsoft.Extensions.Logging;

/// <summary>
/// The operations the ledger counts: one row per conceptual thing the pipeline asked git to
/// do, not per git command. "Is git reliable" is answered per kind × topology from these.
/// Serialized by name.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum GitOperationKind
{
    /// <summary>Clone or refresh the repository mirror.</summary>
    EnsureMirror,
    /// <summary>Create or reuse the task's workspace (worktree or standalone clone).</summary>
    CreateWorkspace,
    /// <summary>Merge the base branch into the task branch.</summary>
    SyncBase,
    /// <summary>Commit leftovers and push the task branch to the forge.</summary>
    Publish,
    /// <summary>Push only (branch already committed).</summary>
    Push,
    /// <summary>Create a branch on the forge from another branch.</summary>
    CreateBranch,
    /// <summary>Dry-run mergeability check (merge-tree).</summary>
    MergeCheck,
    /// <summary>Apply a deterministic conflict strategy in the workspace.</summary>
    ApplyStrategy,
    /// <summary>Inspect a resolved merge before pushing it.</summary>
    VerifyMerge,
    /// <summary>Compute a diff (committed or working tree).</summary>
    Diff,
    /// <summary>Retired (it refreshed a workspace after a cloud Job run); kept so old ledger rows load.</summary>
    RefreshFromRemote,
    /// <summary>Ask the remote whether a branch exists.</summary>
    RemoteBranchExists,
    /// <summary>Prepare the read-only chat workspace.</summary>
    ChatWorkspace,
    /// <summary>Remove a workspace or a whole repository cache.</summary>
    Cleanup,
    /// <summary>Read-only browsing: file tree, file content, history, branches.</summary>
    Read,
    /// <summary>Initialise a folder as a repository (Orkka Local, folder import).</summary>
    Init,
    /// <summary>Attach a remote to a local repository and push it.</summary>
    AttachRemote,
    /// <summary>Squash-merge a branch into a base with local git (local forge).</summary>
    SquashMerge,
    /// <summary>Preflight: can this machine reach the remote with the credential it was given.</summary>
    Probe,
    /// <summary>Anything else — a caller that did not name its operation.</summary>
    Other,
}

/// <summary>One finished operation, as the ledger records it.</summary>
/// <param name="Topology">Where the git ran: <see cref="GitTopologies"/>.</param>
/// <param name="LastCommand">The redacted git command line that failed, when one did.</param>
public sealed record GitOperationRecord(
    GitOperationKind Kind,
    string Topology,
    string? OrgId,
    string? RepoId,
    string? TaskId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    bool Succeeded,
    GitErrorCode Code,
    string? Error,
    string? LastCommand,
    int CommandCount);

/// <summary>The names the ledger uses for where a git process ran.</summary>
public static class GitTopologies
{
    public const string CloudWorker = "cloud-worker";
    public const string ApiHost = "api-host";
    public const string UserRunner = "user-runner";
    public const string LocalHost = "local-host";
    public const string Unknown = "unknown";
}

/// <summary>
/// Where finished operations go. One implementation per host: the Worker writes rows to the
/// database, the runner and the Job log lines (their operations are recorded host-side by
/// the proxy that invoked them). Must never throw and never block the operation it records.
/// </summary>
public interface IGitOperationLedger
{
    string Topology { get; }

    void Record(GitOperationRecord record);
}

/// <summary>Records nothing. For tests and for callers that do not care.</summary>
public sealed class NullGitOperationLedger(string topology = GitTopologies.Unknown) : IGitOperationLedger
{
    public static readonly NullGitOperationLedger Instance = new();

    public string Topology { get; } = topology;

    public void Record(GitOperationRecord record) { }
}

/// <summary>Records to the log: one Information line per success, one Warning per failure,
/// with the code and the redacted failing command.</summary>
public sealed class LoggingGitOperationLedger(ILogger logger, string topology) : IGitOperationLedger
{
    public string Topology { get; } = topology;

    public void Record(GitOperationRecord record)
    {
        if (record.Succeeded)
        {
            logger.LogInformation(
                "[GitOp] {Kind} ok in {Ms}ms ({Commands} cmd) repo={RepoId} task={TaskId}",
                record.Kind, (int)record.Duration.TotalMilliseconds, record.CommandCount,
                record.RepoId, record.TaskId);
            return;
        }

        logger.LogWarning(
            "[GitOp] {Kind} FAILED {Code} in {Ms}ms repo={RepoId} task={TaskId}: {Error} (last: git {Command})",
            record.Kind, record.Code, (int)record.Duration.TotalMilliseconds,
            record.RepoId, record.TaskId, record.Error, record.LastCommand);
    }
}

/// <summary>
/// One in-flight operation. Opened by the engine's operations (and by any caller wrapping a
/// sequence of git commands into one conceptual step), closed with <see cref="Complete"/> or
/// <see cref="Fail"/>. Disposing without either records a failure — an exception on the way
/// out is a failure, and an operation that forgot to say how it ended must not count as a
/// success.
///
/// The current scope rides an <see cref="AsyncLocal{T}"/> so <see cref="GitProcess"/> can
/// attach the command that failed without every call site threading the scope through.
/// </summary>
public sealed class GitOperationScope : IDisposable
{
    private static readonly AsyncLocal<GitOperationScope?> Current = new();

    private readonly IGitOperationLedger _ledger;
    private readonly GitOperationScope? _parent;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private bool _closed;
    private int _commands;
    private string? _lastFailedCommand;
    private GitErrorCode _lastCode;
    private string? _lastError;

    public GitOperationKind Kind { get; }
    public string? OrgId { get; }
    public string? RepoId { get; }
    public string? TaskId { get; }

    private GitOperationScope(
        IGitOperationLedger ledger, GitOperationKind kind, string? orgId, string? repoId, string? taskId)
    {
        _ledger = ledger;
        Kind = kind;
        OrgId = orgId;
        RepoId = repoId;
        TaskId = taskId;
        _parent = Current.Value;
        Current.Value = this;
    }

    /// <summary>Opens a scope and makes it current for the async flow.</summary>
    public static GitOperationScope Begin(
        IGitOperationLedger ledger, GitOperationKind kind,
        string? orgId = null, string? repoId = null, string? taskId = null)
        => new(ledger, kind, orgId, repoId, taskId);

    /// <summary>The scope the current async flow is inside, or null.</summary>
    public static GitOperationScope? Active => Current.Value;

    /// <summary>Called by <see cref="GitProcess"/> for every command it ran inside this scope.</summary>
    internal void Observe(GitCommandResult result, string redactedCommandLine)
    {
        _commands++;
        if (result.Succeeded)
            return;
        _lastFailedCommand = redactedCommandLine;
        _lastCode = result.Code;
        _lastError = result.Error;
    }

    public void Complete()
    {
        Close(succeeded: true, GitErrorCode.None, null);
    }

    /// <summary>Records the failure. The code defaults to what the last failed command was
    /// classified as, so callers only name a code when they know better than git's stderr.</summary>
    public void Fail(string? error, GitErrorCode? code = null)
    {
        var resolved = code
            ?? (_lastCode != GitErrorCode.None ? (GitErrorCode?)_lastCode : null)
            ?? ParseCodePrefix(error)
            ?? GitErrorClassifier.Classify(error);
        Close(succeeded: false, resolved, error ?? _lastError);
    }

    /// <summary>An error the engine already named — "<c>NoCredential: …</c>", the shape
    /// <see cref="GitCommandResult.Describe"/> and the engine's own refusals produce — keeps its
    /// code when it travels through a <c>Result</c> back to the scope.</summary>
    private static GitErrorCode? ParseCodePrefix(string? error)
    {
        if (string.IsNullOrEmpty(error))
            return null;
        var colon = error.IndexOf(':');
        return colon > 0 && Enum.TryParse<GitErrorCode>(error[..colon], out var code) && code != GitErrorCode.None
            ? (GitErrorCode?)code
            : null;
    }

    private void Close(bool succeeded, GitErrorCode code, string? error)
    {
        if (_closed)
            return;
        _closed = true;
        _clock.Stop();

        try
        {
            _ledger.Record(new GitOperationRecord(
                Kind, _ledger.Topology, OrgId, RepoId, TaskId, _startedAt, _clock.Elapsed,
                succeeded, code, error, _lastFailedCommand, _commands));
        }
        catch
        {
            // A ledger that throws must never fail the operation it describes.
        }
    }

    public void Dispose()
    {
        if (!_closed)
            Close(succeeded: false, _lastCode == GitErrorCode.None ? GitErrorCode.Unknown : _lastCode,
                _lastError ?? "The operation ended without reporting an outcome.");

        if (ReferenceEquals(Current.Value, this))
            Current.Value = _parent;
    }
}
