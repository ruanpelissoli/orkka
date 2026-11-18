namespace Orkka.Modules.Usage;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orkka.Modules.Usage.Contracts;
using Orkka.Modules.Usage.Infrastructure;
using Orkka.Modules.Usage.Infrastructure.Services;
using Orkka.SharedKernel.Persistence;

public static class UsageModuleExtensions
{
    /// <summary>DbContext + migration registration.</summary>
    public static IServiceCollection AddUsagePersistence(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<UsageDbContext>(configuration, UsageDbContext.Module);
        return services;
    }

    /// <summary>Full module: persistence + the usage recorder and its read side.</summary>
    public static IServiceCollection AddUsageModule(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddUsagePersistence(configuration);
        services.AddScoped<IUsageRecorder, UsageRecorder>();
        services.AddScoped<IUsageQuery, UsageQuery>();
        return services;
    }
}
