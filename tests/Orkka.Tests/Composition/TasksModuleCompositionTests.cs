namespace Orkka.Tests.Composition;

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Orkka.Modules.Agents;
using Orkka.Modules.Usage;
using Orkka.Modules.Organizations;
using Orkka.Modules.Projects;
using Orkka.Modules.SourceControl;
using Orkka.Modules.Tasks;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Infrastructure.Services;
using Orkka.SharedKernel;

/// <summary>
/// Milestone planning runs on a five-second background poll, so a missing registration would
/// not fail a build, a test, or a request — it would fail once, inside a fire-and-forget task,
/// on a queue nobody is watching, and every plan after it would sit Pending forever. That is
/// exactly the failure mode worth catching at composition time instead.
/// </summary>
public sealed class TasksModuleCompositionTests
{
    // Never opened: nothing here touches the database.
    private static ServiceProvider BuildWorkerHostServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] =
                    Orkka.SharedKernel.Persistence.SqliteDatabase.ConnectionStringFor(
                    Path.Combine(Path.GetTempPath(), "orkka-composition-never-opened.db")),
                // Options validation runs when the decomposer is constructed, and every host
                // that composes this queue supplies a key. Never used — nothing here calls out.
                ["Anthropic:ApiKey"] = "sk-ant-test"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSharedKernel(configuration);
        services.AddOrganizationsModule(configuration);
        services.AddUsageModule(configuration);
        services.AddSourceControlModule(configuration);
        services.AddProjectsModule(configuration);
        services.AddAgentsModule(configuration);
        services.AddTasksModule(configuration);
        services.AddTasksModuleWorker();

        // The Worker's SignalR-backed notifier comes from the Execution module, which drags in
        // agent invokers and a runner hub this test has no business composing. The seam is all
        // that matters here: something must satisfy it.
        services.AddSingleton(Mock.Of<ITaskNotifier>());

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true
        });
    }

    [Fact]
    public void MilestonePlanRunner_ResolvesInTheWorkerHost()
    {
        using var provider = BuildWorkerHostServices();
        using var scope = provider.CreateScope();

        var act = () => scope.ServiceProvider.GetRequiredService<MilestonePlanRunner>();

        act.Should().NotThrow(
            "the queue processor resolves it per job inside a fire-and-forget task, where a "
            + "missing dependency would only ever appear as plans that never start");
    }

    [Fact]
    public void MilestonePlanQueueProcessor_IsHostedByTheWorker()
    {
        using var provider = BuildWorkerHostServices();

        provider.GetServices<IHostedService>()
            .Should().ContainSingle(s => s is MilestonePlanQueueProcessor,
                "AddTasksModuleWorker composes the planning queue — without it the endpoint "
                + "queues jobs nobody ever picks up, which is worse than the synchronous call "
                + "it replaced");
    }
}
