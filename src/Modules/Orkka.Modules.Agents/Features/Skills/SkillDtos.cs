namespace Orkka.Modules.Agents.Features.Skills;

using System.Text.RegularExpressions;
using Orkka.Modules.Agents.Domain;

public sealed record SkillResponse(
    Ulid Id,
    Ulid ProjectId,
    string Slug,
    string Name,
    string Description,
    string Content,
    bool IsEnabled,
    bool IsBuiltIn,
    /// <summary>Agents in this project that have the skill assigned.</summary>
    IReadOnlyList<Ulid> AssignedAgentIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateSkillRequest(
    string Name,
    string Description,
    string Content,
    /// <summary>Optional; derived from <see cref="Name"/> when omitted.</summary>
    string? Slug = null,
    bool IsEnabled = true,
    /// <summary>Agents to assign the new skill to right away.</summary>
    List<string>? AgentIds = null);

public sealed record UpdateSkillRequest(
    string? Name = null,
    string? Description = null,
    string? Content = null,
    string? Slug = null,
    bool? IsEnabled = null);

public sealed record SetAgentSkillsRequest(List<string> SkillIds);

internal static partial class SkillRules
{
    public const int NameMax = 120;
    public const int DescriptionMax = 400;
    public const int ContentMax = 32_000;
    public const int SlugMax = 64;

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();

    public static bool IsValidSlug(string slug) =>
        slug.Length is > 0 and <= SlugMax && SlugPattern().IsMatch(slug);

    /// <summary>"Write release notes!" → "write-release-notes".</summary>
    public static string SlugFromName(string name)
    {
        var chars = name.Trim().ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')
            .ToArray();
        var collapsed = Regex.Replace(new string(chars), "-{2,}", "-").Trim('-');
        if (collapsed.Length > SlugMax) collapsed = collapsed[..SlugMax].TrimEnd('-');
        return collapsed.Length == 0 ? "skill" : collapsed;
    }

    public static SkillResponse ToResponse(this AgentSkill s, IReadOnlyList<Ulid> assignedAgentIds) =>
        new(s.Id, s.ProjectId, s.Slug, s.Name, s.Description, s.Content, s.IsEnabled, s.IsBuiltIn,
            assignedAgentIds, s.CreatedAt, s.UpdatedAt);
}
