namespace Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.SharedKernel.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Orkka.Modules.Projects.Contracts;

/// <param name="Provider">Forge override: github, azuredevops, gitlab, bitbucket or other. Null
/// detects it from the URL's host.</param>
/// <param name="CliAccount">The forge CLI account to act as; null uses the active one.</param>
public sealed record AddProjectRepoRequest(
    string Name,
    string GitUrl,
    string DefaultBranch,
    string? Provider,
    string? CliAccount = null);

/// <param name="SetupCommand">Null leaves the stored command untouched; an empty/whitespace
/// string clears it. Lets the branch-only callers stay unchanged. Same for
/// <paramref name="Provider"/> (a forge override) and <paramref name="CliAccount"/>.</param>
public sealed record UpdateProjectRepoRequest(
    string DefaultBranch, string? SetupCommand = null, string? QaRunCommand = null,
    string? BuildCommand = null, string? CheckCommand = null,
    string? Provider = null, string? CliAccount = null,
    string? Description = null);

public sealed record ProjectRepoResponse(
    Ulid Id,
    Ulid ProjectId,
    string Name,
    string GitUrl,
    string DefaultBranch,
    // "local" for a folder, else the forge: github, azuredevops, gitlab, bitbucket, other.
    string Provider,
    string WorkspaceStatus,
    string CardColor,
    string? SetupCommand,
    string? QaRunCommand,
    string? BuildCommand,
    string? CheckCommand,
    DateTimeOffset CreatedAt,
    string? CliAccount = null,
    // Base-folder repos: the path inside the project's base folder ("" = the base folder is
    // the repo), the routing description, when a rescan last missed it, and "remote" | "local".
    string? FolderPath = null,
    string? Description = null,
    DateTimeOffset? MissingSince = null,
    string Location = "remote");
