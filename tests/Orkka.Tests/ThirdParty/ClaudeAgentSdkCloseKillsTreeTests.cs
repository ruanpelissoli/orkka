namespace Orkka.Tests.ThirdParty;

using System.Diagnostics;
using FluentAssertions;
using ih0.Claude.Agent.SDK;
using ih0.Claude.Agent.SDK.Internal.Transport;
using Orkka.Tests.Worker;

/// <summary>
/// [orkka patch 5] The Claude CLI starts shells, MCP servers and test runners of its own. Closing
/// the transport must end them with it — a plain <c>Process.Kill()</c> left them running after
/// `./orkka stop` (R5, 2026-11-18) — and every CLI the transport starts is announced through
/// <see cref="SubprocessCliTransport.ProcessStarted"/> so the runner can end it on shutdown.
/// </summary>
[Collection(AgentProcessesCollection.Name)]
public sealed class ClaudeAgentSdkCloseKillsTreeTests
{
    [Fact]
    public async Task Close_KillsTheCliAndEverythingItStarted_AndConnectAnnouncesTheProcess()
    {
        using var probe = new ProcessTreeProbe();
        var previous = SubprocessCliTransport.ProcessStarted;
        Process? announced = null;
        SubprocessCliTransport.ProcessStarted = p => announced = p;
        try
        {
            var transport = new SubprocessCliTransport("hi", new ClaudeAgentOptions { CliPath = probe.Script });
            await transport.ConnectAsync();
            announced.Should().NotBeNull("the runner books every CLI the SDK starts");
            var pid = announced!.Id;
            await probe.WaitForChildAsync();

            await transport.CloseAsync();

            (await probe.ChildAliveAsync(TimeSpan.FromSeconds(10))).Should().BeFalse("the CLI's children die with it");
            ProcessGone(pid).Should().BeTrue();
        }
        finally
        {
            SubprocessCliTransport.ProcessStarted = previous;
        }
    }

    private static bool ProcessGone(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
