namespace Orkka.Modules.Tasks.Features.Tasks;

using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.Modules.Agents.Contracts;
using Orkka.SharedKernel.Storage;
using Orkka.SharedKernel.Utilities;
using Microsoft.Extensions.Logging;

using Orkka.Modules.Tasks.Features.Tasks;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;

public sealed class CreatePrEndpoint(
    TasksDbContext db,
    Orkka.Modules.Projects.Contracts.IProjectsApi projectsApi,
    ICurrentUser currentUser,
    IPullRequestService pullRequests,
    ILogger<CreatePrEndpoint> logger)
    : EndpointWithoutRequest<TaskResponse>
{
    public override void Configure()
    {
        Post("/api/tasks/{id:ulid}/create-pr");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.TasksExecute))
            return;

        var id = Ulid.Parse(Route<string>("id"));

        var entity = await db.Tasks
            .FirstOrDefaultAsync(t => t.Id == id && t.OrgId == currentUser.OrgId, ct);

        if (entity is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (!string.IsNullOrEmpty(entity.PrUrl))
        {
            ThrowError("This task already has a pull request.");
            return;
        }

        if (string.IsNullOrEmpty(entity.BranchName))
        {
            ThrowError("This task has no branch — the agent may not have pushed code yet.");
            return;
        }

        var taskRepo = entity.RepoId is { } linkedRepoId
            ? await projectsApi.GetRepoAsync(linkedRepoId, ct)
            : null;

        if (taskRepo is null)
        {
            ThrowError("This task is not linked to a repository.");
            return;
        }

        var prBody = $"""
            ## Task
            **{entity.Title}** ({entity.Type})

            {entity.Description ?? "No description provided."}
            """;

        // The base the worktree actually forked from, else the repo's default branch.
        var prBase = entity.BaseBranch ?? taskRepo.DefaultBranch ?? "main";

        var prResult = await pullRequests.CreateForTaskAsync(
            taskRepo.Id, entity.Id, entity.BranchName, prBase, entity.Title, prBody, entity.ReviewBaseSha, ct);

        if (prResult.IsFailure)
        {
            logger.LogError("Failed to create PR for task {TaskId} (branch {Branch}): {Error}",
                id, entity.BranchName, prResult.Error);
            ThrowError($"Failed to create the pull request. {prResult.Error}");
            return;
        }

        entity.PrUrl = prResult.Value!.HtmlUrl;
        // A new PR supersedes a previously closed one — start watching again, and clear any
        // recorded creation failure (same contract as SetTaskPrUrlAsync).
        entity.PrClosedAt = null;
        entity.MergeFailureReason = null;
        entity.MergeFailureKind = null;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("PR created manually for task {TaskId}: {PrUrl}", id, entity.PrUrl);

        var project = await projectsApi.GetProjectSummaryAsync(entity.ProjectId, ct);
        var repo = entity.RepoId is { } repoId ? await projectsApi.GetRepoAsync(repoId, ct) : null;
        await Send.OkAsync(entity.ToResponse(project!.Key, repo), ct);
    }
}
