namespace Orkka.Tests.Architecture;

using System.Reflection;
using FluentAssertions;

/// <summary>
/// Enforces the modular monolith reference rules:
///   module  -> its own internals, any *.Contracts, SharedKernel, Orkka.Git, AgentRuntime
///   *.Contracts -> SharedKernel only
///   AgentRuntime -> SharedKernel, Orkka.Git and *.Contracts only (shared agent-execution tier consumed
///                   by both the Execution module and the Orkka Local runner)
/// Hosts (AppHost, Worker, Migrator, Local.Host, LocalRunner) are composition roots and may
/// reference everything, so they are not checked here. A failure means a module reached into
/// another module's internals — use that module's Contracts project instead.
/// </summary>
public sealed class ModuleBoundaryTests
{
    private static readonly Assembly[] ModuleAssemblies =
    [
        typeof(Orkka.Modules.Organizations.OrganizationsModuleExtensions).Assembly,
        typeof(Orkka.Modules.Usage.UsageModuleExtensions).Assembly,
        typeof(Orkka.Modules.SourceControl.SourceControlModuleExtensions).Assembly,
        typeof(Orkka.Modules.Projects.ProjectsModuleExtensions).Assembly,
        typeof(Orkka.Modules.Agents.AgentsModuleExtensions).Assembly,
        typeof(Orkka.Modules.Tasks.TasksModuleExtensions).Assembly,
        typeof(Orkka.Modules.Execution.ExecutionModuleExtensions).Assembly,
        typeof(Orkka.Modules.Trackers.TrackersModuleExtensions).Assembly,
    ];

    private static readonly Assembly[] ContractAssemblies =
    [
        typeof(Orkka.Modules.Organizations.Contracts.IOrgMembershipService).Assembly,
        typeof(Orkka.Modules.Usage.Contracts.IUsageRecorder).Assembly,
        typeof(Orkka.Modules.SourceControl.Contracts.IPullRequestService).Assembly,
        typeof(Orkka.Modules.Projects.Contracts.IProjectsApi).Assembly,
        typeof(Orkka.Modules.Agents.Contracts.IAgentsApi).Assembly,
        typeof(Orkka.Modules.Tasks.Contracts.ITasksApi).Assembly,
    ];

    [Fact]
    public void Modules_MustNotReference_OtherModuleInternals()
    {
        foreach (var module in ModuleAssemblies)
        {
            var orkkaRefs = module.GetReferencedAssemblies()
                .Where(a => a.Name!.StartsWith("Orkka.", StringComparison.Ordinal))
                .Select(a => a.Name!)
                .ToList();

            orkkaRefs.Should().AllSatisfy(name =>
                (name.EndsWith(".Contracts", StringComparison.Ordinal)
                    || name is "Orkka.SharedKernel" or "Orkka.AgentRuntime" or "Orkka.Git")
                    .Should().BeTrue(
                        $"{module.GetName().Name} must not reference {name} — modules may only " +
                        "reference *.Contracts assemblies, Orkka.SharedKernel, Orkka.Git and Orkka.AgentRuntime"));
        }
    }

    [Fact]
    public void AgentRuntime_MustReference_ContractsAndSharedKernelOnly()
    {
        var agentRuntime = typeof(Orkka.Modules.Execution.Agents.IAiAgentInvoker).Assembly;

        agentRuntime.GetName().Name.Should().Be("Orkka.AgentRuntime",
            "IAiAgentInvoker moved to the shared agent-execution assembly");

        agentRuntime.GetReferencedAssemblies()
            .Where(a => a.Name!.StartsWith("Orkka.", StringComparison.Ordinal))
            .Select(a => a.Name!)
            .Should().AllSatisfy(name =>
                (name.EndsWith(".Contracts", StringComparison.Ordinal)
                    || name is "Orkka.SharedKernel" or "Orkka.Git")
                    .Should().BeTrue(
                        $"Orkka.AgentRuntime must not reference {name} — it is consumed by the " +
                        "Orkka Local runner and must stay free of module internals"));
    }

    [Fact]
    public void Contracts_MustReference_SharedKernelOnly()
    {
        foreach (var contracts in ContractAssemblies)
        {
            var orkkaRefs = contracts.GetReferencedAssemblies()
                .Where(a => a.Name!.StartsWith("Orkka.", StringComparison.Ordinal))
                .Select(a => a.Name!)
                .ToList();

            orkkaRefs.Should().AllSatisfy(name =>
                (name == "Orkka.SharedKernel")
                    .Should().BeTrue(
                        $"{contracts.GetName().Name} must not reference {name} — contract " +
                        "assemblies stay dependency-light so any module can consume them"));
        }
    }

    [Fact]
    public void SharedKernel_MustNotReference_AnyModule()
    {
        var sharedKernel = typeof(Orkka.SharedKernel.SharedKernelExtensions).Assembly;

        sharedKernel.GetReferencedAssemblies()
            .Where(a => a.Name!.StartsWith("Orkka.", StringComparison.Ordinal))
            .Should().BeEmpty("SharedKernel is the base of the dependency graph");
    }
}
