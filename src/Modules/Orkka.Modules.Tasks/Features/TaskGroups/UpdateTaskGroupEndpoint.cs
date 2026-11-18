namespace Orkka.Modules.Tasks.Features.TaskGroups;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;

/// <summary>
/// Corrects a group's contract by hand.
///
/// The split writes the contract from repo trees and a description, so it can be wrong in ways
/// only a person knows about. Since every half is coded against it — and told it is binding —
/// a wrong contract produces two halves that agree with each other and not with reality, which
/// is worse than no contract at all. This is the escape hatch.
///
/// Editing marks the contract Human, which stops later "as built" appends from a re-run
/// overwriting it.
/// </summary>
public sealed class UpdateTaskGroupEndpoint(
    TasksDbContext db,
    IProjectResolver projectResolver,
    IProjectsApi projectsApi,
    ICurrentUser currentUser)
    : Endpoint<UpdateTaskGroupRequest>
{
    public override void Configure()
    {
        Patch("/api/projects/{projectId}/task-groups/{id:ulid}");
    }

    public override async Task HandleAsync(UpdateTaskGroupRequest req, CancellationToken ct)
    {
        // Editing the contract steers what every member's agent writes next, so it is a task
        // edit in everything but name and takes the same permission.
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.TasksEdit))
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

        var groupId = Route<Ulid>("id");

        var group = await db.TaskGroups.FirstOrDefaultAsync(
            g => g.Id == groupId && g.ProjectId == projectId && g.OrgId == currentUser.OrgId, ct);

        if (group is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Once the work has landed the contract is a historical record of what the halves were
        // built against. Rewriting it then would misreport what shipped.
        if (group.Status is TaskGroupStatus.Merged or TaskGroupStatus.Cancelled)
        {
            ThrowError("This cross-repo task has already settled, so its contract can't be changed.");
            return;
        }

        var contract = req.Contract?.Trim();
        group.Contract = string.IsNullOrWhiteSpace(contract) ? null : contract;
        group.ContractSource = ContractSource.Human;

        await db.SaveChangesAsync(ct);

        await Send.NoContentAsync(ct);
    }
}
