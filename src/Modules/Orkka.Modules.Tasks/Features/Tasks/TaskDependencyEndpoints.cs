namespace Orkka.Modules.Tasks.Features.Tasks;

using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.Modules.Tasks.Infrastructure.Services;
using Orkka.SharedKernel.Security;

using FastEndpoints;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

public sealed class AddTaskDependencyValidator : Validator<AddTaskDependencyRequest>
{
    public AddTaskDependencyValidator()
    {
        RuleFor(x => x.Reason)
            .MaximumLength(1000).WithMessage("Reason must be 1000 characters or fewer.");
    }
}

public sealed class AddTaskDependencyEndpoint(
    TasksDbContext db,
    Orkka.Modules.Projects.Contracts.IProjectsApi projectsApi,
    ICurrentUser currentUser,
    ITaskNotifier taskNotifier)
    : Endpoint<AddTaskDependencyRequest, TaskDependenciesResponse>
{
    public override void Configure()
    {
        Post("/api/tasks/{id:ulid}/dependencies");
    }

    public override async Task HandleAsync(AddTaskDependencyRequest req, CancellationToken ct)
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

        if (req.DependsOnTaskId == id)
        {
            ThrowError("A task cannot depend on itself.");
            return;
        }

        var dependsOn = await db.Tasks
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == req.DependsOnTaskId && t.OrgId == currentUser.OrgId, ct);

        if (dependsOn is null || dependsOn.ProjectId != entity.ProjectId)
        {
            ThrowError("The task to depend on was not found in this project.");
            return;
        }

        // Done sets IsArchived, but a Done-but-unmerged task is still a meaningful blocker
        // (dependents wait for the merge) — only reject targets with nothing left to wait on.
        if (dependsOn.Status == TaskStatus.Completed && dependsOn.IsMerged)
        {
            ThrowError("That task is already done and merged — there is nothing to wait on.");
            return;
        }

        if (dependsOn.Status == TaskStatus.Archived)
        {
            ThrowError("Cannot depend on an archived task.");
            return;
        }

        var exists = await db.TaskDependencies
            .AnyAsync(d => d.TaskId == id && d.DependsOnTaskId == req.DependsOnTaskId, ct);

        if (exists)
        {
            ThrowError("This dependency already exists.");
            return;
        }

        if (await TaskDependencyGraph.WouldCreateCycleAsync(db, id, req.DependsOnTaskId, ct))
        {
            ThrowError("This would create a circular dependency.");
            return;
        }

        db.TaskDependencies.Add(new TaskDependency
        {
            TaskId = id,
            DependsOnTaskId = req.DependsOnTaskId,
            Reason = req.Reason,
            Source = DependencySource.Manual,
            OrgId = entity.OrgId
        });

        await db.SaveChangesAsync(ct);

        var project = await projectsApi.GetProjectSummaryAsync(entity.ProjectId, ct);
        var response = await TaskDependencyQueries.BuildResponseAsync(db, id, project!.Key, ct);

        await taskNotifier.SendTaskDependenciesChangedAsync(entity.ProjectId,
            TaskDependencyQueries.ToChangedEvent(id, response));

        await Send.OkAsync(response, ct);
    }
}

public sealed class RemoveTaskDependencyEndpoint(
    TasksDbContext db,
    Orkka.Modules.Projects.Contracts.IProjectsApi projectsApi,
    ICurrentUser currentUser,
    ITaskNotifier taskNotifier)
    : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("/api/tasks/{id:ulid}/dependencies/{dependsOnTaskId:ulid}");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.TasksEdit))
            return;

        var id = Ulid.Parse(Route<string>("id"));
        var dependsOnTaskId = Ulid.Parse(Route<string>("dependsOnTaskId"));

        var entity = await db.Tasks
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id && t.OrgId == currentUser.OrgId, ct);

        if (entity is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Keyed by the (TaskId, DependsOnTaskId) pair — unique per index. Removes AI-detected
        // rows too; that is how users correct a wrong AI dependency.
        var link = await db.TaskDependencies
            .FirstOrDefaultAsync(d => d.TaskId == id && d.DependsOnTaskId == dependsOnTaskId, ct);

        if (link is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        db.TaskDependencies.Remove(link);
        await db.SaveChangesAsync(ct);

        var project = await projectsApi.GetProjectSummaryAsync(entity.ProjectId, ct);
        var response = await TaskDependencyQueries.BuildResponseAsync(db, id, project!.Key, ct);

        await taskNotifier.SendTaskDependenciesChangedAsync(entity.ProjectId,
            TaskDependencyQueries.ToChangedEvent(id, response));

        await Send.NoContentAsync(ct);
    }
}

internal static class TaskDependencyQueries
{
    /// <summary>
    /// A dependency edge is unmet until the blocker's work exists on the target branch:
    /// Completed alone is not enough — the dependent's agent codes on top of merged code.
    ///
    /// Siblings in the same <see cref="TaskGroup"/> are the one exception, and it is not a
    /// relaxation for convenience — without it a gated group cannot merge at all:
    ///
    ///   the consumer waits for the provider to merge   (this predicate)
    ///   the provider waits for the gate                (merge nothing until every member is Done)
    ///   the gate waits for the consumer to reach Done
    ///
    /// What makes starting early safe is that a group's members do not code against each other's
    /// merged source; they code against <c>TaskGroup.Contract</c>, which is written before any of
    /// them runs. Ordering within the group still holds — the edge is met at Completed, not
    /// ignored — so the provider's PR is open and reviewable before the consumer starts. A Story
    /// member relies on the same rule: Completed = passed review with its commits pushed to the
    /// shared branch, which is exactly what the next member clones from.
    ///
    /// An ARCHIVED blocker (status Archived — a person archived it, or its ticket was retired)
    /// counts as met: that work is not coming, and holding its dependents forever is not a plan.
    /// A Story member's dependents are rewired to its predecessor when it is archived
    /// (TasksApi.RewireStoryDependentsAsync), so the story's order still holds.
    /// </summary>
    public static readonly System.Linq.Expressions.Expression<Func<TaskDependency, bool>> IsUnmet =
        d => d.DependsOnTask.Status != TaskStatus.Archived
            && (d.DependsOnTask.Status != TaskStatus.Completed
                || (!d.DependsOnTask.IsMerged
                    && !(d.Task.GroupId != null && d.Task.GroupId == d.DependsOnTask.GroupId)));

    /// <summary>DTO-level twin of <see cref="IsUnmet"/> for already-materialized items.</summary>
    public static bool IsUnmetItem(TaskDependencyItem d) =>
        d.Status != nameof(TaskStatus.Archived)
        && (d.Status != nameof(TaskStatus.Completed) || (!d.IsMerged && !d.SameGroup));

    /// <summary>Loads both directions of a task's dependency graph as fresh DTO lists.</summary>
    public static async Task<TaskDependenciesResponse> BuildResponseAsync(
        TasksDbContext db, Ulid taskId, string projectKey, CancellationToken ct)
    {
        var dependsOn = (await db.TaskDependencies
                .AsNoTracking()
                .Where(d => d.TaskId == taskId)
                .Select(d => new
                {
                    d.DependsOnTask.Id,
                    d.DependsOnTask.Number,
                    d.DependsOnTask.Title,
                    d.DependsOnTask.Status,
                    d.DependsOnTask.IsMerged,
                    SameGroup = d.Task.GroupId != null && d.Task.GroupId == d.DependsOnTask.GroupId,
                    d.Reason,
                    d.Source
                })
                .ToListAsync(ct))
            .Select(x => new TaskDependencyItem(
                x.Id, x.Number, $"{projectKey}-{x.Number}", x.Title,
                x.Status.ToString(), x.IsMerged, x.SameGroup, x.Reason, x.Source.ToString()))
            .ToList();

        var dependents = (await db.TaskDependencies
                .AsNoTracking()
                .Where(d => d.DependsOnTaskId == taskId)
                .Select(d => new
                {
                    d.Task.Id,
                    d.Task.Number,
                    d.Task.Title,
                    d.Task.Status,
                    d.Task.IsMerged,
                    SameGroup = d.Task.GroupId != null && d.Task.GroupId == d.DependsOnTask.GroupId,
                    d.Reason,
                    d.Source
                })
                .ToListAsync(ct))
            .Select(x => new TaskDependencyItem(
                x.Id, x.Number, $"{projectKey}-{x.Number}", x.Title,
                x.Status.ToString(), x.IsMerged, x.SameGroup, x.Reason, x.Source.ToString()))
            .ToList();

        return new TaskDependenciesResponse(dependsOn, dependents);
    }

    /// <summary>Derives the realtime event (unmet blockers only) from a fresh response.</summary>
    public static TaskDependenciesChangedEvent ToChangedEvent(Ulid taskId, TaskDependenciesResponse response) =>
        new(
            taskId,
            response.DependsOn
                .Where(IsUnmetItem)
                .Select(d => new BlockingTaskRef(d.TaskId, d.Code, d.Status, d.IsMerged))
                .ToList(),
            DateTimeOffset.UtcNow);
}
