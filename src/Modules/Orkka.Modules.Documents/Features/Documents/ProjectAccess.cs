namespace Orkka.Modules.Documents.Features.Documents;

using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;

/// <summary>Resolves the {projectId} route value (slug or ULID) and verifies org ownership.
/// Returns null when the project doesn't exist or belongs to another org (caller sends 404).</summary>
public static class ProjectAccess
{
    public static async Task<Ulid?> ResolveOwnedProjectAsync(
        IProjectResolver projectResolver, IProjectsApi projectsApi, ICurrentUser currentUser,
        string idOrSlug, CancellationToken ct)
    {
        var resolved = await projectResolver.ResolveAsync(idOrSlug, ct);
        if (resolved is not { } projectId)
            return null;

        var project = await projectsApi.GetProjectSummaryAsync(projectId, ct);
        return project is null || project.OrgId != currentUser.OrgId ? null : projectId;
    }
}
