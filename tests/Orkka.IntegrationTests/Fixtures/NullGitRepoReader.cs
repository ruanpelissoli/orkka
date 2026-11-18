namespace Orkka.IntegrationTests.Fixtures;

using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Utilities;

/// <summary>
/// An <see cref="IGitRepoReader"/> for hand-built execution routes in tests that never read a
/// repo: every call fails loudly instead of touching a disk.
/// </summary>
public sealed class NullGitRepoReader : IGitRepoReader
{
    private const string Reason = "repo reading is not available in this test";

    public Task<Result<List<FileTreeNode>>> GetFileTreeAsync(Ulid orgId, Ulid repoId, string branch, CancellationToken ct = default)
        => Task.FromResult(Result<List<FileTreeNode>>.Failure(Reason));

    public Task<Result<string>> GetFileContentAsync(Ulid orgId, Ulid repoId, string branch, string filePath, CancellationToken ct = default)
        => Task.FromResult(Result<string>.Failure(Reason));

    public Task<Result> EnsureBareCloneAsync(Ulid orgId, Ulid repoId, string gitUrl, CancellationToken ct = default)
        => Task.FromResult(Result.Failure(Reason));

    public Task<Result<List<GitCommitInfo>>> GetCommitLogAsync(Ulid orgId, Ulid repoId, string branch, int skip, int take, string? filePath = null, CancellationToken ct = default)
        => Task.FromResult(Result<List<GitCommitInfo>>.Failure(Reason));

    public Task<Result<List<GitBranchInfo>>> ListBranchesAsync(Ulid orgId, Ulid repoId, CancellationToken ct = default)
        => Task.FromResult(Result<List<GitBranchInfo>>.Failure(Reason));

    public Task<Result> EnsureBranchAsync(Ulid orgId, Ulid repoId, string branch, CancellationToken ct = default)
        => Task.FromResult(Result.Failure(Reason));

    public Task<Result> CreateBranchAsync(Ulid orgId, Ulid repoId, string newBranch, string baseBranch, CancellationToken ct = default)
        => Task.FromResult(Result.Failure(Reason));

    public Task<Result<string>> GetCommitDiffAsync(Ulid orgId, Ulid repoId, string sha, int maxChars = 200_000, CancellationToken ct = default)
        => Task.FromResult(Result<string>.Failure(Reason));

    public Task<Result<List<RepoTreeEntry>>> ListTreeAsync(Ulid orgId, Ulid repoId, string branch, string? dirPath = null, CancellationToken ct = default)
        => Task.FromResult(Result<List<RepoTreeEntry>>.Failure(Reason));
}
