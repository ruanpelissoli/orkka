namespace Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Projects.Infrastructure;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Security;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;

/// <summary>Updates repo settings — today just the default branch, which is the base
/// for task worktrees, diffs, and merge targets. Lets users point agents at a feature
/// branch instead of main.</summary>
public sealed class UpdateProjectRepoEndpoint(
    ProjectsDbContext db,
    ICurrentUser currentUser,
    IProjectResolver projectResolver)
    : Endpoint<UpdateProjectRepoRequest, ProjectRepoResponse>
{
    public override void Configure()
    {
        Patch("/api/projects/{projectId}/repos/{repoId:ulid}");
    }

    public override async Task HandleAsync(UpdateProjectRepoRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.ReposManage))
            return;

        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var repoId = Route<Ulid>("repoId");
        var repo = await db.ProjectRepos.FirstOrDefaultAsync(
            r => r.Id == repoId && r.ProjectId == projectId && r.OrgId == currentUser.OrgId, ct);
        if (repo is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // The runner fetches the branch from the remote when a task forks from it; a branch
        // that does not exist there fails that task with the reason.
        repo.DefaultBranch = req.DefaultBranch.Trim();

        // Null = "not sent, leave it alone"; blank = "clear it". Editing the setup command
        // changes its hash, so every existing worktree re-runs setup on its next phase.
        if (req.SetupCommand is not null)
        {
            var trimmed = req.SetupCommand.Trim();
            repo.SetupCommand = trimmed.Length == 0 ? null : trimmed;
        }

        if (req.QaRunCommand is not null)
        {
            var trimmed = req.QaRunCommand.Trim();
            repo.QaRunCommand = trimmed.Length == 0 ? null : trimmed;
        }

        if (req.BuildCommand is not null)
        {
            var trimmed = req.BuildCommand.Trim();
            repo.BuildCommand = trimmed.Length == 0 ? null : trimmed;
        }

        if (req.CheckCommand is not null)
        {
            var trimmed = req.CheckCommand.Trim();
            repo.CheckCommand = trimmed.Length == 0 ? null : trimmed;
        }

        if (req.Provider is not null)
            repo.ForgeKind = Mappers.ParseForge(req.Provider, repo.GitUrl);

        if (req.CliAccount is not null)
        {
            var trimmed = req.CliAccount.Trim();
            repo.CliAccount = trimmed.Length == 0 ? null : trimmed;
        }

        // A routing hint shown to agents next to the project's other repos.
        if (req.Description is not null)
        {
            var trimmed = req.Description.Trim();
            repo.Description = trimmed.Length == 0 ? null : trimmed;
        }

        await db.SaveChangesAsync(ct);

        await Send.OkAsync(repo.ToResponse(), ct);
    }
}
