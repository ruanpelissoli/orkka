namespace Orkka.SharedKernel;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orkka.SharedKernel.Ai;
using Orkka.SharedKernel.Caching;
using Orkka.SharedKernel.Documents;
using Orkka.SharedKernel.Events;
using Orkka.SharedKernel.Security;

public static class SharedKernelExtensions
{
    /// <summary>
    /// Cross-cutting services shared by every module: encryption, the cache, model defaults
    /// and the in-process integration event bus. Call once per host, before any AddXxxModule().
    /// The host supplies <see cref="Storage.IBlobStorageService"/> and <see cref="IUserDirectory"/>.
    /// </summary>
    public static IServiceCollection AddSharedKernel(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<IEncryptionService, AesEncryptionService>();

        // Read-through cache plus the invalidation seam. AddHybridCache registers IMemoryCache,
        // which the remaining IMemoryCache consumers (PDF sidecars) resolve.
        services.AddOrkkaCache(configuration);

        // Turns PDF attachments into agent-readable .txt siblings. Singleton so the
        // already-extracted lookup stays warm across prompt builds.
        services.AddSingleton<IPdfTextSidecarService, PdfTextSidecarService>();

        // Model defaults for utility completions (and the optional DefaultModel override).
        services.AddOptions<AnthropicOptions>()
            .Bind(configuration.GetSection(AnthropicOptions.SectionName));
        // Utility-call routing to the user's own runner — the only place utility calls run.
        // The no-op default misses everywhere (each feature takes its runner-offline path);
        // the host replaces it with RunnerLinkUtility.
        services.TryAddSingleton<IUserRunnerUtility, NoUserRunnerUtility>();

        // Per-model catalog rates. Replaced by the Agents module's DB-backed provider; the null
        // default keeps CostCalculator on its shipped table.
        services.AddScoped<IModelRateProvider, NullModelRateProvider>();

        services.AddInProcessEventBus();

        return services;
    }
}
