namespace Orkka.Modules.Projects.Infrastructure;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.SharedKernel.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

public interface IProjectIdentifierService
{
    /// <summary>Generates an org-unique slug and key for a new project. Requested values,
    /// when provided, are used as the base; collisions get a numeric suffix.</summary>
    Task<(string Slug, string Key)> GenerateAsync(
        string name,
        string? requestedSlug,
        string? requestedKey,
        Ulid orgId,
        CancellationToken ct = default);

    Task<bool> IsSlugAvailableAsync(string slug, Ulid orgId, Ulid? excludeProjectId = null, CancellationToken ct = default);

    Task<bool> IsKeyAvailableAsync(string key, Ulid orgId, CancellationToken ct = default);
}
