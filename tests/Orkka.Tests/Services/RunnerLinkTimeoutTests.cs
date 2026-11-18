namespace Orkka.Tests.Services;

using FluentAssertions;
using Orkka.LocalRunner;
using Orkka.Runners.Server;

/// <summary>Both ends of the runner link wait the same two minutes before calling the other
/// side dead — SignalR's 30-second defaults dropped a busy laptop four times in twenty
/// minutes (prod, 2026-09-17), failing every run in flight each time.</summary>
public sealed class RunnerLinkTimeoutTests
{
    [Fact]
    public void TheHubAndTheRunner_AgreeOnATimeoutThatRidesOutABusySpell()
    {
        RunnerHubTimeouts.ClientTimeout.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(2));
        RunnerSession.HostTimeout.Should().Be(RunnerHubTimeouts.ClientTimeout);
    }
}
