namespace Orkka.Modules.SourceControl.Contracts;

using Orkka.SharedKernel.Utilities;

/// <summary>
/// Orkka's own pull requests — the review record every task's branch gets, for every
/// repository. Each is scoped by the repository's id, never by a forge owner/name: Orkka holds
/// no forge credential and asks no forge API anything. Diffs and mergeability come from the
/// developer's runner.
///
/// "Merge" depends on where the repository lives: a folder on the developer's machine is
/// squash-merged in place; a remote is PUBLISHED — their runner opens a pull request on the
/// forge with their own CLI, and Orkka's part ends there.
/// </summary>
public interface IPullRequestService
{
    /// <summary>Newest first. <paramref name="state"/> is "open", "closed" or "all".</summary>
    Task<Result<IReadOnlyList<GitHubPrSummary>>> ListAsync(
        Ulid repoId, string state = "all", CancellationToken ct = default);

    /// <summary>Idempotent per head branch: an open pull request for it is returned as is.</summary>
    Task<Result<GitHubPullRequest>> CreateAsync(
        Ulid repoId, string head, string baseBranch, string title, string body,
        CancellationToken ct = default);

    /// <summary>
    /// The task's pull request, with the task named explicitly rather than looked up by branch —
    /// a Story's members share one branch, so a branch lookup would pick the wrong one.
    /// Idempotent per (repository, head branch, task). <paramref name="baseSha"/> (a Story
    /// member's <c>ReviewBaseSha</c>) limits its diff to that member's own change.
    /// </summary>
    Task<Result<GitHubPullRequest>> CreateForTaskAsync(
        Ulid repoId, Ulid taskId, string head, string baseBranch, string title, string body,
        string? baseSha = null, CancellationToken ct = default)
        => CreateAsync(repoId, head, baseBranch, title, body, ct);

    /// <summary>Lands the work: a squash merge into a folder, or a forge pull request opened by
    /// the developer's runner for a remote (then <see cref="GitHubPullRequest.ForgeUrl"/> is set).</summary>
    Task<Result<GitHubPullRequest>> MergeAsync(Ulid repoId, int number, CancellationToken ct = default);

    /// <summary>
    /// Closes a pull request without merging. Distinct from merging in every way that matters:
    /// the branch survives, nothing lands on the base, and Orkka records the task as
    /// closed-not-shipped.
    /// </summary>
    Task<Result> CloseAsync(Ulid repoId, int number, CancellationToken ct = default);

    Task<Result<GitHubPullRequestDetail>> GetAsync(Ulid repoId, int number, CancellationToken ct = default);

    Task<Result<IReadOnlyList<GitHubPrFile>>> GetFilesAsync(
        Ulid repoId, int number, CancellationToken ct = default);

    Task<Result<IReadOnlyList<GitHubReviewComment>>> GetCommentsAsync(
        Ulid repoId, int number, CancellationToken ct = default);

    Task<Result<IReadOnlyList<GitHubPrReview>>> GetReviewsAsync(
        Ulid repoId, int number, CancellationToken ct = default);

    Task<Result<GitHubPrReview>> CreateReviewAsync(
        Ulid repoId, int number, string eventType, string? body,
        IReadOnlyList<GitHubNewReviewComment>? comments, CancellationToken ct = default);

    Task<Result<GitHubReviewComment>> CreateCommentAsync(
        Ulid repoId, int number, string commitSha, string path, int line, string body,
        CancellationToken ct = default);
}
