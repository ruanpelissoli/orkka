namespace Orkka.Modules.Projects.Features.Projects;

using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Projects.Domain;
using Orkka.Modules.Projects.Infrastructure;
using Orkka.SharedKernel.Events.Catalog;
using IEventBus = Orkka.SharedKernel.Events.IEventBus;

/// <summary>
/// The one way a project comes into existence — <c>POST /api/projects</c> and
/// <c>POST /api/projects/from-folder</c> both go through here, so the identifiers, the default
/// settings and the <c>ProjectCreated</c> event (which seeds the agent roster) can never differ
/// between them.
/// </summary>
public sealed class ProjectCreator(
    ProjectsDbContext db,
    IProjectIdentifierService projectIdentifiers,
    IEventBus eventBus)
{
    public async Task<Project> CreateAsync(
        NewProject spec, string? description, string? slug, string? key, CancellationToken ct)
    {
        var (finalSlug, finalKey) = await projectIdentifiers.GenerateAsync(
            spec.Name, slug, key, spec.OrgId, ct);

        var hasBaseFolder = !string.IsNullOrWhiteSpace(spec.BaseFolder);
        var entity = new Project
        {
            OrgId = spec.OrgId,
            Name = spec.Name,
            Slug = finalSlug,
            Key = finalKey,
            Description = description,
            CreatedByUserId = spec.CreatedByUserId,
            Settings = Mappers.SerializeSettings(Mappers.InitialSettings(Mappers.DefaultSettings, spec.Locale)),
            BaseFolder = hasBaseFolder ? spec.BaseFolder!.Trim() : null,
            BaseFolderShape = hasBaseFolder ? spec.BaseFolderShape : null,
        };

        db.Projects.Add(entity);
        await db.SaveChangesAsync(ct);

        // Agent seeding happens out-of-module: the Agents module reacts to ProjectCreated and
        // seeds the default pod.
        await eventBus.PublishAsync(new ProjectCreated(
            entity.Id, entity.OrgId, entity.CreatedByUserId), ct);

        return entity;
    }
}
