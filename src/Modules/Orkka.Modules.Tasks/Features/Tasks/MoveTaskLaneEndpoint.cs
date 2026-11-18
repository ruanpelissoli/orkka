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

public sealed class MoveTaskLaneEndpoint(
    TasksDbContext db,
    Orkka.Modules.Projects.Contracts.IProjectsApi projectsApi,
    ICurrentUser currentUser,
    ITaskNotifier taskNotifier,
    ILogger<MoveTaskLaneEndpoint> logger)
    : Endpoint<MoveTaskLaneRequest, TaskResponse>
{
    public override void Configure()
    {
        Patch("/api/tasks/{id:ulid}/lane");
    }

    public override async Task HandleAsync(MoveTaskLaneRequest req, CancellationToken ct)
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

        if (!Enum.TryParse<Lane>(req.TargetLane, true, out var targetLane))
        {
            ThrowError($"'{req.TargetLane}' is not a valid pipeline lane.");
            return;
        }

        // Validate transition
        if (entity.CurrentLane == targetLane)
        {
            ThrowError("Task is already in this lane.");
            return;
        }

        if (targetLane == Lane.Drafting)
        {
            ThrowError("Cannot move a task back to Drafting.");
            return;
        }

        if (entity.CurrentLane == Lane.Drafting && targetLane != Lane.Ready)
        {
            ThrowError("Tasks in Drafting can only be submitted to Ready.");
            return;
        }

        // A task whose PULL REQUEST merged is finished, and moving it backward cannot make it
        // otherwise: the code is on the base branch, the PR is closed, and nothing un-merges.
        // What it CAN do is strand the task — back in CodeReview its PR is no longer open (so
        // no approve button), the merge sweep skips it (already merged), and the pipeline has
        // no exit for it. Same rule Reset applies, same message, for the same reason.
        //
        // PrUrl is the discriminator, not IsMerged alone: a no-PR task moved to Done by hand
        // also carries IsMerged (set below, so its dependents unblock), and reopening THAT is
        // harmless — there was never a merge to undo. See the reopen branch further down.
        if (entity.IsMerged && entity.PrUrl is not null && targetLane < entity.CurrentLane)
        {
            ThrowError("This task's branch was already merged — moving it back would not undo the merged code. Create a new task for follow-up work instead.");
            return;
        }

        // Forward moves are gated on dependencies: a task waits (in Ready) until every task
        // it depends on is done AND merged — its agent must build on top of merged code.
        // Backward moves and Drafting → Ready (submit; blocked tasks wait in Ready) stay open.
        if (targetLane > entity.CurrentLane && entity.CurrentLane != Lane.Drafting)
        {
            var unmetBlockers = await db.TaskDependencies
                .AsNoTracking()
                .Where(d => d.TaskId == id)
                .Where(TaskDependencyQueries.IsUnmet)
                .Select(d => new { d.DependsOnTask.Number, d.DependsOnTask.Status, d.DependsOnTask.IsMerged })
                .ToListAsync(ct);

            if (unmetBlockers.Count > 0)
            {
                var blockedProject = await projectsApi.GetProjectSummaryAsync(entity.ProjectId, ct);
                var blockerList = string.Join(", ", unmetBlockers.Select(b =>
                    $"{blockedProject!.Key}-{b.Number} ({(b.Status == Contracts.TaskStatus.Completed && !b.IsMerged ? "done — awaiting merge" : "not done")})"));
                ThrowError($"This task can't move forward yet. Waiting on: {blockerList}. Dependencies must be done and merged first.");
                return;
            }
        }

        var fromLane = entity.CurrentLane;
        entity.CurrentLane = targetLane;

        // Update status based on target lane
        entity.Status = targetLane switch
        {
            Lane.Ready => Contracts.TaskStatus.InQueue,
            Lane.PlanReady => Contracts.TaskStatus.WaitingPlanApproval,
            Lane.Developing => Contracts.TaskStatus.InQueue, // re-queues the dev agent (e.g. merge-conflict rework)
            Lane.Testing => Contracts.TaskStatus.InQueue, // re-queues the QA agent
            Lane.CodeReview => Contracts.TaskStatus.InQueue, // re-queues the reviewer agent — a drag into review is asking for a review, not a parked badge
            Lane.Done => Contracts.TaskStatus.Completed,
            _ => Contracts.TaskStatus.Active
        };

        if (targetLane == Lane.Developing)
        {
            entity.HasAgentWorking = false;
            entity.QaCycleCount = 0; // fresh automated-QA budget for the new dev cycle
            entity.CheckCycleCount = 0; // and a fresh automated-check budget
            // Fresh review budget too, matching SendTaskMessageEndpoint's resume paths. A
            // human dragging a review-escalated task back to Developing is asking for another
            // attempt — leaving the counter at the cap re-escalated it on the next rejection.
            entity.ReviewCycleCount = 0;
        }

        if (targetLane == Lane.CodeReview)
        {
            // Same reasoning as Developing: the drag is a request for a fresh review, and the
            // queue only claims tasks with no agent marked as working.
            entity.HasAgentWorking = false;
            entity.ReviewCycleCount = 0;
        }

        if (targetLane == Lane.Done)
        {
            entity.CompletedAt = DateTimeOffset.UtcNow;
            entity.IsArchived = true;
            entity.ArchivedAt = DateTimeOffset.UtcNow;

            // No PR means nothing to merge (same convention as CompleteTaskWithoutPrAsync);
            // without this, dependents of a manually-completed no-PR task would wait forever
            // for a merge that can never happen. Tasks WITH a PR keep IsMerged=false — the
            // auto-merge sweep lands them.
            if (entity.PrUrl is null)
                entity.IsMerged = true;
        }
        else if (fromLane == Lane.Done)
        {
            // Reopening a completed task: clear the flags set on the way into Done,
            // otherwise the queue (which skips archived tasks) never picks it up again.
            entity.IsArchived = false;
            entity.ArchivedAt = null;
            entity.CompletedAt = null;

            // The synthetic "merged" stamped on a no-PR task on its way INTO Done comes off on
            // the way out, for the same reason the other flags do. Left on, a reopened task
            // that goes on to earn a real PR is never landed — the merge sweep only considers
            // tasks that are NOT merged — while its dependents build on code that never
            // reached the base branch. (A real merge cannot reach this branch: the guard above
            // refuses to move it backward at all.)
            if (entity.PrUrl is null)
                entity.IsMerged = false;

            // Re-arm the documentation pass: whatever the task does next is new work, and the
            // documents describing its first pass may be wrong once it finishes again.
            entity.DocSyncStatus = Contracts.DocSyncStatus.None;
            entity.DocSyncAttempts = 0;
            entity.DocSyncCompletedAt = null;
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Task {TaskId} '{Title}' moved from {FromLane} to {ToLane}", id, entity.Title, fromLane, targetLane);

        await taskNotifier.SendLaneChangedAsync(entity.ProjectId, new LaneChangedEvent(
            entity.Id, entity.Title, fromLane.ToString(), targetLane.ToString(),
            entity.Status.ToString(), DateTimeOffset.UtcNow));

        var project = await projectsApi.GetProjectSummaryAsync(entity.ProjectId, ct);
        var repo = entity.RepoId is { } repoId ? await projectsApi.GetRepoAsync(repoId, ct) : null;
        await Send.OkAsync(entity.ToResponse(project!.Key, repo), ct);
    }
}
