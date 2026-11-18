namespace Orkka.Modules.Projects.Features.ProjectRepos;

using Orkka.Modules.Projects.Domain;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.SharedKernel.Utilities;

/// <summary>
/// The reader that can actually see a repository: the requesting user's runner
/// (<see cref="ILocalRepoReader"/>). Every repository — a remote or a folder on that machine —
/// is cloned and read there, with the developer's own git access; Orkka's servers hold no
/// credential for anyone's repositories and keep no copy of their code. With no runner to ask
/// every read fails with "your runner is offline".
///
/// The runner asked is the CURRENT user's — the one browsing — which is the same rule every
/// other repository feature applies: the clone lives on the machine of whoever is looking.
/// </summary>
/// <param name="currentUser">Null on a host with no signed-in user (the Worker), where
/// there is no runner to ask on anyone's behalf.</param>
public sealed class RepoReaders(
    ILocalRepoReader local,
    ICurrentUser? currentUser = null)
{
    /// <summary>True when reads can go to the current user's runner.</summary>
    public bool CanReachRunner => local.CanReach && currentUser?.UserId is { Length: > 0 };

    public IGitRepoReader For(ProjectRepo repo) => Current;

    /// <summary>The reader for this request, whatever the repository.</summary>
    public IGitRepoReader Current
        => CanReachRunner
            ? new LocalRepoReaderAdapter(local, currentUser!.UserId!)
            : OfflineRepoReader.Instance;

    /// <summary>What a request with no runner to ask gets: every read fails, saying so.</summary>
    internal sealed class OfflineRepoReader : IGitRepoReader
    {
        public static readonly OfflineRepoReader Instance = new();

        internal const string Offline =
            "Your runner is offline — run `./orkka start` to read repositories. They are "
            + "read on your machine, with your own git access.";

        public Task<Result<List<FileTreeNode>>> GetFileTreeAsync(
            Ulid orgId, Ulid repoId, string branch, CancellationToken ct = default)
            => Task.FromResult(Result<List<FileTreeNode>>.Failure(Offline));

        public Task<Result<string>> GetFileContentAsync(
            Ulid orgId, Ulid repoId, string branch, string filePath, CancellationToken ct = default)
            => Task.FromResult(Result<string>.Failure(Offline));

        public Task<Result> EnsureBareCloneAsync(
            Ulid orgId, Ulid repoId, string gitUrl, CancellationToken ct = default)
            => Task.FromResult(Result.Failure(Offline));

        public Task<Result<List<GitCommitInfo>>> GetCommitLogAsync(
            Ulid orgId, Ulid repoId, string branch, int skip, int take,
            string? filePath = null, CancellationToken ct = default)
            => Task.FromResult(Result<List<GitCommitInfo>>.Failure(Offline));

        public Task<Result<List<GitBranchInfo>>> ListBranchesAsync(
            Ulid orgId, Ulid repoId, CancellationToken ct = default)
            => Task.FromResult(Result<List<GitBranchInfo>>.Failure(Offline));

        public Task<Result> EnsureBranchAsync(
            Ulid orgId, Ulid repoId, string branch, CancellationToken ct = default)
            => Task.FromResult(Result.Failure(Offline));

        public Task<Result> CreateBranchAsync(
            Ulid orgId, Ulid repoId, string newBranch, string baseBranch, CancellationToken ct = default)
            => Task.FromResult(Result.Failure(Offline));

        public Task<Result<string>> GetCommitDiffAsync(
            Ulid orgId, Ulid repoId, string sha, int maxChars = 200_000, CancellationToken ct = default)
            => Task.FromResult(Result<string>.Failure(Offline));

        public Task<Result<List<RepoTreeEntry>>> ListTreeAsync(
            Ulid orgId, Ulid repoId, string branch, string? dirPath = null, CancellationToken ct = default)
            => Task.FromResult(Result<List<RepoTreeEntry>>.Failure(Offline));
    }

    /// <summary>The runner-backed reader wearing the platform reader's interface, bound to
    /// one user. Branch WRITES are not reads: they refuse, naming the reason.</summary>
    internal sealed class LocalRepoReaderAdapter(ILocalRepoReader local, string runnerUserId) : IGitRepoReader
    {
        private const string NotFromHere =
            "Branches are managed in your own git, not from here.";

        public Task<Result<List<FileTreeNode>>> GetFileTreeAsync(
            Ulid orgId, Ulid repoId, string branch, CancellationToken ct = default)
            => local.GetFileTreeAsync(orgId, repoId, runnerUserId, branch, ct);

        public Task<Result<string>> GetFileContentAsync(
            Ulid orgId, Ulid repoId, string branch, string filePath, CancellationToken ct = default)
            => local.GetFileContentAsync(orgId, repoId, runnerUserId, branch, filePath, ct);

        /// <summary>The runner clones the URL (or uses the folder) with the developer's own access.</summary>
        public Task<Result> EnsureBareCloneAsync(
            Ulid orgId, Ulid repoId, string gitUrl, CancellationToken ct = default)
            => local.EnsureMirrorAsync(orgId, repoId, runnerUserId, gitUrl, ct);

        public Task<Result<List<GitCommitInfo>>> GetCommitLogAsync(
            Ulid orgId, Ulid repoId, string branch, int skip, int take,
            string? filePath = null, CancellationToken ct = default)
            => local.GetCommitLogAsync(orgId, repoId, runnerUserId, branch, skip, take, filePath, ct);

        public Task<Result<List<GitBranchInfo>>> ListBranchesAsync(
            Ulid orgId, Ulid repoId, CancellationToken ct = default)
            => local.ListBranchesAsync(orgId, repoId, runnerUserId, ct);

        public Task<Result> EnsureBranchAsync(
            Ulid orgId, Ulid repoId, string branch, CancellationToken ct = default)
            => Task.FromResult(Result.Failure(NotFromHere));

        public Task<Result> CreateBranchAsync(
            Ulid orgId, Ulid repoId, string newBranch, string baseBranch, CancellationToken ct = default)
            => Task.FromResult(Result.Failure(NotFromHere));

        public Task<Result<string>> GetCommitDiffAsync(
            Ulid orgId, Ulid repoId, string sha, int maxChars = 200_000, CancellationToken ct = default)
            => local.GetCommitDiffAsync(orgId, repoId, runnerUserId, sha, maxChars, ct);

        public Task<Result<List<RepoTreeEntry>>> ListTreeAsync(
            Ulid orgId, Ulid repoId, string branch, string? dirPath = null, CancellationToken ct = default)
            => local.ListTreeAsync(orgId, repoId, runnerUserId, branch, dirPath, ct);
    }
}
