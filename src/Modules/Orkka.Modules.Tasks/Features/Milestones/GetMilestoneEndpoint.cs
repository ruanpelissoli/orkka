namespace Orkka.Modules.Tasks.Features.Milestones;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;

/// <summary>
/// One milestone with the tasks under it — what the milestone detail page renders.
///
/// The roadmap list carries counts but no tasks, and the board carries tasks but the whole
/// project's worth. Without this, showing one milestone means fetching every task on the board
/// and filtering client-side. Reads are open to every org member, like every other GET.
/// </summary>
public sealed class GetMilestoneEndpoint(
    TasksDbContext db,
    IProjectResolver projectResolver,
    IProjectsApi projectsApi,
    ICurrentUser currentUser)
    : EndpointWithoutRequest<MilestoneDetailResponse>
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/milestones/{id:ulid}");
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

        var project = await projectsApi.GetProjectSummaryAsync(projectId, ct);
        if (project is null || project.OrgId != currentUser.OrgId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var id = Ulid.Parse(Route<string>("id"));

        var milestone = await db.Milestones
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == id && m.OrgId == currentUser.OrgId
                && m.ProjectId == projectId, ct);

        if (milestone is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var tasks = await db.Tasks
            .AsNoTracking()
            // Archived tasks included: this list is the milestone's contents, and its counts
            // (MilestoneQueries) count them too — the two must not disagree.
            .Where(t => t.MilestoneId == id && t.OrgId == currentUser.OrgId)
            .OrderBy(t => t.Number)
            .Select(t => new MilestoneTaskResponse(
                t.Id, t.Number, t.Title, t.CurrentLane.ToString(), t.Status.ToString(),
                t.Priority.ToString(), t.Type.ToString()))
            .ToListAsync(ct);

        var counts = (await MilestoneQueries.GetCountsByMilestoneAsync(
            db, currentUser.OrgId, projectId, ct)).For(id);

        var repoNames = milestone.RepoId is null
            ? null
            : (await projectsApi.ListReposAsync(projectId, ct)).ToDictionary(r => r.Id, r => r.Name);

        // The neighbours the detail page needs to say "the one before this isn't finished"
        // without fetching the whole roadmap alongside it.
        var previous = await db.Milestones
            .AsNoTracking()
            .Where(m => m.OrgId == currentUser.OrgId && m.ProjectId == projectId
                && m.Position < milestone.Position
                && m.Status != Contracts.MilestoneStatus.Completed
                && m.Status != Contracts.MilestoneStatus.Cancelled)
            .OrderByDescending(m => m.Position)
            .Select(m => new MilestoneNeighbourResponse(m.Id, m.Title, m.Status.ToString()))
            .FirstOrDefaultAsync(ct);

        await Send.OkAsync(new MilestoneDetailResponse(
            milestone.ToResponse(counts, repoNames), tasks, previous), ct);
    }
}
