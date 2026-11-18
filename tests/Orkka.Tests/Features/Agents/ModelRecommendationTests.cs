namespace Orkka.Tests.Features.Agents;

using FluentAssertions;
using Orkka.Modules.Agents.Contracts;

/// <summary>
/// The advisory cheaper-model mapping. The stakes are trust, in both directions: a
/// recommendation that fires on subscription-billed or already-cheap runs reads as noise,
/// and one that upgrades instead of downgrades would silently raise the user's bill.
/// </summary>
public sealed class ModelRecommendationTests
{
    [Theory]
    [InlineData("trivial", "claude-sonnet-4-6", "claude-haiku-4-5")]
    [InlineData("trivial", "claude-sonnet-5", "claude-haiku-4-5")]
    [InlineData("trivial", "claude-opus-5", "claude-haiku-4-5")]
    [InlineData("small", "claude-sonnet-4-6", "claude-haiku-4-5")]
    [InlineData("small", "claude-opus-4-6", "claude-sonnet-4-6")]
    [InlineData("small", "claude-fable-5", "claude-sonnet-4-6")]
    [InlineData("SMALL", "claude-sonnet-4-6", "claude-haiku-4-5")] // grade is case-insensitive
    public void CheaperTierIsRecommended(string complexity, string resolved, string expected)
        => ModelRecommendation.Recommend(complexity, resolved, subscriptionBilled: false)
            .Should().Be(expected);

    [Theory]
    [InlineData("trivial", "claude-haiku-4-5")]  // already at the floor
    [InlineData("small", "claude-haiku-4-5")]
    [InlineData("standard", "claude-opus-5")]    // standard/complex work keeps its model
    [InlineData("complex", "claude-opus-5")]
    [InlineData(null, "claude-opus-5")]          // no grade, no suggestion
    [InlineData("gigantic", "claude-opus-5")]    // unknown grades fail silent
    public void NothingToGain_RecommendsNothing(string? complexity, string resolved)
        => ModelRecommendation.Recommend(complexity, resolved, subscriptionBilled: false)
            .Should().BeNull();

    [Fact]
    public void SubscriptionBilledRuns_AreNeverRecommendedAwayFromFree()
        => ModelRecommendation.Recommend("trivial", "claude-opus-5", subscriptionBilled: true)
            .Should().BeNull();

    [Theory]
    [InlineData("copilot/claude-sonnet-5")] // vendor-CLI namespaces are not comparable tiers
    [InlineData("gpt-5.6-sol")]
    [InlineData("ollama/qwen3-coder")]
    [InlineData(null)]
    [InlineData("")]
    public void NonAnthropicModels_GetNoRecommendation(string? resolved)
        => ModelRecommendation.Recommend("trivial", resolved, subscriptionBilled: false)
            .Should().BeNull();

    [Fact]
    public void RecommendedIdsAreSelectableInTheCatalog()
    {
        // Apply validates against the catalog; a mapping that emits a non-catalog id would
        // make every Apply click fail.
        ModelCatalog.IsSelectable("claude-haiku-4-5").Should().BeTrue();
        ModelCatalog.IsSelectable("claude-sonnet-4-6").Should().BeTrue();
    }
}
