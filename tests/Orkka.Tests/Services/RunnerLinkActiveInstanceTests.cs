namespace Orkka.Tests.Services;

using Orkka.LocalRunner.Contracts;
using Orkka.Runners.Server;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

/// <summary>
/// The ticket-source poll lease is handed to the owner's ACTIVE runner at once: a restarted
/// runner (new InstanceId, new connection) replaced the old one, whose lease would otherwise
/// pause intake for five minutes after every restart (e2e, 2026-11-17).
/// </summary>
public sealed class RunnerLinkActiveInstanceTests
{
    private static RunnerHello Hello(string instanceId) => new(
        RunnerProtocol.ProtocolVersion, "dev-pc", true, true, [], [],
        Capabilities: [RunnerCapabilities.Hybrid], InstanceId: instanceId);

    private static RunnerLink NewLink() => new(new Mock<IHubClients>().Object, NullLogger<RunnerLink>.Instance,
        new RunnerLinkMessages("Your runner is not connected.", "disconnected mid-run"));

    [Fact]
    public void TheReplacingConnection_IsActive_AndTheReplacedOneIsNot()
    {
        var link = NewLink();
        link.SetRunner("conn-old", Hello("old"));
        link.SetRunner("conn-new", Hello("new"));

        link.IsActiveInstance("conn-new", "new").Should().BeTrue();
        link.IsActiveInstance("conn-old", "old").Should().BeFalse("it was replaced");
    }

    [Fact]
    public void AnInstanceIdThatDoesNotMatchTheConnectionsHello_IsNotActive()
    {
        var link = NewLink();
        link.SetRunner("conn-1", Hello("a"));

        link.IsActiveInstance("conn-1", "b").Should().BeFalse();
        link.IsActiveInstance("conn-1", null).Should().BeFalse();
    }

    [Fact]
    public void ADisconnectedLink_HasNoActiveInstance()
    {
        var link = NewLink();
        link.SetRunner("conn-1", Hello("a"));
        link.OnDisconnected("conn-1");

        link.IsActiveInstance("conn-1", "a").Should().BeFalse();
    }
}
