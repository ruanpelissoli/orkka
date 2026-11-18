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

public sealed class UpdateTaskEndpoint(
    TasksDbContext db,
    Orkka.Modules.Projects.Contracts.IProjectsApi projectsApi,
    IAgentsApi agentsApi,
    ICurrentUser currentUser,
    Orkka.Modules.Runners.Contracts.IRunnerCoverageResolver coverageResolver,
    IModelCatalog modelCatalog,
    ITasksApi tasksApi,
    ITaskNotifier taskNotifier)
    : Endpoint<UpdateTaskRequest, TaskResponse>
{
    public override void Configure()
    {
        Patch("/api/tasks/{id:ulid}");
    }

    public override async Task HandleAsync(UpdateTaskRequest req, CancellationToken ct)
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

        if (req.Title is not null) entity.Title = req.Title;
        if (req.Description is not null) entity.Description = req.Description;
        if (req.Priority is not null) entity.Priority = Enum.Parse<Priority>(req.Priority, ignoreCase: true);
        if (req.Type is not null) entity.Type = Enum.Parse<TaskType>(req.Type, ignoreCase: true);
        if (req.Tags is not null) entity.Tags = Mappers.SerializeTags(req.Tags);
        if (req.ContextFiles is not null) entity.ContextFiles = Mappers.SerializeContextFiles(req.ContextFiles);
        if (req.ContextDocumentIds is not null) entity.ContextDocumentIds = Mappers.SerializeContextDocumentIds(req.ContextDocumentIds);
        if (req.SkipPlanning is not null) entity.SkipPlanning = req.SkipPlanning.Value;
        if (req.SkipTesting is not null) entity.SkipTesting = req.SkipTesting.Value;
        if (req.SkipCodeReview is not null) entity.SkipCodeReview = req.SkipCodeReview.Value;
        entity.RequesterLocale = currentUser.Locale ?? entity.RequesterLocale;

        if (req.RepoId is not null)
        {
            var requestedRepo = await projectsApi.GetRepoAsync(req.RepoId.Value, ct);
            if (requestedRepo is null || requestedRepo.ProjectId != entity.ProjectId
                || requestedRepo.OrgId != currentUser.OrgId)
            {
                ThrowError("The specified repository does not belong to this project.");
                return;
            }
            if (entity.RepoId != req.RepoId)
            {
                // Refuse rather than silently strand merged work: the branch and PR are the
                // record of where it landed, and moving the task would orphan both.
                if (entity.IsMerged)
                {
                    ThrowError("This task's work is already merged, so its repository can't be changed.");
                    return;
                }

                // Mid-run the agent is holding a worktree in the OLD repo; swapping underneath
                // it produces a push to one repo and a PR in another.
                if (entity.HasAgentWorking)
                {
                    ThrowError("An agent is working on this task — wait for it to finish before changing the repository.");
                    return;
                }

                // A member's repo is decided by the split, and its siblings cover the others.
                // Moving one would either duplicate a sibling's repo — two tasks sharing a
                // worktree and a branch, the second quietly overwriting the first — or leave the
                // repo it vacated with no task at all while the merge gate still waits on it.
                // A Story member is one sub-task in one repository; picking its repository is fine
                // (the story's shared branch is recorded per repository when it lands).
                if (entity.GroupId is not null && entity.StoryOrder is null)
                {
                    ThrowError(
                        "This task is one repository's half of a cross-repo task, so its "
                        + "repository can't be changed.");
                    return;
                }

                // Every one of these is scoped to the repo that produced it. Carrying them over
                // is what made a task moved to another repo keep reporting the old repo's PR:
                // PR creation is skipped while PrUrl is set, so the stale one simply stayed.
                // The branch itself is left alone in the old repo — the commits stay reachable.
                entity.BranchName = null;
                entity.PrUrl = null;
                entity.PrClosedAt = null;
                entity.MergeFailureReason = null;
                entity.MergeConflictsJson = null;
                entity.MergeResolutionStrategy = null;
                entity.MergeResolutionGuidance = null;
                entity.MergeResolutionStartedAt = null;
                entity.QueuedMergeStrategy = null;
                entity.QueuedMergeGuidance = null;
            }

            entity.RepoId = req.RepoId;
        }

        var routingInputChanged = false;
        if (req.Model is not null)
        {
            var newModel = req.Model.Trim();
            if (newModel.Length > 0 && !(await modelCatalog.GetAsync(ct)).IsSelectable(newModel))
            {
                ThrowError($"'{newModel}' is not a selectable model.");
                return;
            }
            var normalized = newModel.Length == 0 ? null : newModel;
            if (entity.Model != normalized)
            {
                // Fixed once a run has started: the worktree and session already follow it.
                if (entity.StartedAt is not null || entity.HasAgentWorking)
                {
                    ThrowError("This task has already started — its model can't be changed now.");
                    return;
                }
                entity.Model = normalized;
                routingInputChanged = true;
            }
        }

        // Tri-state: null = unchanged, "" = clear (back to auto-assign), Ulid = pin.
        if (req.AssignedAgentId is not null)
        {
            if (req.AssignedAgentId.Length == 0)
            {
                routingInputChanged |= entity.AssignedAgentId is not null;
                entity.AssignedAgentId = null;
            }
            else
            {
                if (!Ulid.TryParse(req.AssignedAgentId, out var assignedAgentId))
                {
                    ThrowError("AssignedAgentId must be a valid id or an empty string to clear the assignment.");
                    return;
                }
                var agentError = await AssignedAgentValidation.ValidateAsync(
                    agentsApi, assignedAgentId, entity.ProjectId, currentUser.OrgId, ct);
                if (agentError is not null)
                {
                    ThrowError(agentError);
                    return;
                }
                routingInputChanged |= entity.AssignedAgentId != assignedAgentId;
                entity.AssignedAgentId = assignedAgentId;
            }
        }

        // Re-check the owner's runner coverage when what decides the model changed — only
        // before the first run (a started task keeps its model). The caller's runner is asked,
        // which is whose runner the task runs on.
        Orkka.Modules.Runners.Contracts.RunnerCoverageDecision? decision = null;
        if (routingInputChanged && entity.StartedAt is null && !entity.HasAgentWorking)
        {
            var summary = await projectsApi.GetProjectSummaryAsync(entity.ProjectId, ct);
            decision = await coverageResolver.ResolveAsync(
                await DevModelLookup.QueryAsync(agentsApi, currentUser.OrgId, currentUser.UserId,
                    entity.ProjectId, entity.RepoId, entity.AssignedAgentId, entity.Model,
                    summary?.Settings?.DefaultModel, ct), ct);
            entity.RunnerUserId = decision.RunnerUserId;
        }

        // Tri-state: null = unchanged, "" = move to the backlog, Ulid = attach.
        var milestoneChanged = false;
        if (req.MilestoneId is not null)
        {
            if (req.MilestoneId.Length == 0)
            {
                milestoneChanged = entity.MilestoneId is not null;
                entity.MilestoneId = null;
            }
            else
            {
                if (!Ulid.TryParse(req.MilestoneId, out var milestoneId))
                {
                    ThrowError("MilestoneId must be a valid id or an empty string to clear it.");
                    return;
                }

                // Scoped to the task's own project: a milestone from a sibling project would
                // otherwise silently absorb this task and corrupt both roadmaps.
                var milestoneExists = await db.Milestones
                    .AnyAsync(m => m.Id == milestoneId && m.OrgId == currentUser.OrgId
                        && m.ProjectId == entity.ProjectId, ct);

                if (!milestoneExists)
                {
                    ThrowError("The specified milestone does not belong to this project.");
                    return;
                }

                milestoneChanged = entity.MilestoneId != milestoneId;
                entity.MilestoneId = milestoneId;
            }
        }

        await db.SaveChangesAsync(ct);

        if (decision is not null)
            await DevModelLookup.AnnounceAsync(tasksApi, entity.Id, decision, ct);

        // Counts moved on both sides of the reassignment, so the roadmap needs a nudge.
        if (milestoneChanged)
        {
            var counts = await Milestones.MilestoneQueries.GetCountsByMilestoneAsync(
                db, currentUser.OrgId, entity.ProjectId, ct);

            foreach (var (affectedId, affectedCounts) in counts.ByMilestone)
            {
                await taskNotifier.SendMilestoneChangedAsync(entity.ProjectId,
                    new MilestoneChangedEvent(
                        affectedId, Status: null,
                        affectedCounts.Total, affectedCounts.Done,
                        affectedCounts.InFlight, affectedCounts.NeedsAttention,
                        affectedCounts.NotStarted, Deleted: false, DateTimeOffset.UtcNow));
            }
        }

        await taskNotifier.SendTaskUpdatedAsync(entity.ProjectId, new TaskUpdatedEvent(
            entity.Id, req.Title, req.Priority, entity.Status.ToString(),
            entity.CurrentLane.ToString(), entity.IsArchived, DateTimeOffset.UtcNow));

        var project = await projectsApi.GetProjectSummaryAsync(entity.ProjectId, ct);
        var repo = entity.RepoId is { } rid ? await projectsApi.GetRepoAsync(rid, ct) : null;
        await Send.OkAsync(entity.ToResponse(project!.Key, repo), ct);
    }
}
