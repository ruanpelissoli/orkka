namespace Orkka.Modules.Tasks.Features.CodeReview;

public sealed record PullRequestDetailResponse(
    int Number, string Title, string? Body, string State,
    bool? Mergeable, bool Merged, bool Draft,
    int Additions, int Deletions, int ChangedFiles,
    string HeadSha, string BaseBranch, string HeadBranch,
    IReadOnlyList<PrFileResponse> Files,
    IReadOnlyList<PrReviewCommentResponse> Comments,
    IReadOnlyList<PrReviewResponse> Reviews,
    bool FilesFromSnapshot = false);

public sealed record PrFileResponse(
    string Filename, string Status, int Additions, int Deletions,
    string? Patch, string? PreviousFilename);

public sealed record PrReviewCommentResponse(
    long Id, string Body, string Path, int? Line,
    string AuthorLogin, string AuthorAvatarUrl, string CreatedAt);

public sealed record PrReviewResponse(
    long Id, string State, string? Body,
    string AuthorLogin, string AuthorAvatarUrl, string SubmittedAt);

public sealed record SubmitCodeReviewRequest(
    string Decision, string? Body, List<NewLineComment>? Comments);

public sealed record NewLineComment(string Path, int Line, string Body);

public sealed record AddLineCommentRequest(string Path, int Line, string Body);
