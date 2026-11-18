namespace Orkka.SharedKernel.Ai;

using System.Text.RegularExpressions;

public static partial class CostCalculator
{
    // Rates per 1M tokens (what the vendor charges us). Keyed by model id; provider is
    // inferred from the id prefix (see IsOpenAiModel) so cache math can differ per vendor.
    //
    // The shipped snapshot: agents.ai_models (seeded from it) is read first
    // (IModelRateProvider); this table is the fallback when the catalog has no row.
    private static readonly Dictionary<string, (decimal Input, decimal Output)> BaseRates = new()
    {
        // Fable family (above Opus tier). 5.1 is not in the picker (a $10/$50 model on a
        // platform that just moved every default to Sonnet 5), but an agent pinned to it by
        // hand must bill at its own rate rather than the $15/$75 unknown-model tier. Its cache
        // reads are 0.025x input (CacheProfiles), the one Anthropic model that differs.
        ["claude-fable-5-1"] = (10.00m, 50.00m),
        ["claude-fable-5"] = (10.00m, 50.00m),
        // Opus family
        ["claude-opus-5"] = (5.00m, 25.00m),
        ["claude-opus-4-8"] = (5.00m, 25.00m),
        ["claude-opus-4-7"] = (5.00m, 25.00m),
        ["claude-opus-4-6"] = (5.00m, 25.00m),
        ["claude-opus-4-5"] = (5.00m, 25.00m),
        ["claude-opus-4-1"] = (15.00m, 75.00m),
        ["claude-opus-4"] = (15.00m, 75.00m),
        // Sonnet family
        // Sonnet 5 LISTS at $2/$10 — that is the sticker, not a promotion. It was carried here
        // as $3/$15 with a $2/$10 "intro" that self-expired on 2026-09-01; from that day every
        // Sonnet 5 run was billed 1.5x what the vendor charged (ESF-2, 2026-09-05: OurCost
        // $3.25 against the CLI's $2.17) and every spend ceiling tripped a third early. The
        // seeded catalog row is corrected by the Agents migration VendorListPriceAudit.
        ["claude-sonnet-5"] = (2.00m, 10.00m),
        ["claude-sonnet-4-6"] = (3.00m, 15.00m),
        ["claude-sonnet-4-5"] = (3.00m, 15.00m),
        ["claude-sonnet-4"] = (3.00m, 15.00m),
        // Haiku family
        ["claude-haiku-4-5"] = (1.00m, 5.00m),
        // Google Gemini text models. VERIFIED 2026-08-15 against
        // ai.google.dev/gemini-api/docs/{pricing,deprecations}, which is also what forced
        // the migration these rows now reflect: gemini-3-pro-preview was SHUT DOWN on
        // 2026-03-09 and gemini-3-flash-preview is deprecated, so the catalog moved to
        // Google's own named replacements — gemini-3.1-pro-preview and gemini-3.6-flash.
        //
        // The four dead ids below stay priced, and deliberately at the SAME rates as their
        // replacements rather than their own historical numbers. GeminiAgentInvoker maps
        // every one of them onto a live id, so an agent still pinned to
        // "gemini-3-flash-preview" really runs gemini-3.6-flash and the vendor really
        // charges the 3.6 rate — while usage is recorded against the agent's catalog id,
        // not the id handed to the CLI. Pricing them at the old $0.50/$3.00 would
        // under-bill every one of those runs.
        //
        // 3.6-flash was picked over 3.1-flash because it is the vendor's stated
        // replacement and the pricing page lists the two identically, so following the
        // pointer costs nothing.
        //
        // Neither needs a CacheProfiles row. Google lists context caching on Pro at
        // $0.20/1M (and $0.40 on the long-context tier) — exactly the 10% of input the
        // default profile already applies. Its 125% cache-WRITE premium is Anthropic-shaped
        // and wrong for Google, but never fires: the gemini invoker reports `cached` as
        // cacheReadTokens and leaves cacheWriteTokens at 0.
        ["gemini-3.1-pro-preview"] = (2.00m, 12.00m),  // >200K tier in LongContextRates
        ["gemini-3.6-flash"] = (1.50m, 7.50m),         // 2027 price; intro in IntroductoryRates
        // Shut down / deprecated at the vendor. Kept billable, priced as what they now run.
        ["gemini-3-pro-preview"] = (2.00m, 12.00m),
        ["gemini-3-pro"] = (2.00m, 12.00m),
        ["gemini-3-flash-preview"] = (1.50m, 7.50m),
        ["gemini-3-flash"] = (1.50m, 7.50m),
        // Google Gemini image models — retired: Orkka no longer generates images server-side.
        // Kept priced (like v0 below) so historical usage rows under these ids still resolve.
        ["gemini-3-pro-image-preview"] = (2.00m, 120.00m),
        ["gemini-2.5-flash-image"] = (0.30m, 30.00m),

        // ---- OpenAI (GPT) ----
        // Every row VERIFIED 2026-09-05 against developers.openai.com/api/docs/pricing and the
        // per-model pages (the 2026-08-15 numbers for gpt-5.6-sol had since moved, and the
        // GPT-5 rows below were placeholders that over-charged up to 4x). Cache shapes and
        // long-context tiers live in CacheProfiles / LongContextRates. Any id missing entirely
        // bills at UnknownModelRates (highest tier), so an omission over-charges rather than under-.
        ["gpt-4o"] = (2.50m, 10.00m),
        ["gpt-4o-mini"] = (0.15m, 0.60m),
        // gpt-5: $1.25 / $10; cached input $0.125 (10%), no cache-write charge.
        ["gpt-5"] = (1.25m, 10.00m),
        // gpt-5.5: $5 / $30 under 272K input; 2x input / 1.5x output for the whole request
        // above it; cached input $0.50 (10%), no cache-write charge.
        ["gpt-5.5"] = (5.00m, 30.00m),
        // Retired from the picker but still billable: agents pinned to it before gpt-5.6-sol
        // landed keep recording usage under this id. Its vendor page is gone (404 on
        // 2026-09-05), so it is priced as gpt-5.5, the model it was a variant of.
        ["gpt-5.5-codex"] = (5.00m, 30.00m),
        // gpt-5.6-sol: $4 / $20 under 272K input, cached input $0.40 (10%), cache writes 1.25x
        // (developers.openai.com/api/docs/models/gpt-5.6-sol). Was carried as $5 / $30 — a
        // 25-50% over-charge on the default OpenAI model.
        ["gpt-5.6-sol"] = (4.00m, 20.00m),
        ["gpt-5.6"] = (4.00m, 20.00m),    // vendor alias for Sol
        // OpenAI image models — retired with server-side image generation; kept priced so
        // historical usage rows under these ids still resolve.
        ["gpt-image-2"] = (8.00m, 30.00m),
        ["gpt-image-1"] = (10.00m, 40.00m),

        // ---- Vercel v0 (retired design-brief preview) ----
        // Nothing calls v0 any more; kept priced so historical usage rows still resolve
        // instead of falling to UnknownModelRates.
        ["v0-auto"] = (3.00m, 15.00m),
        ["v0-mini"] = (3.00m, 15.00m),
        ["v0-pro"] = (3.00m, 15.00m),
        ["v0-max"] = (7.50m, 37.50m),
        ["v0-max-fast"] = (7.50m, 37.50m),
        ["v0-1.5-md"] = (3.00m, 15.00m),
        ["v0-1.5-lg"] = (7.50m, 37.50m),
        ["v0-1.0-md"] = (3.00m, 15.00m),

        // ---- xAI (Grok) ----
        // Verified against docs.x.ai/docs/models on 2026-08-15: $2 input / $0.50 cached
        // input / $6 output per 1M for prompts under 200K, on a 500K-token context window.
        // Cached input is 25% of input, which is neither Anthropic's 10% nor OpenAI's 50% —
        // see IsXaiModel below. At 200K the vendor DOUBLES every rate for the whole request;
        // that lives in LongContextRates, not here, because it depends on the request.
        ["grok-4.6"] = (2.00m, 6.00m),
    };

    /// <summary>
    /// Vendor prices that change once a request's prompt crosses a token threshold, billed at
    /// the higher rate for EVERY token in that request rather than just the overflow. xAI does
    /// this to grok-4.6 at 200K — a 210K-token prompt is not 200K cheap + 10K dear, it is 210K
    /// dear — and with a 500K context window on an agentic coding model, crossing it is
    /// ordinary rather than exotic.
    ///
    /// Applied in <see cref="Calculate"/> under the same sticker-equality guard as
    /// <see cref="IntroductoryRates"/>, so a deliberate admin reprice is never silently
    /// doubled on long runs.
    ///
    /// Google does the same to Gemini Pro at 200K. Its pricing page states the tiers but not
    /// the mechanic; Google's Cloud pricing doc supplies it — a prompt over 200K bills ALL
    /// tokens, input and output, at the long-context rate — which is why the whole-request
    /// shape here fits it unchanged.
    /// </summary>
    /// <remarks><c>MinPromptTokens</c> is the SMALLEST prompt that bills at the higher tier,
    /// because vendors word the boundary differently: xAI charges more once a prompt "reaches"
    /// 200K, OpenAI once one "exceeds" 272K. Storing the inclusive trigger point keeps one
    /// comparison here instead of a per-vendor off-by-one.</remarks>
    private static readonly Dictionary<string, (int MinPromptTokens, decimal Input, decimal Output)>
        LongContextRates = new()
    {
        // docs.x.ai/docs/models, verified 2026-08-15: reaching 200K doubles both rates.
        ["grok-4.6"] = (200_000, 4.00m, 12.00m),
        // developers.openai.com/api/docs/models/gpt-5.6-sol and /gpt-5.5, verified 2026-09-05:
        // exceeding 272K input tokens applies 2x input and 1.5x output "to the full request".
        ["gpt-5.6-sol"] = (272_001, 8.00m, 30.00m),
        ["gpt-5.6"] = (272_001, 8.00m, 30.00m),
        ["gpt-5.5"] = (272_001, 10.00m, 45.00m),
        ["gpt-5.5-codex"] = (272_001, 10.00m, 45.00m),
        // ai.google.dev/gemini-api/docs/pricing, verified 2026-08-15: Gemini 3.1 Pro is
        // $2/$12 for "prompts <= 200k tokens" and $4/$18 for "prompts > 200k", so the
        // smallest prompt at the higher tier is 200_001. The shut-down ids ride along
        // because they now run as 3.1 Pro — see the rate rows above.
        ["gemini-3.1-pro-preview"] = (200_001, 4.00m, 18.00m),
        ["gemini-3-pro-preview"] = (200_001, 4.00m, 18.00m),
        ["gemini-3-pro"] = (200_001, 4.00m, 18.00m),
    };

    /// <summary>True when the (normalized) model id is served by OpenAI. Drives vendor-specific
    /// cache pricing — OpenAI discounts cached input and has no separate cache-write charge,
    /// unlike Anthropic's read=10%/write=125% model.</summary>
    private static bool IsOpenAiModel(string model) =>
        model.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("o3", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("o4", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the (normalized) model id is served by xAI. Its cache pricing is a
    /// third shape again: cached input is 25% of input ($0.50 against $2.00, and $1.00 against
    /// $4.00 on the long-context tier — the same ratio either way), with no separate
    /// cache-write charge. Falling through to the Anthropic default would bill cache reads at
    /// 10% and invent a 125% write charge the vendor never levies.</summary>
    private static bool IsXaiModel(string model) =>
        model.StartsWith("grok", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Per-MODEL cache multipliers (read, write) as fractions of the input rate, overriding the
    /// per-vendor defaults below. Needed because one vendor can ship two shapes: gpt-4o-era
    /// OpenAI models discount cached input to 50% with no write charge, but the GPT-5
    /// generation moved to Anthropic-style pricing — cached input at 10% and cache writes at
    /// 1.25x (verified at developers.openai.com/api/docs/models/gpt-5.6-sol, 2026-08-15).
    ///
    /// The vendor-wide <see cref="IsOpenAiModel"/> rule was billing gpt-5.6-sol cache reads at
    /// 50% of $5 = $2.50/1M against a real $0.50/1M — a 5x over-charge on exactly the tokens
    /// a long agent run accumulates most of. The comment beside its rate row already SAID
    /// "10% cache-read discount"; only the code disagreed.
    /// </summary>
    private static readonly Dictionary<string, (decimal Read, decimal Write)> CacheProfiles = new()
    {
        ["gpt-5.6-sol"] = (0.10m, 1.25m),
        ["gpt-5.6"] = (0.10m, 1.25m),
        // The rest of the GPT-5 generation discounts cached input 90% too, but lists no
        // cache-write charge at all (verified 2026-09-05).
        ["gpt-5"] = (0.10m, 0.00m),
        ["gpt-5.5"] = (0.10m, 0.00m),
        ["gpt-5.5-codex"] = (0.10m, 0.00m),
        // Image models: cached input is a 75% discount ($1.25 against $5 text, $2 against $8
        // image on gpt-image-2), no write charge. The vendor-wide 50% rule over-billed them.
        ["gpt-image-2"] = (0.25m, 0.00m),
        ["gpt-image-1"] = (0.25m, 0.00m),
        // Claude Fable 5.1: cache hits at 0.025x the base input price ($0.25/MTok), writes at
        // the usual 1.25x (platform.claude.com pricing, 2026-09-05).
        ["claude-fable-5-1"] = (0.025m, 1.25m),
    };

    // Safety net for model IDs missing from BaseRates: bill at the HIGHEST tier.
    // Undercharging an unknown (possibly premium) model is a direct loss; the fix
    // for an over-billed known model is to add it to BaseRates.
    private static readonly (decimal Input, decimal Output) UnknownModelRates = (15.00m, 75.00m);

    /// <summary>Namespaces whose models never carry a per-token vendor charge Orkka pays:
    /// Copilot bills premium requests against the user's own Copilot plan, Ollama runs on
    /// the user's machine, and OpenCode drives the user's own provider logins. They are
    /// deliberately NOT in <see cref="BaseRates"/> (a $0 row there would be fiction, and
    /// <see cref="PricedModelIds"/> would seed them as "priced"); instead every rate lookup
    /// answers 0/0 for them, so a usage row under such an id can never fall through to
    /// <see cref="UnknownModelRates"/> and bill a subscription run at $15/$75.</summary>
    private static readonly string[] SubscriptionOnlyPrefixes = ["copilot/", "ollama/", "opencode/"];

    private static readonly (decimal Input, decimal Output) SubscriptionOnlyRates = (0m, 0m);

    /// <summary>True when the model id belongs to a subscription-only namespace (see
    /// <see cref="SubscriptionOnlyPrefixes"/>): Orkka pays the vendor nothing per token for it.</summary>
    public static bool IsSubscriptionOnly(string? model) =>
        !string.IsNullOrWhiteSpace(model)
        && SubscriptionOnlyPrefixes.Any(p => model.TrimStart().StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Time-bounded vendor prices that differ from the sticker in <see cref="BaseRates"/>.
    /// Applied in <see cref="Calculate"/> only while the effective rate still EQUALS the
    /// sticker — the catalog seeder copies the sticker into the DB, so matching on equality
    /// makes the intro price reach seeded deployments too, while a deliberate admin reprice
    /// (any other number) always wins. Self-reverts at <c>Until</c> with no redeploy.
    /// </summary>
    private static readonly Dictionary<string, (DateTimeOffset Until, decimal Input, decimal Output)>
        IntroductoryRates = new()
    {
        // Google lists Gemini 3.6 Flash at "$0.75 through December 31, 2026. $1.50 starting
        // January 1, 2027" (output $3.75 → $7.50), verified 2026-08-15. The sticker in
        // BaseRates is the 2027 number, so this table is what makes today's price correct
        // and the reversion automatic. The deprecated flash ids ride along because they now
        // run as 3.6 Flash — see the rate rows above.
        ["gemini-3.6-flash"] = (new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), 0.75m, 3.75m),
        ["gemini-3-flash-preview"] = (new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), 0.75m, 3.75m),
        ["gemini-3-flash"] = (new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), 0.75m, 3.75m),
    };

    // Maps date-suffixed model IDs to canonical names
    // e.g. "claude-sonnet-4-20250514" -> "claude-sonnet-4"
    [GeneratedRegex(@"-\d{8}$")]
    private static partial Regex DateSuffixRegex();

    private static string NormalizeModelId(string model)
    {
        // First check if the exact model ID is in the rates
        if (BaseRates.ContainsKey(model))
            return model;

        // Strip date suffix (e.g. "-20250514")
        var normalized = DateSuffixRegex().Replace(model, "");
        if (BaseRates.ContainsKey(normalized))
            return normalized;

        return model;
    }

    /// <summary>Every model id this table prices. The catalog seed uses it to guarantee a row
    /// exists for each — a model with no row bills at <see cref="UnknownModelRates"/>.</summary>
    public static IReadOnlyCollection<string> PricedModelIds => BaseRates.Keys;

    /// <summary>The shipped rates for a model id, or null when the table doesn't price it.
    /// The catalog seeder's rate source — reading the live table beats duplicating it.</summary>
    public static (decimal Input, decimal Output)? TryGetBaseRates(string model) =>
        IsSubscriptionOnly(model) ? SubscriptionOnlyRates
        : BaseRates.TryGetValue(NormalizeModelId(model), out var rates) ? rates : null;

    /// <summary>Canonical id for rate lookups (strips a date suffix). Exposed so a DB-backed
    /// <c>IModelRateProvider</c> normalizes identically — otherwise a date-suffixed id misses
    /// its repriced row and silently bills at the shipped rate.</summary>
    public static string NormalizeForRates(string model) => NormalizeModelId(model);

    /// <param name="rates">Overrides the shipped rate table — supplied by
    /// <c>IModelRateProvider</c> when the model has admin-managed rates. Null falls through to
    /// the shipped table, then to <see cref="UnknownModelRates"/>.</param>
    /// <summary>Whether the shipped table carries a long-context or introductory tier for the
    /// model. Those tiers only apply while the effective rate equals the sticker here, so a
    /// vendor-synced price that differs silently switches them off — the sync reports that
    /// so the table gets a hand edit.</summary>
    public static bool HasCodeSideTiers(string model)
    {
        var normalized = NormalizeModelId(model);
        return LongContextRates.ContainsKey(normalized) || IntroductoryRates.ContainsKey(normalized);
    }

    /// <param name="at">Billing moment for time-bounded rates; tests pin it, callers leave it
    /// null for "now".</param>
    /// <param name="cacheRates">Absolute vendor cache prices per 1M tokens (read hit, write),
    /// as the vendor sync records them on the catalog row. When supplied they replace the
    /// multiplier tables below entirely; null keeps the shipped per-model/per-vendor
    /// multipliers.</param>
    public static decimal Calculate(
        string model, int inputTokens, int outputTokens,
        int cacheReadTokens = 0, int cacheWriteTokens = 0,
        (decimal Input, decimal Output)? rates = null,
        DateTimeOffset? at = null,
        (decimal Read, decimal Write)? cacheRates = null)
    {
        var normalized = NormalizeModelId(model);
        // Subscription-only namespaces are 0/0 whatever the caller passes: an admin-managed
        // rate row for a Copilot model would still be charging for tokens the vendor never
        // bills Orkka for, and a missing row must not fall through to UnknownModelRates.
        var (inputRate, outputRate) =
            IsSubscriptionOnly(model) ? SubscriptionOnlyRates
            : rates ?? BaseRates.GetValueOrDefault(normalized, UnknownModelRates);
        var isSticker = BaseRates.TryGetValue(normalized, out var sticker)
            && inputRate == sticker.Input && outputRate == sticker.Output;

        // The vendor's threshold counts the whole PROMPT, so every input-side token counts
        // toward it: fresh input, cached reads, and cache writes are all prompt tokens. Output
        // is not part of the prompt and is excluded.
        if (isSticker
            && LongContextRates.TryGetValue(normalized, out var longContext)
            && (long)inputTokens + cacheReadTokens + cacheWriteTokens >= longContext.MinPromptTokens)
        {
            (inputRate, outputRate) = (longContext.Input, longContext.Output);
        }
        // Sticker-equality check, not a blind override: see IntroductoryRates. No model has
        // both an intro price and a long-context tier today; if one ever does, the intro price
        // is the deliberate discount and should win, which is why it is applied second.
        else if (isSticker
            && IntroductoryRates.TryGetValue(normalized, out var intro)
            && (at ?? DateTimeOffset.UtcNow) < intro.Until)
        {
            (inputRate, outputRate) = (intro.Input, intro.Output);
        }

        // Cache pricing: a per-model override first (see CacheProfiles — one vendor can ship
        // two shapes), then the vendor default. Anthropic: cache reads bill at 10% of input,
        // cache writes at 125%. OpenAI (gpt-4o era): cached input is discounted (~50%) and
        // there is no separate cache-write charge — the codex invoker reports cached input as
        // cacheReadTokens and leaves cacheWriteTokens at 0, so the write term contributes
        // nothing there anyway. xAI: cached input is 25% of input, also with no write charge.
        var (cacheReadMult, cacheWriteMult) =
            CacheProfiles.TryGetValue(normalized, out var profile) ? profile
            : IsOpenAiModel(normalized) ? (0.5m, 0.0m)
            : IsXaiModel(normalized) ? (0.25m, 0.0m)
            : (0.1m, 1.25m);

        var cacheReadCost = cacheRates is { } absolute
            ? cacheReadTokens / 1_000_000m * absolute.Read
            : cacheReadTokens / 1_000_000m * inputRate * cacheReadMult;
        var cacheWriteCost = cacheRates is { } absoluteWrite
            ? cacheWriteTokens / 1_000_000m * absoluteWrite.Write
            : cacheWriteTokens / 1_000_000m * inputRate * cacheWriteMult;

        return
            (inputTokens / 1_000_000m * inputRate) +
            (outputTokens / 1_000_000m * outputRate) +
            cacheReadCost +
            cacheWriteCost;
    }

    public static string FormatCost(
        string model, int inputTokens, int outputTokens,
        (decimal Input, decimal Output)? rates = null)
    {
        var total = Calculate(model, inputTokens, outputTokens, rates: rates);
        return $"${total:F4}";
    }
}
