namespace Orkka.Modules.Projects.Features.ProjectRepos;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Projects.Infrastructure;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Utilities;
using Orkka.SharedKernel.Security;

/// <param name="Command">Ready-to-run `git clone …`, the whole point of the endpoint.</param>
/// <param name="Tokenized">Always false: the developer authenticates with their own git
/// credentials, and the URL is safe to keep around. Kept in the contract for older clients.</param>
/// <param name="ExpiresAt">Always null (nothing embedded expires).</param>
public sealed record RepoCloneAccessResponse(
    string Command,
    string CloneUrl,
    string DefaultBranch,
    bool Tokenized,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// Hands a member the command to clone a project repo onto their own machine. Every repo
/// belongs to the customer's own forge, so the plain URL is returned and the developer's
/// existing git credentials do the work — Orkka never mints or hands out a credential.
///
/// Gated on <see cref="Permission.ReposManage"/> like the other repo writes, so the answer
/// matches what the caller may already do with the repo.
/// </summary>
public sealed class GetRepoCloneAccessEndpoint(
    ProjectsDbContext db,
    ICurrentUser currentUser,
    IProjectResolver projectResolver)
    : EndpointWithoutRequest<RepoCloneAccessResponse>
{
    public override void Configure()
    {
        Post("/api/projects/{projectId}/repos/{repoId:ulid}/clone-access");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.ReposManage))
            return;

        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var repoId = Ulid.Parse(Route<string>("repoId"));
        var repo = await db.ProjectRepos
            .FirstOrDefaultAsync(
                r => r.Id == repoId && r.ProjectId == projectId && r.OrgId == currentUser.OrgId, ct);

        if (repo is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var branch = string.IsNullOrWhiteSpace(repo.DefaultBranch) ? "main" : repo.DefaultBranch;

        // A linked local folder: "repos" are directories on the machine the user already has.
        if (repo.Location == RepoLocation.LocalFolder)
        {
            ThrowError("This repository is a local folder — open it directly instead of cloning.");
            return;
        }

        await Send.OkAsync(new RepoCloneAccessResponse(
            $"git clone {repo.GitUrl}",
            repo.GitUrl,
            branch,
            Tokenized: false,
            ExpiresAt: null), ct);
    }
}
