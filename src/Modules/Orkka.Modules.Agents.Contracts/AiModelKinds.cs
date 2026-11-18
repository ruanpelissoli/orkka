namespace Orkka.Modules.Agents.Contracts;

/// <summary>What a catalog model is for. Chat models drive agent runs and are what users pick
/// on the Team page / task form. Image is legacy: it backed the retired server-side image
/// generation and stays only so old rows load — no image row is listed, seeded, synced or
/// accepted by the admin editor. Stored by name — append, never reorder.</summary>
public enum AiModelKind
{
    Chat,
    Image
}

/// <summary>The tiers the system (not the user) picks a model for. Fast = titles, rolling
/// summaries, dependency analysis. Utility = task decomposition and structured one-shots.
/// Capable = merge-conflict resolution, dev/reviewer agent seeds. Qa = the QA agent, which
/// stays on a Claude model regardless of the project's provider because the QA phase drives
/// Playwright MCP through the Claude Agent SDK. Stored by name — append, never reorder.</summary>
public enum AiModelTier
{
    Fast,
    Utility,
    Capable,
    Qa
}

/// <summary>Who owns a catalog row's billing rates. The vendor sync overwrites Seed and Vendor
/// rows with the price feed; an Admin row is an explicit override and is never touched by
/// sync ("Reset to vendor" flips it back). Stored by name — append, never reorder.</summary>
public enum AiModelRateSource
{
    /// <summary>Copied from the shipped CostCalculator table by AiModelSeeder.</summary>
    Seed,
    /// <summary>Written by the vendor price sync.</summary>
    Vendor,
    /// <summary>Set by a platform admin in ModelManager; sync keeps its hands off.</summary>
    Admin
}

/// <summary>How a catalog row came to exist. Stored by name — append, never reorder.</summary>
public enum AiModelOrigin
{
    Seed,
    Sync,
    Admin
}

