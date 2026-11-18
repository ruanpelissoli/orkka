namespace Orkka.Modules.SourceControl.Contracts;

public sealed record GitHubPullRequest(
    long Id,
    int Number,
    string HtmlUrl,
    string State,
    string? MergeCommitSha = null,
    // The pull request the developer's runner opened on the forge when the work was
    // published; HtmlUrl stays Orkka's own, because the number in it is Orkka's.
    string? ForgeUrl = null);

/// <summary>PR list item for repo-scoped pull request views.</summary>
public sealed record GitHubPrSummary(
    long Id,
    int Number,
    string Title,
    string State,
    bool Merged,
    bool Draft,
    string HeadBranch,
    string BaseBranch,
    string HtmlUrl,
    string? AuthorLogin,
    DateTimeOffset CreatedAt,
    DateTimeOffset? MergedAt);

public sealed record GitHubPullRequestDetail(
    long Id, int Number, string HtmlUrl, string State,
    string Title, string? Body, bool? Mergeable, bool Draft,
    int Additions, int Deletions, int ChangedFiles,
    string HeadSha, string BaseBranch, string HeadBranch,
    bool Merged = false,
    // GitHub's mergeable_state ("behind", "blocked", "clean", "dirty", ...).
    // Null for local PRs and forges that don't report it.
    string? MergeableState = null,
    string? MergeCommitSha = null,
    string? ForgeUrl = null);

public sealed record GitHubPrFile(
    string Sha, string Filename, string Status,
    int Additions, int Deletions, int Changes,
    string? Patch, string? PreviousFilename);

public sealed record GitHubReviewComment(
    long Id, string Body, string Path, int? Line, int? OriginalLine,
    string CommitId, string CreatedAt, string UpdatedAt,
    GitHubUser User);

public sealed record GitHubPrReview(
    long Id, string State, string? Body, string? SubmittedAt,
    GitHubUser User);

public sealed record GitHubUser(long Id, string Login, string AvatarUrl);

public sealed record GitHubNewReviewComment(string Path, int Line, string Body, string? Side = "RIGHT");
