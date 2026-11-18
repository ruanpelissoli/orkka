namespace Orkka.Modules.Agents.Features.Skills;

using System.Text.RegularExpressions;
using Orkka.Modules.Agents.Domain;

/// <summary>
/// A chat message that starts with <c>/slug</c> invokes that project skill explicitly (a
/// "playbook" run). The skill body is handed to the agent alongside the rest of the message;
/// the visible message keeps the raw <c>/slug …</c> text as the durable record.
/// </summary>
internal static partial class SkillInvocation
{
    [GeneratedRegex(@"^\s*/([a-z0-9]+(?:-[a-z0-9]+)*)(?=\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingSlashCommand();

    public sealed record Invoked(AgentSkill Skill, string Remainder);

    /// <summary>The invoked skill, or null when the message doesn't start with a slash
    /// command that names an enabled skill of this project. Any enabled project skill can be
    /// invoked explicitly — an explicit request outranks assignment.</summary>
    public static Invoked? TryParse(string content, IReadOnlyList<AgentSkill> projectSkills)
    {
        if (string.IsNullOrWhiteSpace(content) || projectSkills.Count == 0) return null;
        var match = LeadingSlashCommand().Match(content);
        if (!match.Success) return null;

        var slug = match.Groups[1].Value.ToLowerInvariant();
        var skill = projectSkills.FirstOrDefault(s => s.IsEnabled && s.Slug == slug);
        if (skill is null) return null;

        var remainder = content[match.Length..].Trim();
        return new Invoked(skill, remainder);
    }

    /// <summary>Platform-authored block placed before the user's message on the agent's copy.</summary>
    public static string Block(AgentSkill skill) =>
        $"""
        The user invoked the project skill "{skill.Name}" (/{skill.Slug}). Follow these instructions for this turn; the text after them is the user's request or arguments.
        <skill name="{skill.Slug}">
        {skill.Content.Trim()}
        </skill>


        """;

    /// <summary>Chat has no worktree to drop SKILL.md files into, so assigned skill bodies
    /// ride in the (stable, cache-friendly) system prompt up to this budget; past it a skill
    /// is listed by name and description only and its full body arrives on explicit
    /// <c>/slug</c> invocation.</summary>
    internal const int InlineBudgetChars = 6_000;

    /// <summary>The stable "## Skills" prompt section for chat turns.</summary>
    public static string PromptSection(IReadOnlyList<AgentSkill> assigned)
    {
        if (assigned.Count == 0) return "";
        var sb = new System.Text.StringBuilder();
        sb.Append("\n\n## Skills\n");
        sb.Append("Project playbooks assigned to you. When a request matches one, follow it; the user can also invoke one explicitly by starting a message with its /slug.\n");
        var used = 0;
        foreach (var skill in assigned)
        {
            var description = skill.Description.Length > 300 ? skill.Description[..300] + " […]" : skill.Description;
            sb.Append($"- **{skill.Name}** (`/{skill.Slug}`) — {description}\n");
            var body = skill.Content.Trim();
            if (body.Length == 0 || used + body.Length > InlineBudgetChars) continue;
            used += body.Length;
            sb.Append("  <skill>\n").Append(body).Append("\n  </skill>\n");
        }
        return sb.ToString();
    }
}
