namespace Orkka.Modules.Tasks.Features.Tasks;

using Orkka.Modules.Tasks.Contracts;

/// <summary>
/// The board's wire vocabulary for lanes: snake_case keys (<c>code_review</c>), not enum names
/// (<c>CodeReview</c>). Every endpoint that returns something keyed by lane must go through here
/// — the moment two surfaces spell the same lane differently, the client grows a mapping table
/// of its own and it drifts the first time a lane is added.
/// </summary>
internal static class LaneKeys
{
    private static readonly Dictionary<string, string> KeyToEnumName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["drafting"] = "Drafting",
        ["ready"] = "Ready",
        ["planning"] = "Planning",
        ["plan_ready"] = "PlanReady",
        ["pending_questions"] = "PendingQuestions",
        ["developing"] = "Developing",
        ["testing"] = "Testing",
        ["code_review"] = "CodeReview",
        ["done"] = "Done"
    };

    private static readonly Dictionary<Lane, string> LaneToKey = new()
    {
        [Lane.Drafting] = "drafting",
        [Lane.Ready] = "ready",
        [Lane.Planning] = "planning",
        [Lane.PlanReady] = "plan_ready",
        [Lane.PendingQuestions] = "pending_questions",
        [Lane.Developing] = "developing",
        [Lane.Testing] = "testing",
        [Lane.CodeReview] = "code_review",
        [Lane.Done] = "done"
    };

    /// <summary>Board key to the enum's own name. Unknown keys pass through unchanged — a project
    /// with a custom lane key should not 500 the board.</summary>
    internal static string ToEnumName(string key) => KeyToEnumName.GetValueOrDefault(key, key);

    /// <summary>Enum to the board key. Falls back to the enum name so a lane added to the enum but
    /// not to this table is still visible (and obviously wrong) rather than silently missing.</summary>
    internal static string ToKey(Lane lane) => LaneToKey.GetValueOrDefault(lane, lane.ToString());
}
