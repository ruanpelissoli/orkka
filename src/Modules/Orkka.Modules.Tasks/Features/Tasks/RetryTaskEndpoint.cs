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

public sealed class RetryTaskEndpoint(
    TasksDbContext db,
    Orkka.Modules.Projects.Contracts.IProjectsApi projectsApi,
    ICurrentUser currentUser,
    ITaskNotifier taskNotifier,
    ILogger<RetryTaskEndpoint> logger)
    : EndpointWithoutRequest<TaskResponse>
{
    public override void Configure()
    {
        Post("/api/tasks/{id:ulid}/retry");
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

        // Only allow retry on stuck/blocked/errored/parked tasks. WaitingCodeReview belongs
        // here: every escalation path parks there (no reviewer, setup failure, unparseable
        // verdict, cycle cap), and without a retry it was a dead end — no PR meant no review
        // UI, no approve, no merge, and no way out short of a reset.
        if (entity.Status is not (TaskStatus.Blocked or TaskStatus.Active
            or TaskStatus.WaitingForRunner or TaskStatus.WaitingCodeReview))
        {
            ThrowError("Only blocked, stuck, or parked tasks can be retried.");
            return;
        }

        var fromLane = entity.CurrentLane;

        // Re-queue the task in its current lane so the worker picks it up again. A retried
        // review escalation gets a fresh cycle budget — re-queueing with the counter at the
        // cap would bounce it straight back to WaitingCodeReview on the first rejection.
        if (entity.Status == TaskStatus.WaitingCodeReview)
            entity.ReviewCycleCount = 0;
        // A human pressed Retry — someone looked at the failure, so the automatic
        // provider-outage recovery gets its budget back too.
        entity.AutoRecoveryCount = 0;
        entity.Status = TaskStatus.InQueue;

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Task {TaskId} retried — Lane: {Lane}, Status: InQueue", id, entity.CurrentLane);

        await taskNotifier.SendLaneChangedAsync(entity.ProjectId, new LaneChangedEvent(
            entity.Id, entity.Title, fromLane.ToString(), entity.CurrentLane.ToString(),
            entity.Status.ToString(), DateTimeOffset.UtcNow));

        var project = await projectsApi.GetProjectSummaryAsync(entity.ProjectId, ct);
        var repo = entity.RepoId is { } repoId ? await projectsApi.GetRepoAsync(repoId, ct) : null;
        await Send.OkAsync(entity.ToResponse(project!.Key, repo), ct);
    }
}
