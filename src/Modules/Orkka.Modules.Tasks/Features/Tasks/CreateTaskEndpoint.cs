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

public sealed class CreateTaskEndpoint(
    TasksDbContext db,
    Orkka.Modules.Projects.Contracts.IProjectsApi projectsApi,
    IAgentsApi agentsApi,
    ICurrentUser currentUser,
    IProjectResolver projectResolver,
    ITaskNumberService taskNumbers,
    ITaskNotifier taskNotifier,
    Orkka.Modules.Runners.Contracts.IRunnerCoverageResolver coverageResolver,
    ITasksApi tasksApi,
    IModelCatalog modelCatalog,
    ILogger<CreateTaskEndpoint> logger)
    : Endpoint<CreateTaskRequest, TaskResponse>
{
    public override void Configure()
    {
        Post("/api/projects/{projectId}/tasks");
    }

    public override async Task HandleAsync(CreateTaskRequest req, CancellationToken ct)
    {
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

        // Per-task model override: the form pre-selects the pinned agent's own model and
        // only sends this when the user picked a different one, so an unset value keeps
        // the task tracking whatever the Team page says at run time.
        var model = req.Model?.Trim();
        if (!string.IsNullOrEmpty(model) && !(await modelCatalog.GetAsync(ct)).IsSelectable(model))
        {
            ThrowError($"'{model}' is not a selectable model.");
            return;
        }

        // Spanned repos must belong to the same project as the primary — the same check
        // req.RepoId gets above, and for the same reason: a repo id from another org would
        // otherwise become a worktree this org's agents write into.
        var spannedRepoIds = (req.SpansRepoIds ?? [])
            .Where(id => id != req.RepoId)
            .Distinct()
            .ToList();

        foreach (var spannedRepoId in spannedRepoIds)
        {
            var spannedRepo = await projectsApi.GetRepoAsync(spannedRepoId, ct);
            if (spannedRepo is null || spannedRepo.ProjectId != projectId
                || spannedRepo.OrgId != currentUser.OrgId)
            {
                ThrowError("A repository this task spans does not belong to this project.");
                return;
            }
        }

        if (req.MilestoneId is { } requestedMilestoneId)
        {
            var milestoneExists = await db.Milestones
                .AnyAsync(m => m.Id == requestedMilestoneId && m.OrgId == currentUser.OrgId
                    && m.ProjectId == projectId, ct);

            if (!milestoneExists)
            {
                ThrowError("The specified milestone does not belong to this project.");
                return;
            }
        }

        // The task runs on its creator's runner. The model that will run it decides only what
        // they are told: a provider their runner has not signed in is announced below.
        var decision = await coverageResolver.ResolveAsync(
            await DevModelLookup.QueryAsync(agentsApi, currentUser.OrgId, currentUser.UserId,
                projectId, req.RepoId, req.AssignedAgentId, model, project.Settings?.DefaultModel, ct), ct);

        // Reserved before the insert, on its own: the counter lives on the projects table, and
        // SQLite has one writer — reserving inside an open write transaction here would wait on
        // that very transaction. A failed insert leaves a gap in the numbering, which is fine.
        var number = await taskNumbers.ReserveAsync(projectId, ct);

        // The group is created here but stays empty and Splitting until the task is submitted.
        // Planning the split now would charge for a plan the user may never run — a task can sit
        // in Drafting forever — and would go stale against a repo that keeps moving meanwhile.
        TaskGroup? group = null;
        if (spannedRepoIds.Count > 0)
        {
            group = new TaskGroup
            {
                // Explicit id: the SaveChanges interceptor assigns ids too late for the task
                // below to reference it.
                Id = Ulid.NewUlid(),
                OrgId = currentUser.OrgId,
                ProjectId = projectId,
                MilestoneId = req.MilestoneId,
                Title = req.Title,
                Request = req.Description,
                SpansRepoIdsJson = SpannedRepoIds.Serialize([req.RepoId, .. spannedRepoIds]),
                Status = TaskGroupStatus.Splitting,
                CreatedByUserId = currentUser.UserId,
            };
            db.TaskGroups.Add(group);
        }

        var entity = new TaskEntity
        {
            GroupId = group?.Id,
            OrgId = currentUser.OrgId,
            ProjectId = projectId,
            Number = number,
            RepoId = req.RepoId,
            MilestoneId = req.MilestoneId,
            Title = req.Title,
            Description = req.Description,
            Priority = Enum.Parse<Priority>(req.Priority, ignoreCase: true),
            Type = Enum.Parse<TaskType>(req.Type, ignoreCase: true),
            CurrentLane = Lane.Drafting,
            Status = Contracts.TaskStatus.Draft,
            CreatedByUserId = currentUser.UserId,
            RequesterLocale = currentUser.Locale,
            Tags = Mappers.SerializeTags(req.Tags),
            ContextFiles = Mappers.SerializeContextFiles(req.ContextFiles),
            ContextDocumentIds = Mappers.SerializeContextDocumentIds(req.ContextDocumentIds),
            SkipPlanning = req.SkipPlanning,
            SkipTesting = req.SkipTesting,
            SkipCodeReview = req.SkipCodeReview,
            AssignedAgentId = req.AssignedAgentId,
            Model = string.IsNullOrEmpty(model) ? null : model,
            RunnerUserId = decision.RunnerUserId,
        };

        // One SaveChanges: the group and the task commit together.
        db.Tasks.Add(entity);
        await db.SaveChangesAsync(ct);

        await DevModelLookup.AnnounceAsync(tasksApi, entity.Id, decision, ct);

        var created = await db.Tasks
            .FirstAsync(t => t.Id == entity.Id, ct);

        logger.LogInformation("Task {TaskId} '{Title}' created in project {ProjectId}", created.Id, created.Title, projectId);

        // Push to any open boards — a SignalR failure must not break the create response
        try
        {
            await taskNotifier.SendTaskCreatedAsync(projectId, new TaskCreatedEvent(
                created.Id,
                created.Number,
                $"{project.Key}-{created.Number}",
                created.Title,
                created.Priority.ToString(),
                created.Type.ToString(),
                created.CurrentLane.ToString(),
                created.Status.ToString(),
                repo.Id,
                repo.Name,
                repo.CardColor,
                req.Tags,
                created.CreatedAt));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to broadcast TaskCreated event for task {TaskId}", created.Id);
        }

        await Send.CreatedAtAsync<GetTaskEndpoint>(
            new { id = created.Id },
            created.ToResponse(project.Key, repo),
            cancellation: ct);
    }
}
