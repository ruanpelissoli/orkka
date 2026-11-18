namespace Orkka.Modules.Agents.Domain;

using Orkka.Modules.Agents.Contracts;

/// <summary>Curated identity for a singleton specialist agent (one instance per project,
/// enforced by the filtered unique index on (ProjectId, Role)).</summary>
public sealed record SpecialistAgentSpec(AgentRole Role, string Name, string Avatar);

internal static class AgentSeeder
{
    /// <summary>
    /// Specialist roles seeded with every project. They are advisory or
    /// niche (assignment ordering keeps GameDev last among dev fallbacks), always present,
    /// and exactly one per project. Also the roster the startup backfill
    /// (AgentRosterBackfillService) tops existing projects up with.
    /// </summary>
    public static readonly IReadOnlyList<SpecialistAgentSpec> Specialists =
    [
        new(AgentRole.GameDev, "Rune", "rune"),
        new(AgentRole.GameDesigner, "Sprite", "sprite"),
        new(AgentRole.DBA, "Sage", "sage"),
        new(AgentRole.Legal, "Lex", "lex"),
        new(AgentRole.MobileDev, "Nomad", "nomad"),
    ];

    /// <summary>
    /// Creates the full agent pod for a project. Every available agent is seeded up front —
    /// there is no add/enable flow; users simply use (or ignore) the agents they need.
    /// </summary>
    public static List<ProjectAgent> CreateDefaultPod(
        Ulid orgId, Ulid projectId,
        ModelCatalogSnapshot catalog,
        AiProvider provider = AiProvider.Anthropic)
    {
        var agents = new List<ProjectAgent>();

        // Model tiers follow the seeding provider (a codex-only or gemini-only user gets
        // a pod that can actually run). QA is the exception below: browser (MCP) testing
        // only works on Claude, so Scout is always seeded with a Claude model.
        var fast = catalog.FastModel(provider);
        var capable = catalog.CapableModel(provider);

        // Always: PM
        agents.Add(Create(orgId, projectId, "Luna", AgentRole.PM, "luna", fast));

        // Always: Designer
        agents.Add(Create(orgId, projectId, "Muse", AgentRole.Designer, "muse", capable));

        // Always: FullStackDev
        agents.Add(Create(orgId, projectId, "Nova", AgentRole.FullStackDev, "nova", capable));

        // Always: Solution Architect
        agents.Add(Create(orgId, projectId, "Archie", AgentRole.SolutionArchitect, "archie", capable));

        agents.Add(Create(orgId, projectId, "Pixel", AgentRole.FrontendDev, "pixel", capable));
        agents.Add(Create(orgId, projectId, "Atlas", AgentRole.BackendDev, "atlas", capable));
        agents.Add(Create(orgId, projectId, "Forge", AgentRole.DevOps, "forge", capable));

        // Code reviewer — seeded by default (the CodeReview lane is on unless a task skips it)
        agents.Add(Create(orgId, projectId, "Sentinel", AgentRole.CodeReviewer, "sentinel", capable));

        // QA — seed by default (the Testing lane activates when the project enables QA).
        // Always Claude: only MCP-capable models can drive Playwright browser testing.
        agents.Add(Create(orgId, projectId, "Scout", AgentRole.QA, "scout", catalog.QaModel));

        // Singleton specialists (game dev/designer, DBA, Legal, mobile dev) — always present.
        foreach (var spec in Specialists)
            agents.Add(Create(orgId, projectId, spec.Name, spec.Role, spec.Avatar, capable));

        return agents;
    }

    /// <summary>
    /// The specialist agents a project is missing — used to top existing projects up when
    /// the always-seeded roster grows (e.g. Legal).
    /// </summary>
    public static List<ProjectAgent> CreateMissingSpecialists(
        Ulid orgId, Ulid projectId, IReadOnlyCollection<AgentRole> existingRoles,
        AiProvider provider, ModelCatalogSnapshot catalog)
    {
        var capable = catalog.CapableModel(provider);

        return Specialists
            .Where(s => !existingRoles.Contains(s.Role))
            .Select(s => Create(orgId, projectId, s.Name, s.Role, s.Avatar, capable))
            .ToList();
    }

    public static ProjectAgent Create(
        Ulid orgId, Ulid projectId, string name, AgentRole role,
        string avatar, string model) => new()
    {
        Id = Ulid.NewUlid(),
        OrgId = orgId,
        ProjectId = projectId,
        Name = name,
        Role = role,
        Avatar = avatar,
        Model = model,
        SystemPrompt = DefaultPrompts.ForRole(role),
        IsDefault = true
    };
}
