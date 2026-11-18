namespace Orkka.Tests.Services;

using Orkka.SharedKernel.Ai;
using FluentAssertions;

public sealed class CostCalculatorTests
{
    [Fact]
    public void Calculate_ZeroTokens_YieldsZeroCost()
    {
        CostCalculator.Calculate("claude-sonnet-5", 0, 0).Should().Be(0m);
    }

    [Fact]
    public void Calculate_Sonnet5_BillsTheTwoTenListPrice()
    {
        // $2/$10 is Sonnet 5's list price, not an intro window. A $3/$15 "sticker" with an
        // intro that expired on 2026-09-01 over-billed every run by 1.5x from that day on.
        var baseCost = CostCalculator.Calculate(
            "claude-sonnet-5", inputTokens: 1_000_000, outputTokens: 1_000_000);
        baseCost.Should().Be(12.00m); // 2 + 10

        // And it stays that way — there is no date after which it reverts.
        var later = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var laterBase = CostCalculator.Calculate(
            "claude-sonnet-5", inputTokens: 1_000_000, outputTokens: 1_000_000, at: later);
        laterBase.Should().Be(12.00m);
    }

    [Fact]
    public void Calculate_Fable51_BillsListPrice_WithTheQuarterDollarCacheRead()
    {
        // $10 / $50, and cache hits at 0.025x input — the one Anthropic model whose cache
        // read is not the 0.1x default. Not in the picker, but an id an agent can carry.
        CostCalculator.Calculate("claude-fable-5-1", 1_000_000, 1_000_000).Should().Be(60.00m);
        CostCalculator.Calculate("claude-fable-5-1", 0, 0, cacheReadTokens: 1_000_000).Should().Be(0.25m);
        CostCalculator.Calculate("claude-fable-5-1", 0, 0, cacheWriteTokens: 1_000_000).Should().Be(12.50m);
    }

    [Fact]
    public void Calculate_Sonnet5_AdminRepricingStillWins()
    {
        // A catalog row an admin repriced is any number other than the sticker, and it wins.
        var adminBase = CostCalculator.Calculate(
            "claude-sonnet-5", 1_000_000, 1_000_000, rates: (2.50m, 12.00m));
        adminBase.Should().Be(14.50m);
    }

    // ── Google (Gemini) ─────────────────────────────────────────────────────
    // Rates verified against ai.google.dev/gemini-api/docs/pricing on 2026-08-15.
    // NOTE: like Grok, Gemini Pro's threshold is 200K, so token counts here stay
    // deliberately away from the round 1M figures used for Claude/GPT above — those
    // would silently assert the long-context tier instead of the sticker.

    [Theory]
    // Google shut gemini-3-pro-preview down (2026-03-09) and deprecated
    // gemini-3-flash-preview; the catalog moved to these replacements. Every dead id must
    // still price — an agent pinned to one runs the replacement (GeminiAgentInvoker maps
    // it) but records usage under its own id, so a miss here bills the vendor's real cost
    // at UnknownModelRates, or worse, at a stale cheaper rate.
    [InlineData("gemini-3.1-pro-preview", 2.00, 12.00)]
    [InlineData("gemini-3-pro-preview", 2.00, 12.00)]
    [InlineData("gemini-3-pro", 2.00, 12.00)]
    public void Calculate_GeminiPro_PricesTheLiveIdAndEveryDeadOneAlike(
        string modelId, double inputRate, double outputRate)
    {
        var costBase = CostCalculator.Calculate(
            modelId, inputTokens: 100_000, outputTokens: 100_000);

        // 100K/1M of each rate, well under the 200K long-context threshold.
        costBase.Should().Be((decimal)(inputRate + outputRate) / 10m);
    }

    [Theory]
    [InlineData("gemini-3.6-flash")]
    [InlineData("gemini-3-flash-preview")]
    [InlineData("gemini-3-flash")]
    public void Calculate_GeminiFlash_ChargesTheIntroPriceThenTheStickerIn2027(string modelId)
    {
        // Google: "$0.75 through December 31, 2026. $1.50 starting January 1, 2027"
        // (output $3.75 → $7.50). The sticker in BaseRates is the 2027 number, so the
        // intro table is what makes today correct — and the reversion automatic.
        var during = new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);
        var introBase = CostCalculator.Calculate(
            modelId, inputTokens: 1_000_000, outputTokens: 1_000_000, at: during);
        introBase.Should().Be(4.50m); // $0.75 + $3.75

        var after = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var stickerBase = CostCalculator.Calculate(
            modelId, inputTokens: 1_000_000, outputTokens: 1_000_000, at: after);
        stickerBase.Should().Be(9.00m); // $1.50 + $7.50
    }

    [Fact]
    public void Calculate_GeminiFlash_DeprecatedIdsBillAsWhatTheyActuallyRun()
    {
        // The dead flash ids used to be priced at a guessed $0.50/$3.00. They now resolve
        // to gemini-3.6-flash at the CLI, so pricing them below the 3.6 rate would
        // under-bill every run by an agent nobody has repointed yet.
        var at = new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);
        var live = CostCalculator.Calculate("gemini-3.6-flash", 1_000_000, 1_000_000, at: at);

        CostCalculator.Calculate("gemini-3-flash-preview", 1_000_000, 1_000_000, at: at)
            .Should().Be(live);
        CostCalculator.Calculate("gemini-3-flash", 1_000_000, 1_000_000, at: at)
            .Should().Be(live);
    }

    [Fact]
    public void Calculate_GeminiProLongPrompt_RebillsTheWholeRequestAtTheHigherTier()
    {
        // Google prices "prompts > 200k tokens" at $4/$18, and a prompt over the threshold
        // bills ALL tokens — input and output — at the long-context rate, not just the
        // overage. Same whole-request shape as Grok and gpt-5.6-sol.
        var costBase = CostCalculator.Calculate(
            "gemini-3.1-pro-preview", inputTokens: 210_000, outputTokens: 10_000);

        // 210K/1M * $4 + 10K/1M * $18 = $0.84 + $0.18
        costBase.Should().Be(1.02m);
    }

    [Fact]
    public void Calculate_GeminiProAt200K_StaysOnTheCheapTier()
    {
        // Google's boundary is "<= 200k" cheap / "> 200k" dear — exactly 200,000 is still
        // the low tier, unlike Grok where reaching 200K already triggers the higher rate.
        var costBase = CostCalculator.Calculate(
            "gemini-3.1-pro-preview", inputTokens: 200_000, outputTokens: 0);

        costBase.Should().Be(0.40m); // 200K/1M * $2, not $4
    }

    [Fact]
    public void Calculate_GeminiProCacheRead_MatchesGooglesPublishedCacheRate()
    {
        // Google lists context caching on Pro at $0.20/1M against $2.00 input — exactly the
        // 10% the default (Anthropic-shaped) cache profile applies, which is why Gemini
        // needs no CacheProfiles row. The 125% cache-WRITE premium in that default is wrong
        // for Google but never fires: the gemini invoker reports `cached` as cacheReadTokens
        // and leaves cacheWriteTokens at 0.
        var costBase = CostCalculator.Calculate(
            "gemini-3.1-pro-preview", inputTokens: 0, outputTokens: 0, cacheReadTokens: 100_000);

        costBase.Should().Be(0.02m); // 100K/1M * $0.20
    }

    [Fact]
    public void Calculate_GeminiImageModel_BillsOutputAtImageRate()
    {
        // A typical 1K/2K image ≈ 1120 output tokens on the Pro image model.
        var costBase = CostCalculator.Calculate(
            "gemini-3-pro-image-preview", inputTokens: 500, outputTokens: 1120);

        // 500/1M * $2 + 1120/1M * $120 = $0.001 + $0.1344
        costBase.Should().Be(0.1354m);
    }

    [Fact]
    public void Calculate_GeminiFlashImage_HasKnownRates()
    {
        var costBase = CostCalculator.Calculate(
            "gemini-2.5-flash-image", inputTokens: 0, outputTokens: 1_000_000);

        costBase.Should().Be(30.00m);
    }

    [Fact]
    public void Calculate_GptImage1_BillsPerToken()
    {
        // A 1024x1024 medium-quality image ≈ 1056 output tokens; the model id must not
        // fall through to UnknownModelRates.
        var costBase = CostCalculator.Calculate(
            "gpt-image-1", inputTokens: 100, outputTokens: 1056);

        // 100/1M * $10 + 1056/1M * $40 = $0.001 + $0.04224
        costBase.Should().Be(0.04324m);
    }

    [Fact]
    public void Calculate_OpenAiModel_UsesGptRates()
    {
        // gpt-4o list price: $2.50 input / $10 output per 1M.
        var costBase = CostCalculator.Calculate(
            "gpt-4o", inputTokens: 1_000_000, outputTokens: 1_000_000);

        costBase.Should().Be(12.50m);
    }

    [Fact]
    public void Calculate_OpenAiCachedInput_DiscountsRead_AndHasNoWriteCharge()
    {
        // OpenAI: cached input bills at ~50% of the input rate and there is NO cache-write
        // charge. The Anthropic-specific write premium (125% of input) must not leak in here,
        // or every GPT run with cache-write tokens would be over-billed.
        var costBase = CostCalculator.Calculate(
            "gpt-4o", inputTokens: 0, outputTokens: 0,
            cacheReadTokens: 1_000_000, cacheWriteTokens: 1_000_000);

        // 1M cache reads * ($2.50 * 0.5) + cache writes billed at $0.
        costBase.Should().Be(1.25m);
    }

    [Fact]
    public void Calculate_AnthropicCacheMath_Unchanged()
    {
        // Regression: Claude keeps read=10% / write=125% of the input rate ($3/1M input).
        var costBase = CostCalculator.Calculate(
            "claude-sonnet-4-6", inputTokens: 0, outputTokens: 0,
            cacheReadTokens: 1_000_000, cacheWriteTokens: 1_000_000);

        // 1M * ($3 * 0.1) + 1M * ($3 * 1.25) = $0.30 + $3.75
        costBase.Should().Be(4.05m);
    }

    // ── xAI (Grok) ───────────────────────────────────────────────────────────
    // Rates verified against docs.x.ai/docs/models on 2026-08-15.

    // NOTE: unlike the Claude/GPT cases above, these deliberately do NOT use 1M-token
    // figures. Grok's threshold is 200K, so a 1M-token prompt is long-context by
    // definition — round numbers borrowed from the other vendors would silently be
    // asserting the wrong tier.

    [Fact]
    public void Calculate_Grok_UsesShortContextRates()
    {
        var costBase = CostCalculator.Calculate(
            "grok-4.6", inputTokens: 100_000, outputTokens: 50_000);

        // 100K/1M * $2 + 50K/1M * $6 = $0.20 + $0.30
        costBase.Should().Be(0.50m);
    }

    [Fact]
    public void Calculate_GrokCachedInput_BillsAtAQuarter_AndHasNoWriteCharge()
    {
        // xAI is a THIRD cache shape: cached input is $0.50 against $2.00 input = 25%,
        // neither Anthropic's 10% nor OpenAI's 50%, and there is no cache-write charge.
        // Falling through to the Anthropic default would under-bill reads and invent a
        // 125% write premium the vendor never levies. Token counts stay well under 200K so
        // this asserts the cache shape alone, not the long-context tier.
        var costBase = CostCalculator.Calculate(
            "grok-4.6", inputTokens: 0, outputTokens: 0,
            cacheReadTokens: 50_000, cacheWriteTokens: 50_000);

        // 50K/1M * ($2.00 * 0.25) = $0.025; cache writes billed at $0.
        costBase.Should().Be(0.025m);
    }

    [Fact]
    public void Calculate_GrokLongPrompt_RebillsTheWholeRequestAtTheHigherTier()
    {
        // At 200K the vendor doubles every rate for ALL tokens in the request — a 210K
        // prompt is not 200K cheap + 10K dear. Billing the short-context rate here would
        // under-charge by half on exactly the longest (most expensive) runs.
        var costBase = CostCalculator.Calculate(
            "grok-4.6", inputTokens: 210_000, outputTokens: 10_000);

        // 210K/1M * $4 + 10K/1M * $12 = $0.84 + $0.12
        costBase.Should().Be(0.96m);
    }

    [Fact]
    public void Calculate_GrokPromptJustUnderTheThreshold_StaysOnTheCheapTier()
    {
        var costBase = CostCalculator.Calculate(
            "grok-4.6", inputTokens: 199_999, outputTokens: 0);

        // 199,999/1M * $2 — the boundary is inclusive at 200,000, not before it.
        costBase.Should().Be(0.399998m);
    }

    [Fact]
    public void Calculate_GrokThreshold_CountsEveryInputSideToken()
    {
        // The vendor's threshold is on the PROMPT, and cached reads are prompt tokens too.
        // Counting only fresh input would let a heavily-cached 300K-token prompt bill at the
        // cheap tier — the exact case caching makes common.
        var costBase = CostCalculator.Calculate(
            "grok-4.6", inputTokens: 100_000, outputTokens: 0, cacheReadTokens: 150_000);

        // 250K total prompt ≥ 200K → $4 tier: 100K/1M * $4 + 150K/1M * ($4 * 0.25)
        costBase.Should().Be(0.55m);
    }

    [Fact]
    public void Calculate_GrokOutputAloneNeverTriggersTheLongContextTier()
    {
        // Output is not part of the prompt. A short prompt with a huge completion must stay
        // on the cheap tier, or every long answer would be double-billed.
        var costBase = CostCalculator.Calculate(
            "grok-4.6", inputTokens: 1_000, outputTokens: 500_000);

        // 1K/1M * $2 + 500K/1M * $6
        costBase.Should().Be(3.002m);
    }

    [Fact]
    public void Calculate_GrokAdminReprice_IsNeverDoubledOnLongPrompts()
    {
        // An admin-managed rate is a deliberate number. The long-context bump is guarded on
        // sticker equality for the same reason the intro price is: silently doubling
        // someone's negotiated rate on long runs would be indefensible.
        var costBase = CostCalculator.Calculate(
            "grok-4.6", inputTokens: 210_000, outputTokens: 0, rates: (1.00m, 3.00m));

        costBase.Should().Be(0.21m);
    }

    // ── GPT-5 generation: verified at developers.openai.com, 2026-09-05 ──────

    [Fact]
    public void Calculate_Gpt5Family_BillsTheVerifiedListPrices()
    {
        // gpt-5 was a $5/$25 placeholder against a real $1.25/$10 (4x on input); gpt-5.5 a
        // $5/$25 placeholder against $5/$30; gpt-5.6-sol carried $5/$30 against $4/$20.
        // 100K tokens a side: gpt-5.5 and Sol have a 272K long-context tier, so a round 1M
        // prompt would assert the tier instead of the sticker (see the Gemini note above).
        CostCalculator.Calculate("gpt-5", 1_000_000, 1_000_000).Should().Be(11.25m);
        CostCalculator.Calculate("gpt-5.5", 100_000, 100_000).Should().Be(3.50m);
        CostCalculator.Calculate("gpt-5.6-sol", 100_000, 100_000).Should().Be(2.40m);
        CostCalculator.Calculate("gpt-5.6", 100_000, 100_000).Should().Be(2.40m);

        // gpt-5 / gpt-5.5: cached input at 10%, and NO cache-write charge.
        CostCalculator.Calculate("gpt-5", 0, 0, cacheReadTokens: 1_000_000, cacheWriteTokens: 1_000_000)
            .Should().Be(0.125m);
        // (cache tokens are prompt tokens and count toward the 272K tier — keep it short)
        CostCalculator.Calculate("gpt-5.5", 0, 0, cacheReadTokens: 100_000, cacheWriteTokens: 100_000)
            .Should().Be(0.05m);
        // gpt-5.5 shares Sol's 272K tier: 2x input, 1.5x output, whole request.
        CostCalculator.Calculate("gpt-5.5", 300_000, 10_000).Should().Be(3.00m + 0.45m);
    }

    [Fact]
    public void Calculate_GptImage_CachedInputIsASeventyFivePercentDiscount()
    {
        // $8 image input on gpt-image-2, cached at $2 — the vendor-wide 50% rule billed $4.
        CostCalculator.Calculate("gpt-image-2", 0, 0, cacheReadTokens: 1_000_000).Should().Be(2.00m);
        CostCalculator.Calculate("gpt-image-1", 0, 0, cacheReadTokens: 1_000_000).Should().Be(2.50m);
    }

    [Fact]
    public void Calculate_Gpt56Sol_CachedInput_BillsAtTenPercent_NotFifty()
    {
        // REGRESSION: the vendor-wide OpenAI rule billed cache reads at 50% of input
        // ($2.50/1M) when OpenAI charges $0.50/1M — a 5x over-charge on precisely the tokens
        // a long agent run accumulates most of. The GPT-5 generation uses Anthropic-style
        // cache pricing (10% read / 125% write), unlike the gpt-4o era.
        var costBase = CostCalculator.Calculate(
            "gpt-5.6-sol", inputTokens: 0, outputTokens: 0, cacheReadTokens: 100_000);

        // 100K/1M * ($4.00 * 0.10) = $0.04
        costBase.Should().Be(0.04m);
    }

    [Fact]
    public void Calculate_Gpt4o_KeepsTheFiftyPercentCacheDiscount()
    {
        // The gpt-4o era genuinely does discount cached input to 50% with no write charge —
        // the per-model override must not leak the GPT-5 shape onto it.
        var costBase = CostCalculator.Calculate(
            "gpt-4o", inputTokens: 0, outputTokens: 0, cacheReadTokens: 100_000);

        // 100K/1M * ($2.50 * 0.5) = $0.125
        costBase.Should().Be(0.125m);
    }

    [Fact]
    public void Calculate_Gpt56Sol_LongPrompt_Applies2xInputAnd1_5xOutput()
    {
        // OpenAI applies 2x input / 1.5x output "to the full request" past 272K input tokens
        // — a different multiplier per side, unlike xAI's flat doubling.
        var costBase = CostCalculator.Calculate(
            "gpt-5.6-sol", inputTokens: 300_000, outputTokens: 10_000);

        // 300K/1M * $8 + 10K/1M * $30 = $2.40 + $0.30
        costBase.Should().Be(2.70m);
    }

    [Fact]
    public void Calculate_Gpt56Sol_AtExactly272K_StaysOnTheCheapTier()
    {
        // OpenAI's wording is "exceeding 272K", not "reaching" it (xAI's threshold is
        // inclusive) — the stored trigger point carries that difference.
        var costBase = CostCalculator.Calculate(
            "gpt-5.6-sol", inputTokens: 272_000, outputTokens: 0);

        // 272K/1M * $4 — still short-context.
        costBase.Should().Be(1.088m);
    }

    [Fact]
    public void PricedModelIds_IncludesGrok_SoTheCatalogSeedsARateRow()
        // A model the catalog offers but the rate table doesn't price bills at
        // UnknownModelRates ($15/$75) — for a $2 model that is a 7.5x over-charge.
        => CostCalculator.TryGetBaseRates("grok-4.6").Should().Be((2.00m, 6.00m));

    // ── Subscription-only namespaces ────────────────────────────────────────
    // Copilot bills premium requests on the user's own plan, Ollama runs on their machine,
    // OpenCode drives their own provider logins: Orkka pays nothing per token, so these must
    // never fall through to UnknownModelRates ($15/$75) — a subscription run billed at the
    // premium tier is exactly the "subscription never billed" rule broken.

    [Theory]
    [InlineData("copilot/auto")]
    [InlineData("copilot/claude-sonnet-5")]
    [InlineData("copilot/gpt-5.6-sol")]
    [InlineData("copilot/claude-sonnet-4.5")] // retired, still 0/0
    [InlineData("copilot/gpt-5")]
    [InlineData("COPILOT/GPT-5.4")]
    [InlineData("ollama/gpt-oss:20b")]
    [InlineData("ollama/qwen3-coder:14b-instruct")]
    [InlineData("opencode/anthropic/claude-sonnet-4-6")]
    [InlineData("opencode/openai/gpt-5.6-sol")]
    public void IsSubscriptionOnly_MatchesTheThreeNamespaces(string model)
        => CostCalculator.IsSubscriptionOnly(model).Should().BeTrue();

    [Theory]
    [InlineData("claude-sonnet-5")]
    [InlineData("gpt-5.6-sol")]
    [InlineData("grok-4.6")]
    [InlineData("gemini-3.6-flash")]
    [InlineData("copilot")]   // bare provider id, no namespace separator
    [InlineData("ollama")]
    [InlineData("")]
    [InlineData(null)]
    public void IsSubscriptionOnly_IsFalse_ForPricedVendorsAndBareIds(string? model)
        => CostCalculator.IsSubscriptionOnly(model).Should().BeFalse();

    [Theory]
    [InlineData("copilot/claude-sonnet-5")]
    [InlineData("copilot/gpt-5")]
    [InlineData("ollama/gpt-oss:20b")]
    [InlineData("opencode/anthropic/claude-sonnet-4-6")]
    public void Calculate_SubscriptionOnlyModel_CostsZero_NotUnknownModelRates(string model)
    {
        var costBase = CostCalculator.Calculate(
            model, inputTokens: 1_000_000, outputTokens: 1_000_000,
            cacheReadTokens: 1_000_000, cacheWriteTokens: 1_000_000);

        costBase.Should().Be(0m, "Orkka pays the vendor nothing for a subscription run");
        // The UnknownModelRates tier would have been $15 + $75 + cache terms — the exact
        // failure this rule exists to prevent.
        CostCalculator.FormatCost(model, 1_000_000, 1_000_000).Should().Be("$0.0000");
    }

    [Fact]
    public void Calculate_SubscriptionOnlyModel_IgnoresASuppliedRateOverride()
        // An admin-managed rate row for a Copilot model would still be charging for tokens
        // the vendor never bills Orkka for; the namespace rule outranks it.
        => CostCalculator.Calculate("copilot/claude-sonnet-5", 1_000_000, 1_000_000, rates: (3m, 15m))
            .Should().Be(0m);

    [Theory]
    [InlineData("copilot/claude-sonnet-5")]
    [InlineData("copilot/claude-sonnet-4.5")]
    [InlineData("ollama/gpt-oss:20b")]
    [InlineData("opencode/xai/grok-4.6")]
    public void TryGetBaseRates_SubscriptionOnlyModel_IsZeroZero_SoTheSeedRowIsNotNull(string model)
        // The catalog seed copies this into the row; null would read as "unpriced" in the
        // admin UI and, for a DB-backed rate lookup, fall through to the shipped table anyway.
        => CostCalculator.TryGetBaseRates(model).Should().Be((0m, 0m));

    [Fact]
    public void PricedModelIds_NeverContainsASubscriptionOnlyNamespace()
        // Putting a $0 row in BaseRates would make PricedModelIds seed them as "priced"
        // retired rows; the namespace rule is the single source of the 0/0 answer.
        => CostCalculator.PricedModelIds.Should()
            .NotContain(id => CostCalculator.IsSubscriptionOnly(id));

    [Fact]
    public void Calculate_UnknownNonSubscriptionModel_StillBillsAtTheHighestTier()
    {
        // The safety net is untouched for genuinely unknown ids.
        var costBase = CostCalculator.Calculate(
            "some-new-frontier-model", inputTokens: 1_000_000, outputTokens: 1_000_000);

        costBase.Should().Be(90.00m);
    }
}
