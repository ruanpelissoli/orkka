namespace Orkka.Modules.Tasks.Features.CodeReview;

using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.Modules.Agents.Contracts;
using Orkka.SharedKernel.Storage;
using Orkka.SharedKernel.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Orkka.Modules.Tasks.Features.CodeReview;
using FastEndpoints;

public sealed class GetPullRequestEndpoint(
    TasksDbContext db,
    Orkka.Modules.Projects.Contracts.IProjectsApi projectsApi,
    ICurrentUser currentUser,
    IPullRequestService pullRequests,
    ILogger<GetPullRequestEndpoint> logger)
    : EndpointWithoutRequest<PullRequestDetailResponse>
{
    public override void Configure()
    {
        Get("/api/tasks/{taskId:ulid}/pull-request");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var taskId = Ulid.Parse(Route<string>("taskId"));

        var context = await CodeReviewHelpers.ResolveTaskContextAsync(db, projectsApi, taskId, currentUser.OrgId, ct);
        if (context is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var repoId = context.RepoId;
        var prNumber = context.PrNumber;

        // Sequential on purpose: IPullRequestService is EF-backed and a DbContext rejects
        // concurrent operations.
        var prResult = await pullRequests.GetAsync(repoId, prNumber, ct);
        if (prResult.IsFailure)
        {
            logger.LogWarning("Failed to get PR for task {TaskId}: {Error}", taskId, prResult.Error);
            ThrowError(prResult.Error!);
            return;
        }

        var pr = prResult.Value!;
        var filesResult = await pullRequests.GetFilesAsync(repoId, prNumber, ct);
        var commentsResult = await pullRequests.GetCommentsAsync(repoId, prNumber, ct);
        var reviewsResult = await pullRequests.GetReviewsAsync(repoId, prNumber, ct);

        var files = filesResult.IsSuccess
            ? filesResult.Value!.Select(f => new PrFileResponse(
                f.Filename, f.Status, f.Additions, f.Deletions, f.Patch, f.PreviousFilename)).ToList()
            : [];

        // Once the head branch is gone (published and merged, or the runner is offline) there is
        // nothing live to compare. Orkka keeps no copy of the code, so the newest code-change
        // snapshot fills in WHICH files changed and by how much — paths and counts, no patch —
        // flagged so the UI says the diff itself lives on the developer's machine.
        var filesFromSnapshot = false;
        if (files.Count == 0)
        {
            var filesJson = await db.CodeChangeSnapshots
                .AsNoTracking()
                .Where(s => s.TaskId == taskId)
                .OrderByDescending(s => s.CreatedAt)
                .Select(s => s.FilesJson)
                .FirstOrDefaultAsync(ct);

            files = SnapshotFiles(filesJson);
            filesFromSnapshot = files.Count > 0;
        }

        var comments = commentsResult.IsSuccess
            ? commentsResult.Value!.Select(c => new PrReviewCommentResponse(
                c.Id, c.Body, c.Path, c.Line,
                c.User.Login, c.User.AvatarUrl, c.CreatedAt)).ToList()
            : [];

        var reviews = reviewsResult.IsSuccess
            ? reviewsResult.Value!.Select(r => new PrReviewResponse(
                r.Id, r.State, r.Body,
                r.User.Login, r.User.AvatarUrl, r.SubmittedAt ?? "")).ToList()
            : [];

        var response = new PullRequestDetailResponse(
            pr.Number, pr.Title, pr.Body, pr.State,
            pr.Mergeable, pr.Merged, pr.Draft,
            pr.Additions, pr.Deletions, pr.ChangedFiles,
            pr.HeadSha, pr.BaseBranch, pr.HeadBranch,
            files, comments, reviews, filesFromSnapshot);

        await Send.OkAsync(response, ct);
    }

    /// <summary>A snapshot's per-file stats as PR files without patches. Malformed JSON → none.</summary>
    internal static List<PrFileResponse> SnapshotFiles(string? filesJson)
    {
        if (string.IsNullOrWhiteSpace(filesJson))
            return [];
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(filesJson);
            return [.. doc.RootElement.EnumerateArray().Select(f => new PrFileResponse(
                f.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "",
                f.TryGetProperty("status", out var st) ? st.GetString() ?? "modified" : "modified",
                f.TryGetProperty("additions", out var a) && a.TryGetInt32(out var add) ? add : 0,
                f.TryGetProperty("deletions", out var d) && d.TryGetInt32(out var del) ? del : 0,
                null, null)).Where(f => f.Filename.Length > 0)];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

}
