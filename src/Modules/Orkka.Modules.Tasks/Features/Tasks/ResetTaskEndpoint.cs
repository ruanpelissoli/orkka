namespace Orkka.Modules.Tasks.Features.Tasks;

using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;
using Microsoft.Extensions.Logging;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

// Two-phase reset: this endpoint only flags the task (Status = Resetting) — the worker's
// reset sweep (ProcessResetRequestsAsync) cancels any in-flight run, removes the worktree,
// wipes messages/plans, and finalizes the task back to Drafting/Draft. The API process
// cannot reach worker-node disk, and the in-process event bus does not cross processes,
// so the shared database is the coordination channel (same pattern as merged-worktree sweep).
public sealed class ResetTaskEndpoint(
    TasksDbContext db,
    Orkka.Modules.Projects.Contracts.IProjectsApi projectsApi,
    ICurrentUser currentUser,
    ITaskNotifier taskNotifier,
    ILogger<ResetTaskEndpoint> logger)
    : EndpointWithoutRequest<TaskResponse>
{
    public override void Configure()
    {
        Post("/api/tasks/{id:ulid}/reset");
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

        var project = await projectsApi.GetProjectSummaryAsync(entity.ProjectId, ct);
        var repo = entity.RepoId is { } repoId ? await projectsApi.GetRepoAsync(repoId, ct) : null;

        // Reset already in flight — idempotent no-op so a double-click can't error.
        if (entity.ResetRequestedAt is not null)
        {
            await Send.OkAsync(entity.ToResponse(project!.Key, repo), ct);
            return;
        }

        if (entity.CurrentLane == Lane.Drafting)
        {
            ThrowError("Task is already in Drafting.");
            return;
        }

        if (entity.IsMerged)
        {
            ThrowError("This task's branch was already merged — reset would not undo the merged code. Create a new task instead.");
            return;
        }

        if (entity.IsArchived)
        {
            ThrowError("Unarchive the task first.");
            return;
        }

        entity.Status = TaskStatus.Resetting;
        entity.ResetRequestedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Task {TaskId} reset requested — Lane: {Lane}, awaiting worker sweep",
            id, entity.CurrentLane);

        await taskNotifier.SendStatusChangedAsync(entity.ProjectId, new StatusChangedEvent(
            entity.Id, entity.Status.ToString(), DateTimeOffset.UtcNow));

        await Send.OkAsync(entity.ToResponse(project!.Key, repo), ct);
    }
}
