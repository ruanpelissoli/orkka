namespace Orkka.Tests.Services;

using FluentAssertions;
using Orkka.LocalRunner.Cli;

/// <summary>
/// ConsoleStatus is the runner's end-user connection narration: the ready block must
/// print exactly once (reconnects get a short "Connected." instead), and debug mode
/// must silence it entirely — structured logs cover those events there.
/// </summary>
public sealed class ConsoleStatusTests
{
    [Fact]
    public void First_connect_prints_ready_block_once()
    {
        var output = new StringWriter();
        var status = new ConsoleStatus(enabled: true, output);

        status.Connected("http://localhost:6750");
        status.Connected("http://localhost:6750");

        var text = output.ToString();
        text.Should().ContainAll("Orkka is ready — open http://localhost:6750", "--debug");
        text.Split("Orkka is ready").Should().HaveCount(2); // ready block exactly once
        text.Should().Contain("Connected.");
    }

    [Fact]
    public void Reconnect_lifecycle_prints_friendly_lines()
    {
        var output = new StringWriter();
        var status = new ConsoleStatus(enabled: true, output);

        status.Connected("http://localhost:6750");
        status.Reconnecting();
        status.ConnectionLost();

        output.ToString().Should().ContainAll("reconnecting", "retrying");
    }

    [Fact]
    public void Disabled_status_prints_nothing()
    {
        var output = new StringWriter();
        var status = new ConsoleStatus(enabled: false, output);

        status.Connected("http://localhost:6750");
        status.Reconnecting();
        status.ConnectionLost();

        output.ToString().Should().BeEmpty();
    }
}
