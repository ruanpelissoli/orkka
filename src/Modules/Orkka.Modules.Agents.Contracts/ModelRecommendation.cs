namespace Orkka.Modules.Agents.Contracts;

/// <summary>
/// Advisory cheaper-model suggestion for a single task, derived from the PM review's
/// complexity grade. Never applied automatically and never touching an agent's configured
/// model: the recommendation lands on the task, and a human clicks Apply (which sets the
/// existing per-task override) or Dismiss. Anthropic API-billed runs only — subscription
/// CLI routes cost the user nothing, so there is no saving to recommend.
/// </summary>
public static class ModelRecommendation
{
    public const string Trivial = "trivial";
    public const string Small = "small";

    /// <summary>
    /// The cheaper model worth suggesting, or null when there is nothing to gain:
    /// standard/complex work, an unknown grade, a non-Anthropic or subscription-billed run,
    /// or a resolved model that is already at (or below) the suggested tier.
    /// </summary>
    public static string? Recommend(string? complexity, string? resolvedModel, bool subscriptionBilled)
    {
        if (subscriptionBilled || string.IsNullOrWhiteSpace(resolvedModel))
            return null;
        // Copilot ids also contain "claude-"; the prefix check keeps them (and every other
        // vendor-CLI namespace) out.
        if (!resolvedModel.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
            return null;

        var currentTier = Tier(resolvedModel);
        var candidate = complexity?.Trim().ToLowerInvariant() switch
        {
            Trivial => "claude-haiku-4-5",
            // A small task on an Opus-class model steps down one tier, not two — the point
            // is a sensible saving, not the cheapest possible run.
            Small => currentTier >= 2 ? "claude-sonnet-4-6" : "claude-haiku-4-5",
            _ => null,
        };

        return candidate is not null && Tier(candidate) < currentTier ? candidate : null;
    }

    /// <summary>Display name for the suggestion message/chip; falls back to the raw id.</summary>
    public static string DisplayName(string modelId) =>
        ModelCatalog.Models.FirstOrDefault(
            m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase))?.Name ?? modelId;

    private static int Tier(string model) =>
        model.Contains("haiku", StringComparison.OrdinalIgnoreCase) ? 0
        : model.Contains("sonnet", StringComparison.OrdinalIgnoreCase) ? 1
        : 2; // opus, fable, and anything newer: the expensive tier
}
