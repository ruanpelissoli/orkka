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

using Orkka.Modules.Tasks.Features.Tasks;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;

public sealed class SubmitTaskEndpoint(
    TasksDbContext db,
    Orkka.Modules.Projects.Contracts.IProjectsApi projectsApi,
    ICurrentUser currentUser,
    IProviderReadiness providerReadiness,
    Infrastructure.Services.TaskSubmissionService submissions)
    : EndpointWithoutRequest<TaskResponse>
{
    public override void Configure()
    {
        Post("/api/tasks/{id:ulid}/submit");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.TasksExecute))
            return;

        var id = Ulid.Parse(Route<string>("id"));

        var entity = await db.Tasks
            .FirstOrDefaultAsync(t => t.Id == id && t.OrgId == currentUser.OrgId, ct);

        if (entity is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (entity.CurrentLane != Lane.Drafting)
        {
            ThrowError("Only tasks in Drafting can be submitted.");
            return;
        }

        // Pre-flight: submitting with no runnable AI provider used to fail silently at
        // execution time — surface it here where the user can act on it. Hosted: the
        // always-ready default makes this a no-op.
        if (providerReadiness.GetAnyProviderBlocker() is { } providerBlocker)
        {
            ThrowError(providerBlocker);
            return;
        }

        if (entity.RepoId is null)
        {
            ThrowError("A repository must be assigned before submitting.");
            return;
        }

        // The transition itself, plus the dependency gate, queue position, milestone start,
        // board broadcast and background edge analysis. Shared with autopilot, which performs
        // exactly the same submission from the Worker with no human present.
        await submissions.SubmitAsync(entity, currentUser.Locale, ct);

        var project = await projectsApi.GetProjectSummaryAsync(entity.ProjectId, ct);
        var repo = entity.RepoId is { } repoId ? await projectsApi.GetRepoAsync(repoId, ct) : null;
        await Send.OkAsync(entity.ToResponse(project!.Key, repo), ct);
    }
}
