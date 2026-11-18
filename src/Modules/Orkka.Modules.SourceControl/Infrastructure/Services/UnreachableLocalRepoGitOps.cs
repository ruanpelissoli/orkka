namespace Orkka.Modules.SourceControl.Infrastructure.Services;

using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Utilities;

/// <summary>
/// The default <see cref="ILocalRepoGitOps"/>: a host that has not wired a route to the user's
/// machine cannot reach it, and says so.
///
/// Registered with TryAdd so any host can replace it, and registered AT ALL so that resolving
/// <see cref="IPullRequestService"/> never depends on a host having done that. A missing
/// registration here would fail to construct the service every pull request goes through —
/// turning "the developer's machine is unreachable from this process" into "source control is
/// broken".
/// </summary>
public sealed class UnreachableLocalRepoGitOps : ILocalRepoGitOps
{
    private const string NoRoute =
        "This repository is read on your machine, and this Orkka process has no route to it.";

    public Task<Result> SquashMergeAsync(
        Ulid orgId, Ulid repoId, string? runnerUserId,
        string headBranch, string baseBranch, string commitMessage, CancellationToken ct = default)
        => Task.FromResult(Result.Failure(NoRoute));

    public Task<Result<string>> GetDiffAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string? runnerUserId,
        string baseBranch, string headBranch, CancellationToken ct = default)
        => Task.FromResult(Result<string>.Failure(NoRoute));

    public Task<Result<string>> GetWorkingTreeDiffAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string? runnerUserId,
        string baseBranch, CancellationToken ct = default)
        => Task.FromResult(Result<string>.Failure(NoRoute));

    public Task<bool?> WouldMergeCleanlyAsync(
        Ulid orgId, Ulid repoId, string? runnerUserId,
        string headBranch, string baseBranch, CancellationToken ct = default)
        => Task.FromResult<bool?>(null);

    public Task<Result<string>> OpenPullRequestAsync(
        Ulid orgId, Ulid repoId, string? runnerUserId,
        string headBranch, string baseBranch, string title, string body,
        ForgeKind forge, string? cliAccount, CancellationToken ct = default)
        => Task.FromResult(Result<string>.Failure(NoRoute));

    public Task<string?> GetPullRequestStateAsync(
        Ulid orgId, Ulid repoId, string? runnerUserId,
        string pullRequestUrl, ForgeKind forge, string? cliAccount, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}
