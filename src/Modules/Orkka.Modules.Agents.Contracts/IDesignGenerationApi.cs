namespace Orkka.Modules.Agents.Contracts;

/// <summary>Brand-kit read access for callers outside the Agents module (task prompts).</summary>
public interface IDesignGenerationApi
{
    /// <summary>The project's brand kit, or null when the project has none.</summary>
    Task<BrandKitSummary?> GetBrandKitAsync(
        Ulid orgId, Ulid projectId, CancellationToken ct = default);
}
