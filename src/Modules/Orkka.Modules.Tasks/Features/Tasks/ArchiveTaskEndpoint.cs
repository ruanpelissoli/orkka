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

public sealed class ArchiveTaskEndpoint(
    TasksDbContext db,
    Orkka.Modules.Projects.Contracts.IProjectsApi projectsApi,
    ICurrentUser currentUser,
    ITaskNotifier taskNotifier,
    ILogger<ArchiveTaskEndpoint> logger)
    : EndpointWithoutRequest<TaskResponse>
{
    public override void Configure()
    {
        Post("/api/tasks/{id:ulid}/archive");
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

        if (entity.IsArchived)
        {
            ThrowError("This task is already archived.");
            return;
        }

        entity.IsArchived = true;
        entity.ArchivedAt = DateTimeOffset.UtcNow;
        entity.Status = TaskStatus.Archived;
        // A Story member's place in the chain passes to its predecessor.
        await Infrastructure.Services.StoryChain.RewireDependentsAsync(db, entity, ct);

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Task {TaskId} '{Title}' archived", id, entity.Title);

        await taskNotifier.SendTaskUpdatedAsync(entity.ProjectId, new TaskUpdatedEvent(
            entity.Id, null, null, entity.Status.ToString(),
            entity.CurrentLane.ToString(), true, DateTimeOffset.UtcNow));

        var project = await projectsApi.GetProjectSummaryAsync(entity.ProjectId, ct);
        var repo = entity.RepoId is { } repoId ? await projectsApi.GetRepoAsync(repoId, ct) : null;
        await Send.OkAsync(entity.ToResponse(project!.Key, repo), ct);
    }
}

public sealed class UnarchiveTaskEndpoint(
    TasksDbContext db,
    Orkka.Modules.Projects.Contracts.IProjectsApi projectsApi,
    ICurrentUser currentUser,
    ITaskNotifier taskNotifier,
    ILogger<UnarchiveTaskEndpoint> logger)
    : EndpointWithoutRequest<TaskResponse>
{
    public override void Configure()
    {
        Post("/api/tasks/{id:ulid}/unarchive");
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

        if (!entity.IsArchived)
        {
            ThrowError("This task is not currently archived.");
            return;
        }

        entity.IsArchived = false;
        entity.ArchivedAt = null;

        // Restore status based on current lane
        entity.Status = entity.CurrentLane switch
        {
            Lane.Drafting => TaskStatus.Draft,
            Lane.Ready => TaskStatus.InQueue,
            Lane.PlanReady => TaskStatus.WaitingPlanApproval,
            Lane.Testing => TaskStatus.InQueue,
            Lane.CodeReview => TaskStatus.WaitingCodeReview,
            Lane.Done => TaskStatus.Completed,
            _ => TaskStatus.Active
        };

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Task {TaskId} '{Title}' unarchived, restored to {Status}", id, entity.Title, entity.Status);

        await taskNotifier.SendTaskUpdatedAsync(entity.ProjectId, new TaskUpdatedEvent(
            entity.Id, null, null, entity.Status.ToString(),
            entity.CurrentLane.ToString(), false, DateTimeOffset.UtcNow));

        var project = await projectsApi.GetProjectSummaryAsync(entity.ProjectId, ct);
        var repo = entity.RepoId is { } repoId ? await projectsApi.GetRepoAsync(repoId, ct) : null;
        await Send.OkAsync(entity.ToResponse(project!.Key, repo), ct);
    }
}
