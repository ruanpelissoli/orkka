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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Orkka.Modules.Tasks.Features.Tasks;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

public sealed class BlockTaskEndpoint(
    TasksDbContext db,
    Orkka.Modules.Projects.Contracts.IProjectsApi projectsApi,
    ICurrentUser currentUser,
    ITaskNotifier taskNotifier,
    ILogger<BlockTaskEndpoint> logger)
    : EndpointWithoutRequest<TaskResponse>
{
    public override void Configure()
    {
        Post("/api/tasks/{id:ulid}/block");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.TasksEdit))
            return;

        var id = Ulid.Parse(Route<string>("id"));

        var entity = await db.Tasks
            .FirstOrDefaultAsync(t => t.Id == id && t.OrgId == currentUser.OrgId, ct);

        if (entity is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (entity.Status == TaskStatus.Blocked)
        {
            ThrowError("This task is already blocked. Unblock it first before blocking again.");
            return;
        }

        if (entity.CurrentLane is Lane.Drafting or Lane.Done)
        {
            ThrowError($"Cannot block a task in the {entity.CurrentLane} lane. Only active tasks can be blocked.");
            return;
        }

        entity.Status = TaskStatus.Blocked;
        entity.HasAgentWorking = false;

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Task {TaskId} '{Title}' blocked by user", id, entity.Title);

        await taskNotifier.SendStatusChangedAsync(entity.ProjectId, new StatusChangedEvent(
            entity.Id, entity.Status.ToString(), DateTimeOffset.UtcNow));

        var project = await projectsApi.GetProjectSummaryAsync(entity.ProjectId, ct);
        var repo = entity.RepoId is { } repoId ? await projectsApi.GetRepoAsync(repoId, ct) : null;
        await Send.OkAsync(entity.ToResponse(project!.Key, repo), ct);
    }
}

public sealed class UnblockTaskEndpoint(
    TasksDbContext db,
    Orkka.Modules.Projects.Contracts.IProjectsApi projectsApi,
    ICurrentUser currentUser,
    ITaskNotifier taskNotifier,
    ILogger<UnblockTaskEndpoint> logger)
    : EndpointWithoutRequest<TaskResponse>
{
    public override void Configure()
    {
        Post("/api/tasks/{id:ulid}/unblock");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.TasksEdit))
            return;

        var id = Ulid.Parse(Route<string>("id"));

        var entity = await db.Tasks
            .FirstOrDefaultAsync(t => t.Id == id && t.OrgId == currentUser.OrgId, ct);

        if (entity is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (entity.Status != TaskStatus.Blocked)
        {
            ThrowError("This task is not currently blocked.");
            return;
        }

        // Restore status based on current lane — goes back to queue for agent pickup
        entity.Status = entity.CurrentLane switch
        {
            Lane.Ready => TaskStatus.InQueue,
            Lane.PlanReady => TaskStatus.WaitingPlanApproval,
            Lane.CodeReview => TaskStatus.WaitingCodeReview,
            _ => TaskStatus.InQueue // Agent-driven lanes go back to queue
        };

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Task {TaskId} '{Title}' unblocked, restored to {Status}", id, entity.Title, entity.Status);

        await taskNotifier.SendStatusChangedAsync(entity.ProjectId, new StatusChangedEvent(
            entity.Id, entity.Status.ToString(), DateTimeOffset.UtcNow));

        var project = await projectsApi.GetProjectSummaryAsync(entity.ProjectId, ct);
        var repo = entity.RepoId is { } repoId ? await projectsApi.GetRepoAsync(repoId, ct) : null;
        await Send.OkAsync(entity.ToResponse(project!.Key, repo), ct);
    }
}
