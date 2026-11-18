namespace Orkka.Modules.Tasks.Features.RepoPulls;

using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Features.CodeReview;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Security;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

public sealed record PrSummaryResponse(
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
    DateTimeOffset? MergedAt,
    string? TaskId,
    int? TaskNumber);

/// <summary>Repo identity for repo-scoped PR endpoints — the repo-first sibling of
/// CodeReviewHelpers.ResolveTaskContextAsync.</summary>
public sealed record RepoPullsContext(RepoSummary Repo)
{
    public Ulid RepoId => Repo.Id;
}

public static class RepoPullsHelpers
{
    public static async Task<RepoPullsContext?> ResolveRepoContextAsync(
        IProjectsApi projectsApi, Ulid repoId, Ulid orgId, CancellationToken ct)
    {
        var repo = await projectsApi.GetRepoAsync(repoId, ct);
        if (repo is null || repo.OrgId != orgId)
            return null;

        return new RepoPullsContext(repo);
    }

    /// <summary>Parses the task ULID out of the orkka/task-{ulid} branch convention.</summary>
    public static Ulid? TaskIdFromBranch(string headBranch)
    {
        const string prefix = "orkka/task-";
        if (!headBranch.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        return Ulid.TryParse(headBranch[prefix.Length..], out var taskId) ? taskId : null;
    }

    /// <summary>
    /// Resolves a task from its PR head branch: the orkka/task-{ulid} convention first,
    /// then a lookup by persisted branch name (friendly orkka/{KEY}-{n} branches and tracker
    /// tickets' templated ones, e.g. feature/SHOP-12-add-invoice-export).
    /// </summary>
    public static async Task<Ulid?> ResolveTaskIdFromBranchAsync(
        TasksDbContext db, Ulid orgId, string headBranch, CancellationToken ct)
    {
        if (TaskIdFromBranch(headBranch) is { } parsed)
            return parsed;

        // A Story's members share one branch: the member still in flight (not landed, not
        // archived), earliest in story order, owns its open pull request.
        var id = await db.Tasks
            .Where(t => t.BranchName == headBranch && t.OrgId == orgId)
            .OrderBy(t => t.IsMerged || t.IsArchived)
            .ThenBy(t => t.StoryOrder ?? 0)
            .ThenByDescending(t => t.CreatedAt)
            .Select(t => t.Id)
            .FirstOrDefaultAsync(ct);
        return id == default ? null : id;
    }
}

public sealed class ListRepoPullsEndpoint(
    TasksDbContext db,
    IProjectsApi projectsApi,
    ICurrentUser currentUser,
    IPullRequestService pullRequests,
    ILogger<ListRepoPullsEndpoint> logger)
    : EndpointWithoutRequest<List<PrSummaryResponse>>
{
    public override void Configure()
    {
        Get("/api/repos/{repoId:ulid}/pulls");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var repoId = Ulid.Parse(Route<string>("repoId"));
        var context = await RepoPullsHelpers.ResolveRepoContextAsync(
            projectsApi, repoId, currentUser.OrgId, ct);
        if (context is null) { await Send.NotFoundAsync(ct); return; }

        var state = Query<string?>("state", isRequired: false) ?? "all";
        var result = await pullRequests.ListAsync(
            context.RepoId, state, ct);
        if (result.IsFailure)
        {
            logger.LogWarning("Failed to list PRs for repo {RepoId}: {Error}", repoId, result.Error);
            ThrowError(result.Error!);
            return;
        }

        // Enrich Orkka-branch PRs with their task so the UI can link to the task review.
        var byBranchTaskId = result.Value!
            .Select(p => (Pr: p, TaskId: RepoPullsHelpers.TaskIdFromBranch(p.HeadBranch)))
            .ToList();

        // Friendly (orkka/{KEY}-{n}) and ticket-templated branches resolve via the persisted name.
        var unresolvedBranches = byBranchTaskId
            .Where(x => x.TaskId is null)
            .Select(x => x.Pr.HeadBranch)
            .Distinct()
            .ToList();
        if (unresolvedBranches.Count > 0)
        {
            var matches = await db.Tasks
                .Where(t => t.OrgId == currentUser.OrgId
                    && t.BranchName != null && unresolvedBranches.Contains(t.BranchName))
                .Select(t => new { t.BranchName, t.Id, t.CreatedAt })
                .ToListAsync(ct);
            var byName = matches
                .GroupBy(m => m.BranchName!)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.CreatedAt).First().Id);
            byBranchTaskId = byBranchTaskId
                .Select(x => x.TaskId is null && byName.TryGetValue(x.Pr.HeadBranch, out var tid)
                    ? (x.Pr, (Ulid?)tid)
                    : x)
                .ToList();
        }

        var taskIds = byBranchTaskId.Where(x => x.TaskId is not null).Select(x => x.TaskId!.Value).ToList();
        var taskNumbers = taskIds.Count == 0
            ? []
            : await db.Tasks
                .Where(t => taskIds.Contains(t.Id) && t.OrgId == currentUser.OrgId)
                .ToDictionaryAsync(t => t.Id, t => t.Number, ct);

        var response = byBranchTaskId.Select(x => new PrSummaryResponse(
            x.Pr.Number, x.Pr.Title, x.Pr.State, x.Pr.Merged, x.Pr.Draft,
            x.Pr.HeadBranch, x.Pr.BaseBranch, x.Pr.HtmlUrl, x.Pr.AuthorLogin,
            x.Pr.CreatedAt, x.Pr.MergedAt,
            x.TaskId?.ToString(),
            x.TaskId is { } tid && taskNumbers.TryGetValue(tid, out var n) ? n : null))
            .ToList();

        await Send.OkAsync(response, ct);
    }
}

public sealed class GetRepoPullEndpoint(
    IProjectsApi projectsApi,
    ICurrentUser currentUser,
    IPullRequestService pullRequests,
    ILogger<GetRepoPullEndpoint> logger)
    : EndpointWithoutRequest<PullRequestDetailResponse>
{
    public override void Configure()
    {
        Get("/api/repos/{repoId:ulid}/pulls/{number:int}");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var repoId = Ulid.Parse(Route<string>("repoId"));
        var number = Route<int>("number");
        var context = await RepoPullsHelpers.ResolveRepoContextAsync(
            projectsApi, repoId, currentUser.OrgId, ct);
        if (context is null) { await Send.NotFoundAsync(ct); return; }

        // Sequential on purpose: IPullRequestService is EF-backed and a DbContext
        // rejects concurrent operations.
        var prResult = await pullRequests.GetAsync(context.RepoId, number, ct);
        if (prResult.IsFailure)
        {
            logger.LogWarning("Failed to get PR #{Number} for repo {RepoId}: {Error}", number, repoId, prResult.Error);
            ThrowError(prResult.Error!);
            return;
        }

        var pr = prResult.Value!;
        var filesResult = await pullRequests.GetFilesAsync(context.RepoId, number, ct);
        var commentsResult = await pullRequests.GetCommentsAsync(context.RepoId, number, ct);
        var reviewsResult = await pullRequests.GetReviewsAsync(context.RepoId, number, ct);

        var files = filesResult is { IsSuccess: true } f
            ? f.Value!.Select(x => new PrFileResponse(
                x.Filename, x.Status, x.Additions, x.Deletions, x.Patch, x.PreviousFilename)).ToList()
            : [];
        var comments = commentsResult is { IsSuccess: true } c
            ? c.Value!.Select(x => new PrReviewCommentResponse(
                x.Id, x.Body, x.Path, x.Line, x.User.Login, x.User.AvatarUrl, x.CreatedAt)).ToList()
            : [];
        var reviews = reviewsResult is { IsSuccess: true } r
            ? r.Value!.Select(x => new PrReviewResponse(
                x.Id, x.State, x.Body, x.User.Login, x.User.AvatarUrl, x.SubmittedAt ?? "")).ToList()
            : [];

        await Send.OkAsync(new PullRequestDetailResponse(
            pr.Number, pr.Title, pr.Body, pr.State,
            pr.Mergeable, pr.Merged, pr.Draft,
            pr.Additions, pr.Deletions, pr.ChangedFiles,
            pr.HeadSha, pr.BaseBranch, pr.HeadBranch,
            files, comments, reviews), ct);
    }
}

/// <summary>
/// Closes a pull request without merging, from the repo's PR list.
///
/// Gated on CodeReviewMerge, the same permission as merging: both are irreversible decisions
/// about whether work lands, and closing someone's PR is not a lesser act than merging it.
/// </summary>
public sealed class CloseRepoPullEndpoint(
    TasksDbContext db,
    IProjectsApi projectsApi,
    ICurrentUser currentUser,
    IPullRequestService pullRequests,
    ITasksApi tasksApi,
    ITaskNotifier taskNotifier,
    ILogger<CloseRepoPullEndpoint> logger)
    : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/api/repos/{repoId:ulid}/pulls/{number:int}/close");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.CodeReviewMerge))
            return;

        var repoId = Ulid.Parse(Route<string>("repoId"));
        var number = Route<int>("number");
        var context = await RepoPullsHelpers.ResolveRepoContextAsync(
            projectsApi, repoId, currentUser.OrgId, ct);
        if (context is null) { await Send.NotFoundAsync(ct); return; }

        // Read the head branch BEFORE closing — it is how an Orkka PR is traced back to its
        // task, and GitHub still reports it afterwards but there is no reason to depend on that.
        var prResult = await pullRequests.GetAsync(
            context.RepoId, number, ct);
        if (prResult.IsFailure) { ThrowError(prResult.Error!); return; }

        var closeResult = await pullRequests.CloseAsync(
            context.RepoId, number, ct);
        if (closeResult.IsFailure)
        {
            logger.LogWarning("Failed to close PR #{Number} for repo {RepoId}: {Error}",
                number, repoId, closeResult.Error);
            ThrowError(closeResult.Error!);
            return;
        }

        // An Orkka-branch PR belongs to a task. Record it as closed — NOT merged: the work did
        // not ship, and marking it merged would move the task to Done, count toward its
        // milestone, and score the forecast as a task that completed.
        if (await RepoPullsHelpers.ResolveTaskIdFromBranchAsync(
                db, currentUser.OrgId, prResult.Value!.HeadBranch, ct) is { } taskId)
        {
            await tasksApi.MarkPrClosedAsync(taskId, ct);

            var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(
                t => t.Id == taskId && t.OrgId == currentUser.OrgId, ct);

            if (task is not null)
            {
                // Reuses the merge-failed channel, which is what already renders the reason on
                // the board card and the task page.
                await taskNotifier.SendTaskMergeFailedAsync(task.ProjectId,
                    new TaskMergeFailedEvent(task.Id, task.MergeFailureReason, DateTimeOffset.UtcNow));
            }
        }

        await Send.NoContentAsync(ct);
    }
}

public sealed class MergeRepoPullEndpoint(
    TasksDbContext db,
    IProjectsApi projectsApi,
    ICurrentUser currentUser,
    IPullRequestService pullRequests,
    ITaskNotifier taskNotifier,
    Orkka.SharedKernel.Events.IEventBus eventBus,
    ILogger<MergeRepoPullEndpoint> logger)
    : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/api/repos/{repoId:ulid}/pulls/{number:int}/merge");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.CodeReviewMerge))
            return;

        var repoId = Ulid.Parse(Route<string>("repoId"));
        var number = Route<int>("number");
        var context = await RepoPullsHelpers.ResolveRepoContextAsync(
            projectsApi, repoId, currentUser.OrgId, ct);
        if (context is null) { await Send.NotFoundAsync(ct); return; }

        // Grab the head branch before merging so we can sync any task it belongs to.
        var prResult = await pullRequests.GetAsync(
            context.RepoId, number, ct);
        if (prResult.IsFailure) { ThrowError(prResult.Error!); return; }

        var mergeResult = await pullRequests.MergeAsync(context.RepoId, number, ct);
        if (mergeResult.IsFailure)
        {
            logger.LogWarning("Failed to merge PR #{Number} for repo {RepoId}: {Error}", number, repoId, mergeResult.Error);

            // Orkka-branch PRs belong to a task — persist the failure there so the
            // board/detail surface it (mirrors the task-scoped merge endpoint), and hand
            // conflict failures to the automatic resolver instead of parking on the human.
            var armed = MergeAutoResolution.ArmResult.NotArmed;
            if (await RepoPullsHelpers.ResolveTaskIdFromBranchAsync(
                    db, currentUser.OrgId, prResult.Value!.HeadBranch, ct) is { } failedTaskId)
            {
                var failedTask = await db.Tasks.FirstOrDefaultAsync(
                    t => t.Id == failedTaskId && t.OrgId == currentUser.OrgId, ct);
                var reason = mergeResult.Error ?? "Merge failed";
                reason = reason.Length > 1000 ? reason[..1000] : reason;
                if (failedTask is not null && !failedTask.IsMerged)
                {
                    armed = await MergeAutoResolution.TryArmAsync(
                        db, pullRequests, context.RepoId,
                        number, failedTask, reason, logger, ct);

                    if (failedTask.MergeFailureReason != reason)
                    {
                        failedTask.MergeFailureReason = reason;
                        var failureMessage = new TaskMessage
                        {
                            TaskId = failedTask.Id,
                            OrgId = failedTask.OrgId,
                            Role = "system",
                            Content = armed == MergeAutoResolution.ArmResult.Armed
                                ? MergeAutoResolution.StartedMessage
                                : $"Merge failed: {reason}",
                            MessageType = armed == MergeAutoResolution.ArmResult.Armed
                                ? MessageType.Text
                                : MessageType.Error
                        };
                        db.TaskMessages.Add(failureMessage);
                        await db.SaveChangesAsync(ct);

                        await taskNotifier.SendTaskMergeFailedAsync(failedTask.ProjectId,
                            new TaskMergeFailedEvent(failedTask.Id, reason, DateTimeOffset.UtcNow));
                        await taskNotifier.SendNewMessageAsync(failedTask.Id, new NewMessageEvent(
                            failedTask.Id, failureMessage.Id, failureMessage.Role, failureMessage.Content,
                            failureMessage.MessageType.ToString(), failureMessage.SenderId, failureMessage.CreatedAt));
                    }
                }
            }

            ThrowError(armed != MergeAutoResolution.ArmResult.NotArmed
                ? MergeAutoResolution.StartedMessage
                : mergeResult.Error!);
            return;
        }

        // Orkka-branch PRs belong to a task — mirror the task-scoped merge transition.
        if (await RepoPullsHelpers.ResolveTaskIdFromBranchAsync(
                db, currentUser.OrgId, prResult.Value!.HeadBranch, ct) is { } taskId)
        {
            var task = await db.Tasks.FirstOrDefaultAsync(
                t => t.Id == taskId && t.OrgId == currentUser.OrgId, ct);
            if (task is not null && !task.IsMerged)
            {
                var fromLane = task.CurrentLane.ToString();
                task.CurrentLane = Lane.Done;
                task.Status = TaskStatus.Completed;
                task.CompletedAt ??= DateTimeOffset.UtcNow;
                task.IsMerged = true;
                var hadFailure = task.MergeFailureReason is not null;
                // Mirror MarkTaskMergedAsync: every piece of merge state comes off with the
                // merge, or a stale kind/strategy keeps steering the merge page and the sweep.
                task.MergeFailureReason = null;
                task.MergeFailureKind = null;
                task.MergeConflictsJson = null;
                task.MergeResolutionStrategy = null;
                task.MergeResolutionGuidance = null;
                task.MergeResolutionStartedAt = null;
                task.QueuedMergeStrategy = null;
                task.QueuedMergeGuidance = null;
                await db.SaveChangesAsync(ct);

                if (hadFailure)
                {
                    // Clear any stale board badges (Reason=null = cleared).
                    await taskNotifier.SendTaskMergeFailedAsync(task.ProjectId,
                        new TaskMergeFailedEvent(task.Id, null, DateTimeOffset.UtcNow));
                }

                logger.LogInformation("PR #{Number} merged from repo view — task {TaskId} moved to Done", number, taskId);
                await taskNotifier.SendLaneChangedAsync(task.ProjectId, new LaneChangedEvent(
                    task.Id, task.Title, fromLane, Lane.Done.ToString(),
                    TaskStatus.Completed.ToString(), DateTimeOffset.UtcNow));
                // Same derivation as MarkTaskMergedAsync — a milestone task's PR lands on the
                // integration branch, not the default, and deploy consumers key on this.
                var landedOnDefault = task.MilestoneId is not { } milestoneId
                    || !await db.Milestones.AsNoTracking()
                        .AnyAsync(m => m.Id == milestoneId && m.BranchName != null, ct);
                await eventBus.PublishAsync(new Orkka.SharedKernel.Events.Catalog.TaskMerged(
                    task.Id, task.OrgId, task.ProjectId, task.RepoId,
                    mergeResult.Value!.MergeCommitSha, landedOnDefault), ct);
            }
        }

        await Send.NoContentAsync(ct);
    }
}

public sealed class SubmitRepoPullReviewEndpoint(
    TasksDbContext db,
    IProjectsApi projectsApi,
    ICurrentUser currentUser,
    IPullRequestService pullRequests,
    ITaskNotifier taskNotifier,
    ILogger<SubmitRepoPullReviewEndpoint> logger)
    : Endpoint<SubmitCodeReviewRequest>
{
    public override void Configure()
    {
        Post("/api/repos/{repoId:ulid}/pulls/{number:int}/reviews");
    }

    public override async Task HandleAsync(SubmitCodeReviewRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.CodeReviewReview))
            return;

        var repoId = Ulid.Parse(Route<string>("repoId"));
        var number = Route<int>("number");
        var context = await RepoPullsHelpers.ResolveRepoContextAsync(
            projectsApi, repoId, currentUser.OrgId, ct);
        if (context is null) { await Send.NotFoundAsync(ct); return; }

        // Head branch first — an Orkka task PR gets the same lane semantics as the
        // task review page, so "approve" means the same thing on every surface.
        var prResult = await pullRequests.GetAsync(
            context.RepoId, number, ct);
        if (prResult.IsFailure) { ThrowError(prResult.Error!); return; }

        var eventType = req.Decision.ToLowerInvariant() switch
        {
            "approve" => "APPROVE",
            "request_changes" => "REQUEST_CHANGES",
            _ => "COMMENT",
        };

        var comments = req.Comments?
            .Select(c => new GitHubNewReviewComment(c.Path, c.Line, c.Body))
            .ToList();

        var result = await pullRequests.CreateReviewAsync(
            context.RepoId, number,
            eventType, req.Body, comments, ct);
        if (result.IsFailure && eventType != "COMMENT")
        {
            // GitHub refuses an APPROVE from the PR's own author — and the Orkka App authors
            // every task PR, so approving from this page ALWAYS hit that wall while the task
            // page quietly fell back. Same fallback here: record the verdict as a COMMENT
            // review and carry on; Orkka's own record of the review is what drives the lane.
            logger.LogWarning(
                "Forge refused {EventType} review for PR #{Number} on repo {RepoId} ({Error}) — falling back to a comment",
                eventType, number, repoId, result.Error);
            var prefix = eventType == "APPROVE" ? "✅ Approved in Orkka." : "🔁 Changes requested in Orkka.";
            var fallbackBody = string.IsNullOrWhiteSpace(req.Body) ? prefix : $"{prefix}\n\n{req.Body}";
            result = await pullRequests.CreateReviewAsync(
                context.RepoId, number,
                "COMMENT", fallbackBody, comments, ct);
        }
        if (result.IsFailure)
        {
            logger.LogWarning("Failed to submit review for PR #{Number} on repo {RepoId}: {Error}", number, repoId, result.Error);
            ThrowError(result.Error!);
            return;
        }

        // Mirror SubmitCodeReviewEndpoint's transitions for Orkka task PRs.
        if (await RepoPullsHelpers.ResolveTaskIdFromBranchAsync(
                db, currentUser.OrgId, prResult.Value!.HeadBranch, ct) is { } taskId)
        {
            var task = await db.Tasks.FirstOrDefaultAsync(
                t => t.Id == taskId && t.OrgId == currentUser.OrgId, ct);
            if (task is not null)
            {
                var fromLane = task.CurrentLane.ToString();
                var changedLane = false;

                if (eventType == "APPROVE" && task.CurrentLane != Lane.Done)
                {
                    task.CurrentLane = Lane.Done;
                    task.Status = TaskStatus.Completed;
                    task.CompletedAt = DateTimeOffset.UtcNow;
                    changedLane = true;
                    logger.LogInformation("Repo-view review approved PR #{Number} — task {TaskId} moved to Done (merge processor will merge)", number, taskId);
                }
                else if (eventType == "REQUEST_CHANGES")
                {
                    if (!string.IsNullOrWhiteSpace(req.Body))
                    {
                        db.TaskMessages.Add(new Orkka.Modules.Tasks.Domain.TaskMessage
                        {
                            TaskId = taskId,
                            OrgId = task.OrgId,
                            Role = "reviewer",
                            Content = req.Body,
                            MessageType = MessageType.CodeReview,
                        });
                    }
                    task.CurrentLane = Lane.Developing;
                    task.Status = TaskStatus.InQueue;
                    task.HasAgentWorking = false;
                    changedLane = true;
                    logger.LogInformation("Repo-view review requested changes on PR #{Number} — task {TaskId} re-queued for dev", number, taskId);
                }

                await db.SaveChangesAsync(ct);

                await taskNotifier.SendCodeReviewedAsync(taskId, new CodeReviewedEvent(
                    taskId, req.Decision, req.Body, DateTimeOffset.UtcNow));
                if (changedLane)
                {
                    await taskNotifier.SendLaneChangedAsync(task.ProjectId, new LaneChangedEvent(
                        taskId, task.Title, fromLane,
                        task.CurrentLane.ToString(), task.Status.ToString(), DateTimeOffset.UtcNow));
                }
            }
        }

        await Send.NoContentAsync(ct);
    }
}

public sealed class AddRepoPullCommentEndpoint(
    IProjectsApi projectsApi,
    ICurrentUser currentUser,
    IPullRequestService pullRequests,
    ILogger<AddRepoPullCommentEndpoint> logger)
    : Endpoint<AddLineCommentRequest, PrReviewCommentResponse>
{
    public override void Configure()
    {
        Post("/api/repos/{repoId:ulid}/pulls/{number:int}/comments");
    }

    public override async Task HandleAsync(AddLineCommentRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.CodeReviewReview))
            return;

        var repoId = Ulid.Parse(Route<string>("repoId"));
        var number = Route<int>("number");
        var context = await RepoPullsHelpers.ResolveRepoContextAsync(
            projectsApi, repoId, currentUser.OrgId, ct);
        if (context is null) { await Send.NotFoundAsync(ct); return; }

        var prResult = await pullRequests.GetAsync(
            context.RepoId, number, ct);
        if (prResult.IsFailure) { ThrowError(prResult.Error!); return; }

        var result = await pullRequests.CreateCommentAsync(
            context.RepoId, number,
            prResult.Value!.HeadSha, req.Path, req.Line, req.Body, ct);
        if (result.IsFailure)
        {
            logger.LogWarning("Failed to add comment on PR #{Number} for repo {RepoId}: {Error}", number, repoId, result.Error);
            ThrowError(result.Error!);
            return;
        }

        var c = result.Value!;
        await Send.OkAsync(new PrReviewCommentResponse(
            c.Id, c.Body, c.Path, c.Line, c.User.Login, c.User.AvatarUrl, c.CreatedAt), ct);
    }
}
