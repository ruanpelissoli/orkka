namespace Orkka.Modules.Organizations.Domain;

using Orkka.SharedKernel.Domain;

public sealed class Organization : BaseEntity
{
    /// <summary>The name an auto-provisioned workspace gets until it is renamed.</summary>
    public const string DefaultName = "Personal";

    public required string Name { get; set; }
    public required string Slug { get; set; }

    /// <summary>The workspace's AI provider choice: which vendor's CLI new projects' agents are
    /// seeded on and system-picked work prefers (a <c>WorkspaceAiProviders</c> key —
    /// "claude", "codex", "copilot", "gemini", "grok"). Null = Automatic: Claude when it can
    /// run, else the first signed-in provider. A developer whose employer pays for Copilot
    /// says so here.</summary>
    public string? AiProvider { get; set; }

    // Navigation (module-internal only; Projects live in the Projects module and
    // reference organizations by OrgId)
    public ICollection<OrgMember> Members { get; set; } = [];
}
