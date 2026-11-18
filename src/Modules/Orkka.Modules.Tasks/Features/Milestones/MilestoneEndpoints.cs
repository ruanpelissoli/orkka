namespace Orkka.Modules.Tasks.Features.Milestones;

using FastEndpoints;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;

public sealed class CreateMilestoneValidator : Validator<CreateMilestoneRequest>
{
    public CreateMilestoneValidator()
    {
        RuleFor(x => x.Title)
            .NotEmpty().WithMessage("Title is required.")
            .MaximumLength(200).WithMessage("Title must be 200 characters or fewer.");
        RuleFor(x => x.Outcome)
            .MaximumLength(2000).WithMessage("Outcome must be 2000 characters or fewer.");
    }
}

public sealed class UpdateMilestoneValidator : Validator<UpdateMilestoneRequest>
{
    public UpdateMilestoneValidator()
    {
        RuleFor(x => x.Title)
            .MaximumLength(200).WithMessage("Title must be 200 characters or fewer.");
        RuleFor(x => x.Outcome)
            .MaximumLength(2000).WithMessage("Outcome must be 2000 characters or fewer.");
    }
}

/// <summary>
/// The roadmap. Reads are open to every org member, like every other GET.
/// </summary>
public sealed class ListMilestonesEndpoint(
    TasksDbContext db,
    IProjectResolver projectResolver,
    IProjectsApi projectsApi,
    ICurrentUser currentUser)
    : EndpointWithoutRequest<MilestoneListResponse>
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/milestones");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        // Null means the caller cannot reach this project. 404 rather than 403 — a 403 would
        // confirm the id exists.
        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // ResolveAsync passes a syntactically valid ULID through without checking it exists,
        // by contract — the endpoint owns that check.
        var project = await projectsApi.GetProjectSummaryAsync(projectId, ct);
        if (project is null || project.OrgId != currentUser.OrgId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var milestones = await db.Milestones
            .AsNoTracking()
            .Where(m => m.OrgId == currentUser.OrgId && m.ProjectId == projectId)
            .OrderBy(m => m.Position)
            .ThenBy(m => m.CreatedAt)
            .ToListAsync(ct);

        var counts = await MilestoneQueries.GetCountsByMilestoneAsync(
            db, currentUser.OrgId, projectId, ct);

        // Only fetched when some milestone is actually scoped — a single-repo project would
        // otherwise pay a repo query to render a label identical on every card.
        var repoNames = milestones.Any(m => m.RepoId is not null)
            ? (await projectsApi.ListReposAsync(projectId, ct)).ToDictionary(r => r.Id, r => r.Name)
            : null;

        await Send.OkAsync(new MilestoneListResponse(
            milestones.Select(m => m.ToResponse(counts.For(m.Id), repoNames)).ToList(),
            counts.Backlog), ct);
    }
}

public sealed class CreateMilestoneEndpoint(
    TasksDbContext db,
    IProjectResolver projectResolver,
    IProjectsApi projectsApi,
    ICurrentUser currentUser,
    ITaskNotifier taskNotifier)
    : Endpoint<CreateMilestoneRequest, MilestoneResponse>
{
    public override void Configure()
    {
        Post("/api/projects/{projectId}/milestones");
    }

    public override async Task HandleAsync(CreateMilestoneRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.TasksCreate))
            return;

        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Same reason as the list endpoint: without this, a well-formed but unknown project id
        // would happily accumulate milestones nobody can ever reach.
        var project = await projectsApi.GetProjectSummaryAsync(projectId, ct);
        if (project is null || project.OrgId != currentUser.OrgId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // A milestone pointed at another project's repo would plan against a tree its tasks
        // can never be created in.
        if (req.RepoId is { } requestedRepoId)
        {
            var repo = await projectsApi.GetRepoAsync(requestedRepoId, ct);
            if (repo is null || repo.ProjectId != projectId || repo.OrgId != currentUser.OrgId)
            {
                ThrowError("The specified repository does not belong to this project.");
                return;
            }
        }

        var milestone = new Milestone
        {
            OrgId = currentUser.OrgId,
            ProjectId = projectId,
            RepoId = req.RepoId,
            Title = req.Title.Trim(),
            Outcome = string.IsNullOrWhiteSpace(req.Outcome) ? null : req.Outcome.Trim(),
            Position = req.Position
                ?? await MilestoneQueries.NextPositionAsync(db, currentUser.OrgId, projectId, ct),
            CreatedByUserId = currentUser.UserId,
        };

        db.Milestones.Add(milestone);
        await db.SaveChangesAsync(ct);

        var response = milestone.ToResponse(MilestoneQueries.EmptyCounts);
        await taskNotifier.SendMilestoneChangedAsync(projectId,
            MilestoneNotifications.Changed(milestone.Id, response.Status, response.Counts));

        await Send.OkAsync(response, ct);
    }
}

public sealed class UpdateMilestoneEndpoint(
    TasksDbContext db,
    IProjectResolver projectResolver,
    IProjectsApi projectsApi,
    ICurrentUser currentUser,
    ITaskNotifier taskNotifier)
    : Endpoint<UpdateMilestoneRequest, MilestoneResponse>
{
    public override void Configure()
    {
        Patch("/api/projects/{projectId}/milestones/{id:ulid}");
    }

    public override async Task HandleAsync(UpdateMilestoneRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.TasksEdit))
            return;

        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var id = Ulid.Parse(Route<string>("id")!);
        var milestone = await db.Milestones
            .FirstOrDefaultAsync(m => m.Id == id && m.OrgId == currentUser.OrgId
                && m.ProjectId == projectId, ct);

        if (milestone is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (req.Title is not null) milestone.Title = req.Title.Trim();
        if (req.Outcome is not null)
            milestone.Outcome = string.IsNullOrWhiteSpace(req.Outcome) ? null : req.Outcome.Trim();
        if (req.Position is { } position) milestone.Position = position;

        // Empty string clears it back to cross-repo; omitted leaves it alone. Same convention
        // the task endpoint uses for milestone reassignment.
        if (req.RepoId is not null)
        {
            if (req.RepoId.Length == 0)
            {
                milestone.RepoId = null;
            }
            else
            {
                if (!Ulid.TryParse(req.RepoId, out var repoId))
                {
                    ThrowError("RepoId must be a valid id or an empty string to clear it.");
                    return;
                }

                var repo = await projectsApi.GetRepoAsync(repoId, ct);
                if (repo is null || repo.ProjectId != projectId || repo.OrgId != currentUser.OrgId)
                {
                    ThrowError("The specified repository does not belong to this project.");
                    return;
                }

                milestone.RepoId = repoId;
            }
        }

        if (req.Status is not null)
        {
            if (!Enum.TryParse<MilestoneStatus>(req.Status, ignoreCase: true, out var status))
            {
                ThrowError("Status must be one of: Planned, Active, Completed, Cancelled.");
                return;
            }

            milestone.Status = status;
            // Keep the stamps consistent with a manual override, so a hand-completed milestone
            // reads the same as one the auto-transition closed.
            milestone.StartedAt ??= status is MilestoneStatus.Active or MilestoneStatus.Completed
                ? DateTimeOffset.UtcNow
                : null;
            milestone.CompletedAt = status == MilestoneStatus.Completed
                ? milestone.CompletedAt ?? DateTimeOffset.UtcNow
                : null;
        }

        await db.SaveChangesAsync(ct);

        var counts = await MilestoneQueries.GetCountsByMilestoneAsync(
            db, currentUser.OrgId, projectId, ct);
        var repoNames = milestone.RepoId is null
            ? null
            : (await projectsApi.ListReposAsync(projectId, ct)).ToDictionary(r => r.Id, r => r.Name);
        var response = milestone.ToResponse(counts.For(milestone.Id), repoNames);

        await taskNotifier.SendMilestoneChangedAsync(projectId,
            MilestoneNotifications.Changed(milestone.Id, response.Status, response.Counts));

        await Send.OkAsync(response, ct);
    }
}

/// <summary>
/// Deleting a milestone detaches its tasks — it never deletes work. A milestone is a planning
/// artifact; the tasks under it may already be merged.
/// </summary>
public sealed class DeleteMilestoneEndpoint(
    TasksDbContext db,
    IProjectResolver projectResolver,
    ICurrentUser currentUser,
    ITaskNotifier taskNotifier)
    : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("/api/projects/{projectId}/milestones/{id:ulid}");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.TasksDelete))
            return;

        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var id = Ulid.Parse(Route<string>("id")!);
        var milestone = await db.Milestones
            .FirstOrDefaultAsync(m => m.Id == id && m.OrgId == currentUser.OrgId
                && m.ProjectId == projectId, ct);

        if (milestone is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Explicit rather than relying on the FK's SetNull: the tasks are already loaded by the
        // sweep below on some providers, and being obvious here beats being clever.
        await db.Tasks
            .Where(t => t.OrgId == currentUser.OrgId && t.MilestoneId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.MilestoneId, (Ulid?)null), ct);

        // Edges pointing AT this milestone are NoAction on the FK (two cascade paths into one
        // table is a foot-gun), so they are cleared here or the delete fails on the constraint.
        // The other leg cascades with the row.
        await db.MilestoneDependencies
            .Where(d => d.DependsOnMilestoneId == id)
            .ExecuteDeleteAsync(ct);

        db.Milestones.Remove(milestone);
        await db.SaveChangesAsync(ct);

        await taskNotifier.SendMilestoneChangedAsync(projectId,
            MilestoneNotifications.Deleted(id));

        await Send.NoContentAsync(ct);
    }
}
