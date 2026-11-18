namespace Orkka.Modules.SourceControl.Contracts;

using Orkka.SharedKernel.Utilities;

public interface IGitRepoReader
{
    /// <summary>
    /// Lists all files in the repo at the given branch from the bare clone.
    /// </summary>
    Task<Result<List<FileTreeNode>>> GetFileTreeAsync(
        Ulid orgId, Ulid repoId, string branch, CancellationToken ct = default);

    /// <summary>
    /// Reads a file's content from the bare clone without creating a worktree.
    /// </summary>
    Task<Result<string>> GetFileContentAsync(
        Ulid orgId, Ulid repoId, string branch, string filePath, CancellationToken ct = default);

    /// <summary>
    /// Ensures a bare clone exists (clones if missing, fetches if present).
    /// </summary>
    Task<Result> EnsureBareCloneAsync(
        Ulid orgId, Ulid repoId, string gitUrl, CancellationToken ct = default);

    /// <summary>
    /// Commit history for a branch, newest first. When <paramref name="filePath"/> is set,
    /// returns the change history of that file (follows renames).
    /// </summary>
    Task<Result<List<GitCommitInfo>>> GetCommitLogAsync(
        Ulid orgId, Ulid repoId, string branch, int skip, int take,
        string? filePath = null, CancellationToken ct = default);

    /// <summary>Branches with their tip commit, most recently committed first.</summary>
    Task<Result<List<GitBranchInfo>>> ListBranchesAsync(
        Ulid orgId, Ulid repoId, CancellationToken ct = default);

    /// <summary>
    /// Makes <paramref name="branch"/> usable as a base for worktrees/diffs. Orkka Local
    /// materializes a remote-only branch as a local ref in the user's repository and
    /// refreshes the bare mirror; forge-hosted repos are a no-op (every origin branch is
    /// already fetchable from the bare cache).
    /// </summary>
    Task<Result> EnsureBranchAsync(
        Ulid orgId, Ulid repoId, string branch, CancellationToken ct = default);

    /// <summary>
    /// Creates a new branch from an existing one (local or remote) without touching any
    /// working tree. Orkka Local creates the ref in the user's repository; forge-hosted
    /// repos push the new ref to the remote.
    /// </summary>
    Task<Result> CreateBranchAsync(
        Ulid orgId, Ulid repoId, string newBranch, string baseBranch, CancellationToken ct = default);

    /// <summary>Unified diff introduced by a single commit (truncated to maxChars).</summary>
    Task<Result<string>> GetCommitDiffAsync(
        Ulid orgId, Ulid repoId, string sha, int maxChars = 200_000, CancellationToken ct = default);

    /// <summary>
    /// One level of the repo tree (lazy browsing): the root when
    /// <paramref name="dirPath"/> is null/empty, otherwise that directory's entries.
    /// </summary>
    Task<Result<List<RepoTreeEntry>>> ListTreeAsync(
        Ulid orgId, Ulid repoId, string branch, string? dirPath = null, CancellationToken ct = default);
}
