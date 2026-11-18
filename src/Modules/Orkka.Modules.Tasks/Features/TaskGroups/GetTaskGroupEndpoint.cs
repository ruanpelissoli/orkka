namespace Orkka.Modules.Tasks.Features.TaskGroups;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;

/// <summary>
/// One cross-repo group: the original request, the contract its halves code against, and where
/// each half has got to.
///
/// The board cannot answer this. A group's members are ordinary tasks scattered across lanes and
/// repos, and the two things that make them one piece of work — the shared contract and the merge
/// gate — live on neither of them. Without this view, "why has nothing merged yet" has no answer
/// anywhere in the product.
/// </summary>
public sealed class GetTaskGroupEndpoint(
    TasksDbContext db,
    IProjectResolver projectResolver,
    IProjectsApi projectsApi,
    ITasksApi tasksApi,
    ICurrentUser currentUser)
    : EndpointWithoutRequest<TaskGroupResponse>
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/task-groups/{id:ulid}");
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

        var groupId = Route<Ulid>("id");

        var group = await db.TaskGroups
            .AsNoTracking()
            .Include(g => g.Branches)
            .FirstOrDefaultAsync(
                g => g.Id == groupId && g.ProjectId == projectId && g.OrgId == currentUser.OrgId, ct);

        if (group is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var repos = (await projectsApi.ListReposAsync(projectId, ct))
            .ToDictionary(r => r.Id);

        var members = await db.Tasks
            .AsNoTracking()
            .Where(t => t.GroupId == groupId)
            .OrderBy(t => t.StoryOrder)
            .ThenBy(t => t.Number)
            .Select(t => new
            {
                t.Id,
                t.Number,
                t.Title,
                t.RepoId,
                t.CurrentLane,
                t.Status,
                t.PrUrl,
                t.IsMerged,
                t.PrClosedAt,
                t.MergeFailureReason,
                t.StoryOrder,
                t.ExternalKey,
            })
            .ToListAsync(ct);

        // The gate's own answer, not a re-derivation. Two implementations of "is this ready"
        // would drift, and the one the user reads would be the wrong one.
        var gate = (await tasksApi.GetGroupMergeStatesAsync([groupId], ct)).FirstOrDefault();

        await Send.OkAsync(new TaskGroupResponse(
            group.Id,
            group.Title,
            group.Request,
            group.Contract,
            group.ContractSource.ToString(),
            group.MergePolicy.ToString(),
            group.Status.ToString(),
            group.MilestoneId,
            gate?.Ready ?? false,
            gate?.BlockedBy,
            group.SettledAt,
            group.CreatedAt,
            [.. members.Select(m => new TaskGroupMemberResponse(
                m.Id,
                m.Number,
                $"{project.Key}-{m.Number}",
                m.Title,
                m.RepoId,
                m.RepoId is { } repoId && repos.TryGetValue(repoId, out var repo) ? repo.Name : null,
                m.RepoId is { } colorRepoId && repos.TryGetValue(colorRepoId, out var colorRepo)
                    ? colorRepo.CardColor
                    : null,
                m.CurrentLane.ToString(),
                m.Status.ToString(),
                m.PrUrl,
                m.IsMerged,
                m.PrClosedAt is not null,
                m.MergeFailureReason,
                m.StoryOrder,
                m.ExternalKey))],
            [.. Domain.RepoRequests.Parse(group.RepoRequestsJson)
                .Select(r => new RepoRequestResponse(
                    r.RepoName, r.Reason, r.Status, r.RequestedAt))],
            group.Kind.ToString(),
            group.BranchName,
            group.ExternalKey,
            group.ExternalUrl,
            group.ReadyAt,
            [.. group.Branches.OrderBy(b => b.CreatedAt).Select(b => new TaskGroupBranchResponse(
                b.RepoId,
                repos.TryGetValue(b.RepoId, out var branchRepo) ? branchRepo.Name : null,
                b.BranchName,
                b.ForgePrUrl,
                b.ForgeState is "merged" or "closed" ? b.ForgeState
                    : b.ForgePrUrl is null ? (b.ForgeState == "merged" ? "merged" : "none")
                    : b.ForgePrDraft ? "draft" : "ready",
                b.ReadyAt))]), ct);
    }
}
