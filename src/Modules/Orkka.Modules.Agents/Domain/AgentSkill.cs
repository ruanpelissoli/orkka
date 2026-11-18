namespace Orkka.Modules.Agents.Domain;

using Orkka.SharedKernel.Domain;

/// <summary>
/// A reusable, project-scoped instruction package (a "skill" / playbook) in the SKILL.md
/// spirit: a short description the agent sees on every run, and a markdown body it reads
/// only when the work matches. Assigned to agents via <see cref="ProjectAgent.SkillIds"/>;
/// invoked explicitly in chat with <c>/slug</c>.
///
/// Built-in skills are seeded per project (like the agent roster) and can be edited or
/// disabled but not deleted — a deleted built-in would be re-seeded on the next backfill.
/// </summary>
public sealed class AgentSkill : TenantEntity
{
    public Ulid ProjectId { get; set; }

    /// <summary>URL/command-safe identifier, unique per project: <c>[a-z0-9-]{1,64}</c>.
    /// Also the folder name the skill is written under in a worktree.</summary>
    public required string Slug { get; set; }
    public required string Name { get; set; }

    /// <summary>One or two sentences shown to the agent on every run so it knows when to
    /// open the skill. Keep it short — it rides in the (command-line constrained) prompt.</summary>
    public string Description { get; set; } = "";

    /// <summary>The SKILL.md body (markdown). Read by the agent on demand.</summary>
    public string Content { get; set; } = "";

    public bool IsEnabled { get; set; } = true;

    /// <summary>Seeded starter skill. Editable and disable-able, never deletable.</summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>Stable key of the built-in this row was seeded from (null for custom
    /// skills) — the backfill uses it to top existing projects up without re-adding one the
    /// user renamed.</summary>
    public string? BuiltInKey { get; set; }

    public string? CreatedByUserId { get; set; }
    public string? UpdatedByUserId { get; set; }
}
