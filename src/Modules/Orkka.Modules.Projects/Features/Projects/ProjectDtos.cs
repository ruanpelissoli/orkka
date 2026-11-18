namespace Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.SharedKernel.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Orkka.Modules.Projects.Contracts;

public sealed record CreateProjectRequest(
    string Name,
    string? Description,
    string? Slug = null,
    string? Key = null);

public sealed record UpdateProjectRequest(
    string? Name,
    string? Description,
    string? Slug = null,
    ProjectSettingsDto? Settings = null);

public sealed record ProjectResponse(
    Ulid Id,
    Ulid OrgId,
    string Name,
    string Slug,
    string Key,
    string? Description,
    ProjectSettingsDto Settings,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int? TaskCount,
    // The folder on the developer's machine the project was created from, and its shape
    // ("monorepo" | "multirepo"). Null for projects whose repos were linked one by one.
    string? BaseFolder = null,
    string? BaseFolderShape = null);
