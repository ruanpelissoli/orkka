namespace Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.SharedKernel.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Orkka.Modules.Projects.Domain;
using Orkka.Modules.Projects.Contracts;

internal static class Mappers
{
    private static readonly string[] CardColors =
        ["#4F46E5", "#7C3AED", "#DB2777", "#DC2626", "#EA580C",
         "#D97706", "#65A30D", "#059669", "#0891B2", "#2563EB"];

    public static string RandomCardColor() =>
        CardColors[Random.Shared.Next(CardColors.Length)];

    public static ProjectRepoResponse ToResponse(this ProjectRepo entity) => new(
        entity.Id,
        entity.ProjectId,
        entity.Name,
        entity.GitUrl,
        entity.DefaultBranch,
        MapProvider(entity),
        MapWorkspaceStatus(entity.WorkspaceStatus),
        entity.CardColor,
        entity.SetupCommand,
        entity.QaRunCommand,
        entity.BuildCommand,
        entity.CheckCommand,
        entity.CreatedAt,
        entity.CliAccount,
        entity.FolderPath,
        entity.Description,
        entity.MissingSince,
        entity.Location == RepoLocation.LocalFolder ? "local" : "remote");

    /// <summary>The forge named in a request, or the one the URL's host says when none (or
    /// an unknown name) is given.</summary>
    public static ForgeKind ParseForge(string? provider, string gitUrl) => provider?.ToLowerInvariant() switch
    {
        "github" => ForgeKind.GitHub,
        "azuredevops" or "azure-devops" or "ado" => ForgeKind.AzureDevOps,
        "gitlab" => ForgeKind.GitLab,
        "bitbucket" => ForgeKind.Bitbucket,
        "other" => ForgeKind.Other,
        _ => RepoUrl.DetectForge(gitUrl),
    };

    // A folder on someone's machine is not a forge, and the UI has to tell them apart: it
    // decides whether to offer a PR link or the folder's own sync controls.
    private static string MapProvider(ProjectRepo repo) => repo.Location == RepoLocation.LocalFolder
        ? "local"
        : repo.ForgeKind.ToString().ToLowerInvariant();

    private static string MapWorkspaceStatus(WorkspaceStatus status) => status switch
    {
        WorkspaceStatus.Cloning => "cloning",
        WorkspaceStatus.Error => "error",
        _ => "active"
    };
}
