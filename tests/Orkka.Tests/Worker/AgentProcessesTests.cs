namespace Orkka.Tests.Worker;

using FluentAssertions;
using Orkka.Modules.Execution.Agents;

/// <summary>
/// Stopping Orkka with an agent in flight must not leave the agent's CLI, or anything it started,
/// running (R5, 2026-11-18: claude.exe and its MCP servers outlived `./orkka stop`).
/// <see cref="AgentProcesses"/> is the backstop the runner uses after it has cancelled its runs.
/// </summary>
[Collection(AgentProcessesCollection.Name)]
public sealed class AgentProcessesTests
{
    [Fact]
    public async Task KillAll_EndsATrackedProcessAndItsChildren()
    {
        using var probe = new ProcessTreeProbe();
        using var process = probe.Start();
        AgentProcesses.Track(process);
        await probe.WaitForChildAsync();

        var killed = AgentProcesses.KillAll();

        killed.Should().BeGreaterThanOrEqualTo(1);
        process.WaitForExit(10_000).Should().BeTrue();
        (await probe.ChildAliveAsync(TimeSpan.FromSeconds(10))).Should().BeFalse("the CLI's children die with it");
        AgentProcesses.Count.Should().Be(0);
    }

    [Fact]
    public async Task KillAll_SkipsProcessesThatAlreadyExited()
    {
        using var probe = new ProcessTreeProbe();
        using var process = probe.Start();
        AgentProcesses.Track(process);
        await probe.WaitForChildAsync();
        process.Kill(entireProcessTree: true);
        process.WaitForExit(10_000).Should().BeTrue();

        AgentProcesses.KillAll().Should().Be(0);
    }

    [Fact]
    public void Track_ADisposedProcess_IsIgnored()
    {
        using var probe = new ProcessTreeProbe();
        var process = probe.Start();
        process.Kill(entireProcessTree: true);
        process.WaitForExit(10_000);
        process.Dispose();

        var act = () => AgentProcesses.Track(process);

        act.Should().NotThrow();
        AgentProcesses.KillAll().Should().Be(0);
    }
}

/// <summary><see cref="AgentProcesses"/> and the SDK's spawn hook are process-wide: their tests
/// must not run in parallel with each other.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AgentProcessesCollection
{
    public const string Name = "AgentProcesses (process-wide)";
}
