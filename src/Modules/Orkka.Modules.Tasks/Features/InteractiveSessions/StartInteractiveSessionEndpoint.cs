namespace Orkka.Modules.Tasks.Features.InteractiveSessions;

using FastEndpoints;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Features.Tasks;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;

/// <summary>
/// Opens an interactive development session: a live chat that does real work on a real branch.
///
/// The session IS a task from the first keystroke rather than becoming one when it ends — every
/// capability it needs (worktree lifecycle, usage records keyed on TaskId/ExecutionId, compute
/// metering, timeline, reset, PR creation, the Code Review lane) is already keyed to a task, and
/// materializing it at the end would mean reconciling spend onto a row that did not exist while
/// the money was being spent. See .claude/docs/INTERACTIVE.md.
///
/// Sessions only ever start FRESH. Taking an existing task interactive would make
/// <see cref="TaskEntity.IsInteractive"/> mutable mid-life, flipping the queue exclusion under a
/// running lease — a different feature with a different set of races.
/// </summary>
public sealed class StartInteractiveSessionEndpoint(
    TasksDbContext db,
    IProjectsApi projectsApi,
    IAgentsApi agentsApi,
    ICurrentUser currentUser,
    IProjectResolver projectResolver,
    ITaskNumberService taskNumbers,
    ITaskNotifier taskNotifier,
    Orkka.Modules.Runners.Contracts.IRunnerCoverageResolver coverageResolver,
    ITasksApi tasksApi,
    IModelCatalog modelCatalog,
    ILogger<StartInteractiveSessionEndpoint> logger)
    : Endpoint<StartInteractiveSessionRequest, TaskResponse>
{
    /// <summary>Placeholder until the first turn names the session from what was actually asked.
    /// Shared with the worker, which only overwrites a title still equal to this one.</summary>
    private const string UntitledSession = InteractiveSessionDefaults.UntitledSession;

    public override void Configure()
    {
        Post("/api/projects/{projectId}/interactive-sessions");
    }

    public override async Task HandleAsync(StartInteractiveSessionRequest req, CancellationToken ct)
    {
        // Both gates, deliberately: this creates a task AND starts an agent on it. Neither
        // permission alone describes what the caller is about to do, and the standard member
        // preset carries both, so requiring them is not a practical burden.
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.TasksCreate))
            return;
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.TasksExecute))
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

        // A session edits code, so it is bound to exactly one repo at open — same rule as a
        // task. Cross-repo work in one session is a handoff, not a free-for-all.
        var repo = await projectsApi.GetRepoAsync(req.RepoId, ct);
        if (repo is null || repo.ProjectId != projectId || repo.OrgId != currentUser.OrgId)
        {
            ThrowError("The specified repository does not belong to this project.");
            return;
        }

        if (req.AssignedAgentId is { } assignedAgentId)
        {
            var agentError = await AssignedAgentValidation.ValidateAsync(
                agentsApi, assignedAgentId, projectId, currentUser.OrgId, ct);
            if (agentError is not null)
            {
                ThrowError(agentError);
                return;
            }
        }

        var model = req.Model?.Trim();
        if (!string.IsNullOrEmpty(model) && !(await modelCatalog.GetAsync(ct)).IsSelectable(model))
        {
            ThrowError($"'{model}' is not a selectable model.");
            return;
        }

        // The session runs on its opener's runner; its model decides only what they are told
        // about that runner's coverage.
        var decision = await coverageResolver.ResolveAsync(
            await DevModelLookup.QueryAsync(agentsApi, currentUser.OrgId, currentUser.UserId,
                projectId, req.RepoId, req.AssignedAgentId, model, project.Settings?.DefaultModel, ct), ct);

        var title = string.IsNullOrWhiteSpace(req.Title) ? UntitledSession : req.Title.Trim();

        // Reserved on its own before the insert (one SQLite writer: never inside an open write
        // transaction). A failed insert leaves a gap in the numbering, which is fine.
        var number = await taskNumbers.ReserveAsync(projectId, ct);

        var entity = new TaskEntity
        {
            OrgId = currentUser.OrgId,
            ProjectId = projectId,
            Number = number,
            RepoId = req.RepoId,
            Title = title,
            CurrentLane = Lane.Developing,
            Status = Contracts.TaskStatus.Active,
            CreatedByUserId = currentUser.UserId,
            RequesterLocale = currentUser.Locale,
            AssignedAgentId = req.AssignedAgentId,
            Model = string.IsNullOrEmpty(model) ? null : model,
            // There is no plan and no PM refinement in a session — the human IS the refinement.
            // Set so that nothing which later routes on these gates tries to run them.
            SkipPlanning = true,
            SkipPmReview = true,
            IsInteractive = true,
            InteractiveSessionStartedAt = DateTimeOffset.UtcNow,
            // Work starts the moment the session opens, unlike a queued task that waits.
            StartedAt = DateTimeOffset.UtcNow,
            RunnerUserId = decision.RunnerUserId,
            // Never InQueue: a session is driven by its human, and ListQueuedTasksAsync excludes
            // interactive tasks anyway. Deliberately left out of any milestone — ad-hoc work
            // should not be forced under an outcome, and a task created straight into Developing
            // would skip the milestone's first-task-starts transition.
        };

        db.Tasks.Add(entity);
        await db.SaveChangesAsync(ct);

        await DevModelLookup.AnnounceAsync(tasksApi, entity.Id, decision, ct);

        logger.LogInformation(
            "Interactive session opened on task {TaskId} ({Code}) in project {ProjectId} by {UserId}",
            entity.Id, $"{project.Key}-{entity.Number}", projectId, currentUser.UserId);

        // Board cards should show the session as soon as it opens — a session the board does not
        // know about is exactly the "board is lying" state this design exists to avoid.
        try
        {
            await taskNotifier.SendTaskCreatedAsync(projectId, new TaskCreatedEvent(
                entity.Id,
                entity.Number,
                $"{project.Key}-{entity.Number}",
                entity.Title,
                entity.Priority.ToString(),
                entity.Type.ToString(),
                entity.CurrentLane.ToString(),
                entity.Status.ToString(),
                repo.Id,
                repo.Name,
                repo.CardColor,
                Tags: null,
                entity.CreatedAt));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to broadcast TaskCreated for interactive session {TaskId}", entity.Id);
        }

        await Send.CreatedAtAsync<GetTaskEndpoint>(
            new { id = entity.Id },
            entity.ToResponse(project.Key, repo),
            cancellation: ct);
    }
}
