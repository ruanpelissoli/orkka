namespace Orkka.SharedKernel.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Registered by each module's persistence setup; consumed by the Migrator and dev auto-migrate.</summary>
public interface IModuleMigrationRegistry
{
    IReadOnlyList<Type> ContextTypes { get; }
}

public sealed record ModuleMigrationEntry(Type ContextType);

internal sealed class ModuleMigrationRegistry(IEnumerable<ModuleMigrationEntry> entries) : IModuleMigrationRegistry
{
    public IReadOnlyList<Type> ContextTypes { get; } = [.. entries.Select(e => e.ContextType).Distinct()];
}

public static class ModuleMigrationExtensions
{
    /// <summary>Registers a module DbContext with the migration registry. Call inside AddXxxModule/AddXxxPersistence.</summary>
    public static IServiceCollection AddModuleMigration<TContext>(this IServiceCollection services)
        where TContext : ModuleDbContext
    {
        services.AddSingleton(new ModuleMigrationEntry(typeof(TContext)));
        services.TryAddModuleMigrationRegistry();
        return services;
    }

    private static void TryAddModuleMigrationRegistry(this IServiceCollection services)
    {
        if (!services.Any(d => d.ServiceType == typeof(IModuleMigrationRegistry)))
            services.AddSingleton<IModuleMigrationRegistry, ModuleMigrationRegistry>();
    }

    /// <summary>Applies pending migrations for every registered module context, in registration order.</summary>
    public static async Task ApplyModuleMigrationsAsync(this IServiceProvider serviceProvider, CancellationToken ct = default)
    {
        var registry = serviceProvider.GetRequiredService<IModuleMigrationRegistry>();

        foreach (var contextType in registry.ContextTypes)
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            var db = (DbContext)scope.ServiceProvider.GetRequiredService(contextType);
            await db.Database.MigrateAsync(ct);
        }
    }
}
