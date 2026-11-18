namespace Orkka.Tests.Architecture;

using System.Reflection;
using Orkka.SharedKernel.Domain;
using FluentAssertions;

/// <summary>Entity conventions, swept across every module assembly.</summary>
public sealed class EntityConventionTests
{
    private static readonly Assembly[] ModuleAssemblies =
    [
        typeof(Orkka.Modules.Organizations.OrganizationsModuleExtensions).Assembly,
        typeof(Orkka.Modules.Usage.UsageModuleExtensions).Assembly,
        typeof(Orkka.Modules.SourceControl.SourceControlModuleExtensions).Assembly,
        typeof(Orkka.Modules.Projects.ProjectsModuleExtensions).Assembly,
        typeof(Orkka.Modules.Agents.AgentsModuleExtensions).Assembly,
        typeof(Orkka.Modules.Tasks.TasksModuleExtensions).Assembly,
    ];

    private static IEnumerable<Type> AllEntities() => ModuleAssemblies
        .SelectMany(a => a.GetTypes())
        .Where(t => t is { IsClass: true, IsAbstract: false } && t.IsSubclassOf(typeof(BaseEntity)));

    [Fact]
    public void ModuleAssemblies_ContainEntities()
    {
        // Guards against this suite going vacuous after refactors.
        AllEntities().Count().Should().BeGreaterThan(20);
    }

    [Fact]
    public void AllTenantEntities_ShouldImplementITenantEntity()
    {
        AllEntities().Where(t => t.IsSubclassOf(typeof(TenantEntity)))
            .Should().AllSatisfy(t => t.Should().Implement<ITenantEntity>());
    }

    [Fact]
    public void AllEntities_ShouldBeSealed()
    {
        AllEntities().Should().AllSatisfy(t =>
            t.IsSealed.Should().BeTrue($"{t.Name} should be sealed"));
    }

    [Fact]
    public void AllEntities_ShouldImplementIAuditableEntity()
    {
        AllEntities().Should().AllSatisfy(t => t.Should().Implement<IAuditableEntity>());
    }
}
