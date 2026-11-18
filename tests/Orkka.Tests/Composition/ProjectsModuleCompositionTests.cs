namespace Orkka.Tests.Composition;

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Projects;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.SourceControl;

/// <summary>
/// The Worker composes <c>AddProjectsModule</c> WITHOUT <c>AddProjectsModuleApi</c>, so anything
/// reachable from a both-hosts registration has to be registered in the both-hosts method too.
/// <see cref="RepoCloneSynchronizer"/> sat in the API-only method while <c>IProjectsApi</c> and
/// the Worker's brief-artifacts generator both needed it — a gap that only shows up as a
/// resolution failure at runtime, in one host, on the paths that clone repos.
/// </summary>
public sealed class ProjectsModuleCompositionTests
{
    // Never opened: EF only parses it at registration time.
    private static ServiceProvider BuildWorkerHostServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = Orkka.SharedKernel.Persistence.SqliteDatabase.ConnectionStringFor(
                    Path.Combine(Path.GetTempPath(), "orkka-composition-never-opened.db"))
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSourceControlModule(configuration);
        services.AddProjectsModule(configuration);

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true
        });
    }

    [Fact]
    public void ProjectsApi_ResolvesInTheWorkerHost()
    {
        using var provider = BuildWorkerHostServices();
        using var scope = provider.CreateScope();

        var act = () => scope.ServiceProvider.GetRequiredService<IProjectsApi>();

        act.Should().NotThrow(
            "the Worker registers IProjectsApi via AddProjectsModule and never calls AddProjectsModuleApi");
    }

    [Fact]
    public void RepoCloneSynchronizer_ResolvesInTheWorkerHost()
    {
        using var provider = BuildWorkerHostServices();
        using var scope = provider.CreateScope();

        var act = () => scope.ServiceProvider.GetRequiredService<RepoCloneSynchronizer>();

        act.Should().NotThrow(
            "every repo-reading path goes through it first, and those paths exist in both hosts");
    }
}
