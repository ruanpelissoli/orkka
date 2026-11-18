namespace Orkka.Modules.Tasks.Infrastructure.Services;

using Orkka.Modules.Agents.Contracts;

/// <summary>
/// Which side of an API contract a repo's work sits on.
///
/// Named for the relationship rather than for "server" and "UI": what matters is that one side
/// DEFINES an interface the other side CALLS, and a client cannot be written against a contract
/// that does not exist yet. That is true of a Next.js app calling a Go service, and equally true
/// of a mobile app calling that same service.
/// </summary>
public enum WorkLayer
{
    /// <summary>No usable signal — a full-stack repo, an unassigned one, or a project where
    /// the distinction is meaningless. Never participates in layer ordering.</summary>
    Unknown,
    /// <summary>Defines the endpoints, schema, or contract. Runs first.</summary>
    ApiProvider,
    /// <summary>Consumes them. Cannot be implemented against an interface that isn't written.</summary>
    ApiConsumer,
}

/// <summary>
/// Maps a project's repos onto <see cref="WorkLayer"/> using the dev agents assigned to them.
///
/// The agent roster is the right signal because it is already the platform's own answer to
/// "what kind of code lives here" — seeded from stack detection, and correctable by the user on
/// the team page. Reading it beats re-deriving the stack, and beats guessing from task titles,
/// which is how "Add user avatar endpoint" gets filed as frontend work.
/// </summary>
public sealed class RepoLayerMap
{
    private readonly Dictionary<Ulid, WorkLayer> _byRepo;

    private RepoLayerMap(Dictionary<Ulid, WorkLayer> byRepo) => _byRepo = byRepo;

    /// <summary>
    /// False when layering cannot say anything useful: a single-repo project, a managed one
    /// where Nova writes both sides of a feature in one task, or a roster with no dev agents.
    /// Callers must skip layer ordering entirely rather than apply a half-informed guess.
    /// </summary>
    public bool IsLayered { get; private init; }

    public WorkLayer LayerOf(Ulid? repoId) =>
        repoId is { } id && _byRepo.TryGetValue(id, out var layer) ? layer : WorkLayer.Unknown;

    public static async Task<RepoLayerMap> BuildAsync(
        IAgentsApi agentsApi, Ulid projectId, CancellationToken ct = default)
    {
        var agents = await agentsApi.ListAgentsAsync(projectId, ct);
        var byRepo = new Dictionary<Ulid, WorkLayer>();

        foreach (var agent in agents)
        {
            // Cross-repo agents (PM, QA, Reviewer, Architect) say nothing about a repo's layer.
            if (agent.RepoIds.Count == 0)
                continue;

            var layer = LayerOfRole(agent.Role);
            if (layer == WorkLayer.Unknown)
                continue;

            foreach (var repoId in agent.RepoIds)
            {
                // A repo that a full-stack agent also serves is genuinely both sides, so a
                // conflicting pair collapses to Unknown rather than to whichever agent the
                // roster happened to list first.
                byRepo[repoId] = byRepo.TryGetValue(repoId, out var existing) && existing != layer
                    ? WorkLayer.Unknown
                    : layer;
            }
        }

        // Layering only means something when both sides are actually present. One repo, or all
        // repos on the same side, and every edge it would add is noise that serialises work
        // which could have run at once.
        var layered = byRepo.Values.Any(l => l == WorkLayer.ApiProvider)
            && byRepo.Values.Any(l => l == WorkLayer.ApiConsumer);

        return new RepoLayerMap(byRepo) { IsLayered = layered };
    }

    /// <summary>
    /// FullStackDev is deliberately Unknown: on a managed project Nova writes the API route and
    /// the screen that calls it in the same task, so there is no ordering to impose and forcing
    /// one would serialise a single change against itself. GameDev likewise — a gameplay repo
    /// is not API-layered.
    /// </summary>
    private static WorkLayer LayerOfRole(AgentRole role) => role switch
    {
        AgentRole.BackendDev or AgentRole.DevOps => WorkLayer.ApiProvider,
        AgentRole.FrontendDev or AgentRole.MobileDev => WorkLayer.ApiConsumer,
        _ => WorkLayer.Unknown,
    };
}
