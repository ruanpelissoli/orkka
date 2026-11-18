namespace Orkka.SharedKernel.Caching;

/// <summary>
/// Cache configuration. Every knob is optional: with no configuration at all the cache is a
/// pure in-process cache, which is all a single local process needs.
/// </summary>
public sealed class CacheOptions
{
    public const string SectionName = "Cache";

    /// <summary>
    /// Master kill switch. False keeps <c>HybridCache</c> registered but makes every entry
    /// bypass it, so a misbehaving cache can be turned off by config.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Baked into every key, so a change to the shape of a cached payload can be isolated by
    /// bumping one value.
    /// </summary>
    public string KeyVersion { get; set; } = "1";
}
