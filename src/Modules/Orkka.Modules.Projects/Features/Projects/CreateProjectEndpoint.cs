namespace Orkka.Modules.Projects.Features.Projects;

using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.Modules.Projects.Infrastructure;
using FastEndpoints;

public sealed class CreateProjectEndpoint(
    ICurrentUser currentUser,
    IProjectIdentifierService projectIdentifiers,
    ProjectCreator creator)
    : Endpoint<CreateProjectRequest, ProjectResponse>
{
    public override void Configure()
    {
        Post("/api/projects");
    }

    public override async Task HandleAsync(CreateProjectRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.ProjectsCreate))
            return;

        if (req.Slug is not null && !await projectIdentifiers.IsSlugAvailableAsync(req.Slug, currentUser.OrgId, ct: ct))
            ThrowError(r => r.Slug!, "This slug is already in use.");
        if (req.Key is not null && !await projectIdentifiers.IsKeyAvailableAsync(req.Key, currentUser.OrgId, ct))
            ThrowError(r => r.Key!, "This key is already in use.");

        var entity = await creator.CreateAsync(
            new NewProject(currentUser.OrgId, currentUser.UserId, currentUser.Locale, req.Name),
            req.Description, req.Slug, req.Key, ct);

        await Send.CreatedAtAsync<GetProjectEndpoint>(
            new { id = entity.Id },
            entity.ToResponse(taskCount: 0),
            cancellation: ct);
    }
}
