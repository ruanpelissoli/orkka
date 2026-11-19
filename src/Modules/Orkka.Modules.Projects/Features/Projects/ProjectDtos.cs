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
    string? BaseFolderShape = null,
    // The guided setup: when Start was pressed (null = setup not finished, so the ticket
    // source is not polled yet) and the steps confirmed so far (ProjectSetupSteps).
    DateTimeOffset? SetupCompletedAt = null,
    string[]? SetupSteps = null);

/// <param name="Step">One of <see cref="ProjectSetupSteps.Confirmable"/>.</param>
/// <param name="Done">False takes the confirmation back.</param>
public sealed record ProjectSetupStepRequest(string Step, bool Done = true);

/// <param name="Warnings">What the checklist still flagged when the developer chose "Start
/// anyway" — recorded in the audit trail, never enforced here (the checks live on the
/// developer's machine).</param>
public sealed record StartProjectSetupRequest(string[]? Warnings = null);
