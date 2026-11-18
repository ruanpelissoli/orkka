namespace Orkka.Tests.Local;

using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orkka.Local;
using Orkka.Local.Infrastructure;
using Orkka.Local.Storage;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Features.Hubs;
using Orkka.Runners.Server;
using Orkka.SharedKernel.Ai;
using Orkka.SharedKernel.Storage;

/// <summary>
/// Orkka.Local merges what used to be two processes. The registrations that differ between
/// them are the ones a wrong order silently breaks — the old Worker's SignalR-client notifier
/// dialling a hub in its own process, or the API host's HTTP relay standing in for the
/// in-process runner link. These pin what the composed container actually resolves.
/// </summary>
public sealed class LocalCompositionTests
{
    private static ServiceProvider Compose() => ComposeServices().BuildServiceProvider();

    private static ServiceCollection ComposeServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Never opened: nothing here touches the database.
                ["ConnectionStrings:DefaultConnection"] = Orkka.SharedKernel.Persistence.SqliteDatabase.ConnectionStringFor(
                    Path.Combine(Path.GetTempPath(), "orkka-composition-never-opened.db")),
                ["Encryption:Key"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                ["WorkerAuth:ApiKey"] = "k",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddOrkkaLocal(configuration,
            new LocalSettings(6750, Path.Combine(Path.GetTempPath(), "orkka-composition"), "ui-dist", "s"));
        return services;
    }

    [Fact]
    public void TaskNotifier_IsTheDirectHubNotifier()
    {
        using var provider = Compose();

        provider.GetRequiredService<ITaskNotifier>().Should().BeOfType<TaskNotifier>(
            "the TaskHub lives in this process; the Worker's SignalR-client notifier would dial itself");
        provider.GetServices<ITaskNotifier>().Should().ContainSingle();
    }

    [Fact]
    public void UserRunnerUtility_IsTheInProcessRunnerLink()
    {
        using var provider = Compose();

        provider.GetRequiredService<IUserRunnerUtility>().Should().BeOfType<RunnerLinkUtility>(
            "it must replace AddRunnersModule's RelayUserRunnerUtility, an HTTP hop to a Worker");
    }

    [Fact]
    public void HostServices_AreTheLocalOnes()
    {
        using var provider = Compose();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IBlobStorageService>().Should().BeOfType<FileSystemBlobStorageService>();
        scope.ServiceProvider.GetRequiredService<IUserDirectory>().Should().BeOfType<LocalUserDirectory>();
        scope.ServiceProvider.GetRequiredService<Orkka.Runners.Server.AppRun.IAppRunEvents>()
            .Should().BeOfType<LocalAppRunEvents>();
        scope.ServiceProvider.GetRequiredService<Orkka.Modules.Runners.Contracts.IRunnerClaimAffinity>()
            .Should().BeOfType<Orkka.Runners.Server.Hosting.WorkerRunnerClaimAffinity>();
    }

    [Fact]
    public void OnlyTheLocalEngine_IsComposed()
    {
        var services = ComposeServices();

        var hosted = services
            .Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService))
            .Select(d => d.ImplementationType?.Name)
            .OfType<string>()
            .ToList();

        hosted.Should().Contain("TaskQueueProcessor");
    }
}
