namespace Orkka.Modules.Agents.Domain;

using Orkka.Modules.Agents.Contracts;
using Orkka.SharedKernel.Domain;

public sealed class ProjectAgent : TenantEntity
{
    // Cross-module references (Projects module) — plain ids, no navigations.
    public Ulid ProjectId { get; set; }

    // Repo scope: empty = works across all repos; otherwise the subset of repos this
    // agent handles. Stored as a JSON array of Guids (a primitive collection — the global
    // Ulid↔Guid converter doesn't apply to collections); convert at boundaries with
    // new Ulid(g) / u.ToGuid().
    public List<Guid> RepoIds { get; set; } = [];
    // Skills assigned to this agent (AgentSkill ids, same convention as RepoIds).
    // Empty = none. Only enabled skills are injected; deleting a skill scrubs it from every
    // agent in the same transaction.
    public List<Guid> SkillIds { get; set; } = [];
    public required string Name { get; set; }
    public AgentRole Role { get; set; }
    public string Avatar { get; set; } = "";
    public string Model { get; set; } = "claude-sonnet-5";
    // Retired: the image-generation provider pin from when Orkka generated raster design
    // assets server-side. Nothing reads or writes it any more; the column stays so existing
    // rows load without a data-dropping migration.
    public string? ImageProvider { get; set; }
    public string SystemPrompt { get; set; } = "";
    public string UserInstructions { get; set; } = "";
    // Seeded with the default pod — cannot be deleted. Agents are always active; users
    // simply use (or ignore) them.
    public bool IsDefault { get; set; }
    public long TotalTokensIn { get; set; }
    public long TotalTokensOut { get; set; }
    public decimal TotalCostUsd { get; set; }
    public int TasksCompleted { get; set; }
    public int TasksFailed { get; set; }

    // Navigation (intra-module)
    public ICollection<ProjectMemory> Memories { get; set; } = [];
    public ICollection<AgentConversation> Conversations { get; set; } = [];
    public ICollection<DesignAsset> DesignAssets { get; set; } = [];
    public ICollection<ArchitectureDiagram> Diagrams { get; set; } = [];
}
