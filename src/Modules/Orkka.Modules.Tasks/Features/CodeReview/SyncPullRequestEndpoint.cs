namespace Orkka.Modules.Tasks.Features.CodeReview;

using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;
using Orkka.Modules.SourceControl.Contracts;
using Microsoft.Extensions.Logging;

using FastEndpoints;

/// <summary>What the forge said, and what Orkka did about it.</summary>
public sealed record SyncPullRequestResponse(
    // "merged" | "closed" | "open"
    string PrState,
    // True when this call changed the task (completed it, or recorded the closed PR).
    bool TaskUpdated,
    string Message);

/// <summary>
/// Manual escape hatch: re-reads the task's PR from the forge and reconciles the task with
/// the answer — a PR merged on GitHub completes the task (unblocking its dependents), a
/// closed one is recorded, an open one reports its state. Exists because the webhook can
/// miss and the sweeps have blind spots; before this, the fix was a database UPDATE.
/// </summary>
public sealed class SyncPullRequestEndpoint(
    TasksDbContext db,
    ITasksApi tasksApi,
    Orkka.Modules.Projects.Contracts.IProjectsApi projectsApi,
    ICurrentUser currentUser,
    IPullRequestService pullRequests,
    ILogger<SyncPullRequestEndpoint> logger)
    : EndpointWithoutRequest<SyncPullRequestResponse>
{
    public override void Configure()
    {
        Post("/api/tasks/{taskId:ulid}/pull-request/sync");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.CodeReviewMerge))
            return;

        var taskId = Ulid.Parse(Route<string>("taskId"));
        var (context, taskExists, resolveError) = await CodeReviewHelpers.ResolveTaskContextDetailedAsync(
            db, projectsApi, taskId, currentUser.OrgId, ct);
        if (context is null)
        {
            if (!taskExists) { await Send.NotFoundAsync(ct); return; }
            ThrowError(resolveError ?? "The pull request could not be resolved.");
            return;
        }

        var prDetail = await pullRequests.GetAsync(
            context.RepoId, context.PrNumber, ct);
        if (prDetail.IsFailure)
        {
            ThrowError($"Could not read the pull request from the repository: {prDetail.Error}");
            return;
        }

        var pr = prDetail.Value!;
        var task = context.Task;

        if (pr.Merged)
        {
            if (task.IsMerged && task.CurrentLane == Lane.Done)
            {
                await Send.OkAsync(new SyncPullRequestResponse(
                    "merged", false, "Already in sync — the PR is merged and the task is Done."), ct);
                return;
            }

            logger.LogInformation(
                "Manual sync: PR for task {TaskId} is merged on the forge — completing the task", taskId);
            await tasksApi.MarkTaskMergedAsync(taskId, pr.MergeCommitSha, ct);
            await Send.OkAsync(new SyncPullRequestResponse(
                "merged", true, "The pull request was already merged on the repository — the task "
                + "is now Done and its dependents are unblocked."), ct);
            return;
        }

        if (pr.State == "closed")
        {
            var already = task.PrClosedAt is not null;
            if (!already)
            {
                logger.LogInformation(
                    "Manual sync: PR for task {TaskId} was closed without merging", taskId);
                await tasksApi.MarkPrClosedAsync(taskId, ct);
            }
            await Send.OkAsync(new SyncPullRequestResponse(
                "closed", !already, "The pull request was closed without merging. Reopen it on the "
                + "repository, or reset the task to start over."), ct);
            return;
        }

        await Send.OkAsync(new SyncPullRequestResponse(
            "open", false, pr.Mergeable == false
                ? "The pull request is open but has conflicts with its base branch."
                : "The pull request is open — nothing to reconcile."), ct);
    }
}
