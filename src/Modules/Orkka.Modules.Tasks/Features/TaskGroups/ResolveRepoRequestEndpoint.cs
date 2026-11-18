namespace Orkka.Modules.Tasks.Features.TaskGroups;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// Rules on an agent's ask to add a repository to a cross-repo task.
///
/// Approving creates that repository's half as a real, submitted task in the group, ordered
/// after the member that asked for it — the requester found it needed something that repo has to
/// provide, so it is the caller. Declining records the decision so the same request is not put
/// to the user twice by a re-entered phase.
///
/// This is a human decision on purpose: it widens the task's blast radius and its bill, and the
/// gate now waits on one more half.
/// </summary>
public sealed class ResolveRepoRequestEndpoint(
    TasksDbContext db,
    IProjectResolver projectResolver,
    IProjectsApi projectsApi,
    ITaskNumberService taskNumbers,
    ITaskNotifier taskNotifier,
    ICurrentUser currentUser,
    ILogger<ResolveRepoRequestEndpoint> logger)
    : Endpoint<ResolveRepoRequestRequest>
{
    public override void Configure()
    {
        Post("/api/projects/{projectId}/task-groups/{id:ulid}/repo-requests");
    }

    public override async Task HandleAsync(ResolveRepoRequestRequest req, CancellationToken ct)
    {
        // Approving files new work and spends money on it — the same bar as creating a task.
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.TasksCreate))
            return;

        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var project = await projectsApi.GetProjectSummaryAsync(projectId, ct);
        if (project is null || project.OrgId != currentUser.OrgId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var groupId = Route<Ulid>("id");
        var group = await db.TaskGroups.FirstOrDefaultAsync(
            g => g.Id == groupId && g.ProjectId == projectId && g.OrgId == currentUser.OrgId, ct);

        if (group is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var request = RepoRequests.Parse(group.RepoRequestsJson)
            .FirstOrDefault(r =>
                string.Equals(r.RepoName, req.RepoName, StringComparison.OrdinalIgnoreCase)
                && r.Status == RepoRequests.Pending);

        if (request is null)
        {
            ThrowError("There is no open request for that repository on this cross-repo task.");
            return;
        }

        if (!req.Approve)
        {
            group.RepoRequestsJson = RepoRequests.Resolve(
                group.RepoRequestsJson, request.RepoName, RepoRequests.Declined);
            await db.SaveChangesAsync(ct);
            await Send.NoContentAsync(ct);
            return;
        }

        var repos = await projectsApi.ListReposAsync(projectId, ct);
        var repo = repos.FirstOrDefault(r =>
            r.OrgId == currentUser.OrgId
            && string.Equals(r.Name, request.RepoName, StringComparison.OrdinalIgnoreCase));

        if (repo is null)
        {
            // The agent named something the project does not have. Recorded verbatim precisely
            // so this is visible rather than silently dropped at request time.
            ThrowError($"This project has no repository called \"{request.RepoName}\".");
            return;
        }

        // One task per repository is the group's core invariant — two would share a worktree and
        // a branch, and the second would quietly overwrite the first.
        var alreadyCovered = await db.Tasks
            .AnyAsync(t => t.GroupId == groupId && t.RepoId == repo.Id, ct);

        if (alreadyCovered)
        {
            group.RepoRequestsJson = RepoRequests.Resolve(
                group.RepoRequestsJson, request.RepoName, RepoRequests.Approved);
            await db.SaveChangesAsync(ct);
            ThrowError("That repository already has a task in this cross-repo task.");
            return;
        }

        var requester = await db.Tasks.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == Ulid.Parse(request.RequestedByTaskId), ct);

        // Reserved before the transaction (one SQLite writer: reserving inside it would wait on
        // it). A failed insert leaves a gap in the numbering, which is fine.
        var number = await taskNumbers.ReserveAsync(projectId, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var maxQueuePosition = await db.Tasks
            .Where(t => t.OrgId == currentUser.OrgId && t.ProjectId == projectId && t.QueuePosition != null)
            .MaxAsync(t => (int?)t.QueuePosition, ct) ?? 0;

        var sibling = new TaskEntity
        {
            // Explicit id: the interceptor assigns ids too late for the edge below.
            Id = Ulid.NewUlid(),
            OrgId = currentUser.OrgId,
            ProjectId = projectId,
            GroupId = group.Id,
            MilestoneId = group.MilestoneId,
            RepoId = repo.Id,
            Number = number,
            Title = $"{group.Title} — {repo.Name}",
            Description = BuildDescription(group, request),
            Priority = requester?.Priority ?? Priority.Medium,
            Type = requester?.Type ?? TaskType.Feature,
            // Submitted, like the halves the split created: a member left in Drafting would hold
            // the merge gate open forever waiting on a task nobody knew to submit.
            CurrentLane = Lane.Ready,
            Status = TaskStatus.InQueue,
            QueuePosition = maxQueuePosition + 1,
            CreatedByUserId = currentUser.UserId,
            RequesterLocale = currentUser.Locale,
            RunnerUserId = requester?.RunnerUserId ?? currentUser.UserId,
        };

        db.Tasks.Add(sibling);

        // The requester is the caller: it asked because it needs something this repo provides,
        // so the new half defines the interface and goes first.
        if (requester is not null)
        {
            db.TaskDependencies.Add(new TaskDependency
            {
                Id = Ulid.NewUlid(),
                OrgId = currentUser.OrgId,
                TaskId = requester.Id,
                DependsOnTaskId = sibling.Id,
                Reason = "Requested mid-run — this side needs what the new repository provides.",
                // Manual, or the dependency analyzer drops it on its next run.
                Source = DependencySource.Manual,
            });
        }

        group.RepoRequestsJson = RepoRequests.Resolve(
            group.RepoRequestsJson, request.RepoName, RepoRequests.Approved);

        // Approving reopens work on a group that may have been sitting at AwaitingMerge.
        if (group.Status is TaskGroupStatus.AwaitingMerge)
            group.Status = TaskGroupStatus.Active;

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        logger.LogInformation(
            "Approved repo request '{Repo}' on group {GroupId} — created task {TaskId}",
            repo.Name, group.Id, sibling.Id);

        await taskNotifier.SendLaneChangedAsync(projectId, new LaneChangedEvent(
            sibling.Id, sibling.Title, Lane.Drafting.ToString(), Lane.Ready.ToString(),
            sibling.Status.ToString(), DateTimeOffset.UtcNow));

        await Send.NoContentAsync(ct);
    }

    private static string BuildDescription(TaskGroup group, RepoRequest request) =>
        $"""
        ## What

        This repository's half of "{group.Title}", added because another half could not be
        finished without it.

        **Why it was needed:** {request.Reason}

        ## Contract

        The agreed contract for this cross-repo task is injected into your context automatically.
        Implement your side of it exactly.

        ## Acceptance Criteria

        - [ ] This repository's side of the contract is implemented
        - [ ] The half that asked for this can be completed against it
        """;
}
