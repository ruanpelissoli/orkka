namespace Orkka.Modules.SourceControl.Contracts;

using Orkka.SharedKernel.Utilities;

/// <summary>
/// The git operations Orkka's pull requests need from the developer's machine, where every
/// repository is cloned with the developer's own access: read a branch's diff, ask whether it
/// would merge cleanly, merge it (a folder), or publish it as a pull request on its forge
/// (a remote).
///
/// Deliberately expressed in plain ids and branch names rather than runner protocol types:
/// the SourceControl module owns pull requests, not the runner transport, and the two hosts
/// reach that machine by completely different routes (in-process link on Orkka Local, the
/// Worker relay in hybrid). Implementations live where that knowledge belongs.
///
/// Every method degrades rather than throws when the machine is unreachable — a folder whose
/// owner has closed their laptop is an ordinary state, not a fault.
/// </summary>
public interface ILocalRepoGitOps
{
    /// <summary>
    /// Squash-merges <paramref name="headBranch"/> into <paramref name="baseBranch"/> inside
    /// the user's working repository. Fails with the user-facing reason when the repository
    /// is not in a state that allows it (see the runner's own preconditions).
    /// </summary>
    Task<Result> SquashMergeAsync(
        Ulid orgId, Ulid repoId, string? runnerUserId,
        string headBranch, string baseBranch, string commitMessage, CancellationToken ct = default);

    /// <summary>The branch's unified diff against its base, empty when it cannot be read.</summary>
    Task<Result<string>> GetDiffAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string? runnerUserId,
        string baseBranch, string headBranch, CancellationToken ct = default);

    /// <summary>The task worktree's working-tree diff against its base (committed and not) —
    /// a run in progress, read live from the runner.</summary>
    Task<Result<string>> GetWorkingTreeDiffAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string? runnerUserId,
        string baseBranch, CancellationToken ct = default);

    /// <summary>A Story member's working-tree diff against its review base
    /// (<paramref name="baseSha"/>) — its own change only. Implementations that cannot fall
    /// back to the diff against the base branch.</summary>
    Task<Result<string>> GetWorkingTreeDiffAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string? runnerUserId,
        string baseBranch, string? baseSha, CancellationToken ct = default)
        => GetWorkingTreeDiffAsync(orgId, repoId, taskId, runnerUserId, baseBranch, ct);

    /// <summary>True when the branch would merge cleanly, false when it conflicts, and null
    /// when the answer is unknown — an offline machine must not read as "conflicted".</summary>
    Task<bool?> WouldMergeCleanlyAsync(
        Ulid orgId, Ulid repoId, string? runnerUserId,
        string headBranch, string baseBranch, CancellationToken ct = default);

    /// <summary>
    /// Opens (or finds the already open) pull request for a remote repository on its forge,
    /// with the developer's own CLI on their runner. Success carries the pull request's URL.
    /// </summary>
    Task<Result<string>> OpenPullRequestAsync(
        Ulid orgId, Ulid repoId, string? runnerUserId,
        string headBranch, string baseBranch, string title, string body,
        ForgeKind forge, string? cliAccount, CancellationToken ct = default);

    /// <summary>A Story member's diff: <paramref name="baseSha"/>..head (its own change only).
    /// Implementations that cannot fall back to the full branch diff.</summary>
    Task<Result<string>> GetDiffAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string? runnerUserId,
        string baseBranch, string headBranch, string? baseSha, CancellationToken ct = default)
        => GetDiffAsync(orgId, repoId, taskId, runnerUserId, baseBranch, headBranch, ct);

    /// <summary>
    /// Opens (or finds) a Story's pull request as a DRAFT with the developer's CLI. Fails with
    /// "update Orkka" on a runner without the <c>stories</c> capability — never opens a
    /// ready pull request in its place.
    /// </summary>
    Task<Result<string>> OpenDraftPullRequestAsync(
        Ulid orgId, Ulid repoId, string? runnerUserId,
        string headBranch, string baseBranch, string title, string body,
        ForgeKind forge, string? cliAccount, CancellationToken ct = default)
        => Task.FromResult(Result<string>.Failure(StoryForgeOps.UpdateRunner));

    /// <summary>Marks a Story pull request ready for review, or (<paramref name="undo"/>) back to draft.</summary>
    Task<Result> MarkPullRequestReadyAsync(
        Ulid orgId, Ulid repoId, string? runnerUserId,
        string pullRequestUrl, bool undo, ForgeKind forge, string? cliAccount, CancellationToken ct = default)
        => Task.FromResult(Result.Failure(StoryForgeOps.UpdateRunner));

    /// <summary>Rewrites a pull request's description.</summary>
    Task<Result> UpdatePullRequestBodyAsync(
        Ulid orgId, Ulid repoId, string? runnerUserId,
        string pullRequestUrl, string body, ForgeKind forge, string? cliAccount, CancellationToken ct = default)
        => Task.FromResult(Result.Failure(StoryForgeOps.UpdateRunner));

    /// <summary>"open", "merged", "closed" or "unknown" for a pull request the runner opened;
    /// null when the machine cannot be asked right now.</summary>
    Task<string?> GetPullRequestStateAsync(
        Ulid orgId, Ulid repoId, string? runnerUserId,
        string pullRequestUrl, ForgeKind forge, string? cliAccount, CancellationToken ct = default);
}

/// <summary>Shared wording for the Story forge operations.</summary>
public static class StoryForgeOps
{
    /// <summary>What a runner without the <c>stories</c> capability is told.</summary>
    public const string UpdateRunner =
        "Your runner is too old to open a Story's draft pull request or mark it ready — run "
        + "`git pull`, then `./orkka start`, and try again.";
}
