namespace Orkka.Modules.SourceControl.Contracts;

using Orkka.SharedKernel.Utilities;

/// <summary>
/// A tracker Story's pull requests — one per repository it touches, all opened from the Story's
/// shared branch. Opened as drafts after the first sub-task lands and marked ready after the
/// last (the landing itself runs inside <see cref="IPullRequestService.MergeAsync"/>).
/// </summary>
public interface IStoryPullRequests
{
    /// <summary>
    /// A sub-task joined a Story whose pull requests were already ready: they go back to draft
    /// (<c>gh pr ready --undo</c>, <c>az repos pr update --draft true</c>, <c>glab mr update
    /// --draft</c>) until the new member lands and they are readied again. The Story's ready
    /// mark is cleared whatever the forge says, so the end of the story re-readies them.
    /// </summary>
    Task<Result> ReturnToDraftAsync(Ulid groupId, CancellationToken ct = default);
}
