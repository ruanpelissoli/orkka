namespace Orkka.Git;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

/// <summary>Where this host's git runs, as the ledger names it — see <see cref="GitTopologies"/>.</summary>
public sealed record GitTopology(string Name);

public static class GitServiceCollectionExtensions
{
    /// <summary>
    /// Registers the engine for a host: one <see cref="GitProcess"/>, the host's
    /// <see cref="GitTopology"/>, and — unless a module registers a persistent one — a logging
    /// <see cref="IGitOperationLedger"/>. Every module that runs git resolves these; none
    /// constructs its own.
    /// </summary>
    public static IServiceCollection AddOrkkaGit(this IServiceCollection services, string topology)
    {
        services.TryAddSingleton<GitProcess>();
        services.TryAddSingleton(new GitTopology(topology));
        services.TryAddSingleton<IGitOperationLedger>(sp =>
            new LoggingGitOperationLedger(
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("Orkka.Git.Ledger"), topology));
        return services;
    }
}
