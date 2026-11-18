namespace Orkka.Tests.Worker;

using System.Text.Json;
using FluentAssertions;
using Orkka.SharedKernel.Ai;

public sealed class ClaudeMcpInitStatusTests
{
    private static JsonElement Data(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Read_ReturnsEveryServerWithItsStatus()
    {
        var data = Data("""{"cwd":"/w","mcp_servers":[{"name":"orkka","status":"failed"},{"name":"playwright","status":"connected"}]}""");

        ClaudeMcpInitStatus.Read(data).Should().Equal(
            new ClaudeMcpInitStatus.McpServerStatus("orkka", "failed"),
            new ClaudeMcpInitStatus.McpServerStatus("playwright", "connected"));
    }

    [Fact]
    public void Unavailable_ReportsFailedAndMissing_NotConnected()
    {
        var data = Data("""{"mcp_servers":[{"name":"orkka","status":"failed"},{"name":"playwright","status":"connected"}]}""");

        var unavailable = ClaudeMcpInitStatus.Unavailable(data, ["orkka", "playwright", "ghost"]);

        unavailable.Should().Equal(
            new ClaudeMcpInitStatus.McpServerStatus("orkka", "failed"),
            new ClaudeMcpInitStatus.McpServerStatus("ghost", ClaudeMcpInitStatus.Missing));
    }

    [Fact]
    public void Unavailable_NoRosterInPayload_IsNotEvidence()
    {
        // An init event without mcp_servers (older CLI, odd payload) must not raise alarms.
        ClaudeMcpInitStatus.Unavailable(Data("""{"cwd":"/w"}"""), ["orkka"]).Should().BeEmpty();
        ClaudeMcpInitStatus.Unavailable(Data("""{"mcp_servers":"nope"}"""), ["orkka"]).Should().BeEmpty();
    }

    [Fact]
    public void Unavailable_ToleratesMalformedEntries()
    {
        var data = Data("""{"mcp_servers":[null,{"status":"failed"},{"name":"orkka"},{"name":"ok","status":"CONNECTED"}]}""");

        ClaudeMcpInitStatus.Unavailable(data, ["orkka", "ok"]).Should().Equal(
            new ClaudeMcpInitStatus.McpServerStatus("orkka", ""));
    }
}
