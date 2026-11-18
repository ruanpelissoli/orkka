namespace Orkka.Modules.SourceControl.Contracts;

using Orkka.SharedKernel.Utilities;

/// <summary>
/// Read-only access to a repository that is a FOLDER on a user's own machine, for a hosted
/// process that has no such folder: the same reads <see cref="IGitRepoReader"/> offers, each
/// naming the user whose runner holds the folder.
///
/// Exists because the Repo page of a hybrid project — file tree, file contents, commits,
/// branches — asked the platform's own reader to mirror <c>C:\Code\app</c> on an API pod,
/// which can only ever fail. The runner already serves every one of these reads for Orkka
/// Local; this is the route to it from the hosted side (the Worker relay), expressed in plain
/// ids so the module that browses repositories never learns the runner transport.
///
/// Every method degrades rather than throws when the machine is unreachable.
/// </summary>
public interface ILocalRepoReader
{
    /// <summary>False when this process has no route to any user's machine at all (a host
    /// without the relay configured). Callers then report the runner as unreachable — there
    /// is no platform copy of the repository to read instead.</summary>
    bool CanReach { get; }

    /// <summary>Refreshes the runner's mirror of the folder so the reads below are current.</summary>
    Task<Result> EnsureMirrorAsync(
        Ulid orgId, Ulid repoId, string runnerUserId, string folderPath, CancellationToken ct = default);

    Task<Result<List<RepoTreeEntry>>> ListTreeAsync(
        Ulid orgId, Ulid repoId, string runnerUserId, string branch, string? dirPath,
        CancellationToken ct = default);

    Task<Result<List<FileTreeNode>>> GetFileTreeAsync(
        Ulid orgId, Ulid repoId, string runnerUserId, string branch, CancellationToken ct = default);

    Task<Result<string>> GetFileContentAsync(
        Ulid orgId, Ulid repoId, string runnerUserId, string branch, string filePath,
        CancellationToken ct = default);

    Task<Result<List<GitCommitInfo>>> GetCommitLogAsync(
        Ulid orgId, Ulid repoId, string runnerUserId, string branch, int skip, int take,
        string? filePath, CancellationToken ct = default);

    Task<Result<List<GitBranchInfo>>> ListBranchesAsync(
        Ulid orgId, Ulid repoId, string runnerUserId, CancellationToken ct = default);

    Task<Result<string>> GetCommitDiffAsync(
        Ulid orgId, Ulid repoId, string runnerUserId, string sha, int maxChars,
        CancellationToken ct = default);
}
