namespace Orkka.Modules.SourceControl.Infrastructure.Services;

using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Utilities;

/// <summary>
/// The <see cref="ILocalRepoReader"/> of a process with no route to a user's machine —
/// the default a host that composes the runner relay replaces. <see cref="CanReach"/> is
/// false, so callers say the runner is unreachable instead of reading (Orkka keeps no copy
/// of anyone's code); the methods only answer when someone ignored that.
/// </summary>
public sealed class UnreachableLocalRepoReader : ILocalRepoReader
{
    private const string NoRoute =
        "This repository is a folder on your machine, and this Orkka process has no route to it.";

    public bool CanReach => false;

    public Task<Result> EnsureMirrorAsync(
        Ulid orgId, Ulid repoId, string runnerUserId, string folderPath, CancellationToken ct = default)
        => Task.FromResult(Result.Failure(NoRoute));

    public Task<Result<List<RepoTreeEntry>>> ListTreeAsync(
        Ulid orgId, Ulid repoId, string runnerUserId, string branch, string? dirPath, CancellationToken ct = default)
        => Task.FromResult(Result<List<RepoTreeEntry>>.Failure(NoRoute));

    public Task<Result<List<FileTreeNode>>> GetFileTreeAsync(
        Ulid orgId, Ulid repoId, string runnerUserId, string branch, CancellationToken ct = default)
        => Task.FromResult(Result<List<FileTreeNode>>.Failure(NoRoute));

    public Task<Result<string>> GetFileContentAsync(
        Ulid orgId, Ulid repoId, string runnerUserId, string branch, string filePath, CancellationToken ct = default)
        => Task.FromResult(Result<string>.Failure(NoRoute));

    public Task<Result<List<GitCommitInfo>>> GetCommitLogAsync(
        Ulid orgId, Ulid repoId, string runnerUserId, string branch, int skip, int take, string? filePath,
        CancellationToken ct = default)
        => Task.FromResult(Result<List<GitCommitInfo>>.Failure(NoRoute));

    public Task<Result<List<GitBranchInfo>>> ListBranchesAsync(
        Ulid orgId, Ulid repoId, string runnerUserId, CancellationToken ct = default)
        => Task.FromResult(Result<List<GitBranchInfo>>.Failure(NoRoute));

    public Task<Result<string>> GetCommitDiffAsync(
        Ulid orgId, Ulid repoId, string runnerUserId, string sha, int maxChars, CancellationToken ct = default)
        => Task.FromResult(Result<string>.Failure(NoRoute));
}
