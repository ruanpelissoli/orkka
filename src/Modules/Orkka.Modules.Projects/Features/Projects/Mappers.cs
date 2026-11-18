namespace Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.SharedKernel.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using System.Text.Json;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Projects.Domain;

internal static class Mappers
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static readonly ProjectSettingsDto DefaultSettings = new(
        MaxParallelTasks: 1,
        SameRepoParallelism: false,
        DefaultTokenLimit: 100000);

    public static string SerializeSettings(ProjectSettingsDto settings) =>
        JsonSerializer.Serialize(settings, JsonOptions);

    /// <summary>
    /// The settings a new project starts with: the edition defaults plus the creator's
    /// interface language as the project's working language. From here on the two are
    /// independent — switching the navbar language changes what the person reads, not what
    /// the agents write — and a project created with no usable locale pins English rather than
    /// following whoever happens to open it next.
    /// </summary>
    public static ProjectSettingsDto InitialSettings(ProjectSettingsDto defaults, string? creatorLocale) =>
        defaults with { Language = AgentLanguage.Normalize(creatorLocale) ?? AgentLanguage.Default };

    /// <summary>
    /// The language this project's agents write in (<see cref="ProjectLanguage.Resolve(ProjectSettingsDto?, string?)"/>
    /// over the entity's stored settings). <paramref name="requesterLocale"/> is the legacy
    /// answer for a project that has no language yet.
    /// </summary>
    public static string? ResolveAgentLanguage(this Project entity, string? requesterLocale = null) =>
        ProjectLanguage.Resolve(DeserializeSettings(entity.Settings), requesterLocale);

    /// <summary>The stored settings JSON as a DTO, defaults when there is none.</summary>
    public static ProjectSettingsDto DeserializeSettings(string? json) =>
        json is null
            ? DefaultSettings
            : JsonSerializer.Deserialize<ProjectSettingsDto>(json, JsonOptions) ?? DefaultSettings;

    public static ProjectResponse ToResponse(this Project entity, int? taskCount = null)
    {
        var settings = entity.Settings is not null
            ? JsonSerializer.Deserialize<ProjectSettingsDto>(entity.Settings, JsonOptions)!
            : DefaultSettings;

        return new ProjectResponse(
            entity.Id,
            entity.OrgId,
            entity.Name,
            entity.Slug,
            entity.Key,
            entity.Description,
            settings,
            entity.CreatedAt,
            entity.UpdatedAt,
            taskCount,
            entity.BaseFolder,
            ShapeName(entity.BaseFolderShape));
    }

    public static string? ShapeName(BaseFolderShape? shape) => shape switch
    {
        BaseFolderShape.Monorepo => "monorepo",
        BaseFolderShape.MultiRepo => "multirepo",
        _ => null,
    };
}
