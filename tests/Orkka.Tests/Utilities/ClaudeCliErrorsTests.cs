namespace Orkka.Tests.Utilities;

using FluentAssertions;
using Orkka.SharedKernel.Ai;

public sealed class ClaudeCliErrorsTests
{
    [Theory]
    [InlineData("Control request timeout: initialize")]
    [InlineData("control request timeout: initialize")]
    [InlineData("Control request timeout: interrupt")]
    public void Describe_ControlRequestTimeout_ReturnsActionableMessage(string message)
    {
        var described = ClaudeCliErrors.Describe(message);

        described.Should().Be(ClaudeCliErrors.InitializeTimeoutMessage);
        described.Should().Contain("claude -p");
        described.Should().Contain("npm i -g @anthropic-ai/claude-code");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("exit code 1")]
    [InlineData("Failed to authenticate. API Error: 401")]
    public void Describe_UnrecognizedMessage_ReturnsNull(string? message)
    {
        ClaudeCliErrors.Describe(message).Should().BeNull();
    }

    [Theory]
    [InlineData("API Error: Connection closed mid-response. The response above may be incomplete.")]
    [InlineData("API Error: Connection lost mid-response. The response above may be incomplete.")]
    [InlineData("API Error: The response stopped arriving. The response above may be incomplete.")]
    [InlineData("API Error: Server error mid-response. The response above may be incomplete.")]
    [InlineData("API Error: Your computer went to sleep mid-response. The response above may be incomplete.")]
    [InlineData("connection closed mid-response")]
    [InlineData("Connection lost.")]
    [InlineData("Connection error.")]
    [InlineData("fetch failed")]
    [InlineData("read ECONNRESET")]
    [InlineData("socket hang up")]
    [InlineData("API Error: 529 {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}")]
    [InlineData("API Error: 500 {\"type\":\"error\",\"error\":{\"type\":\"api_error\",\"message\":\"Internal server error\"}}")]
    [InlineData("Agent process failed: Command failed (exit code: -1073740791)\nError output: Check stderr output for details")]
    [InlineData("Command failed (exit code: 3221226505)")]
    public void IsTransient_TransportAndAvailabilityBlips_True(string error)
    {
        ClaudeCliErrors.IsTransient(error).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Failed to authenticate. API Error: 401")]
    [InlineData("API Error: 429 rate limited")]
    [InlineData("The agent hit its maximum-turns limit before it could finish the reply.")]
    [InlineData("Credit balance is too low")]
    [InlineData("No conversation found with session ID 0199-abc")]
    public void IsTransient_PermanentFailures_False(string? error)
    {
        ClaudeCliErrors.IsTransient(error).Should().BeFalse();
    }

    [Theory]
    [InlineData("API Error: Connection lost mid-response. The response above may be incomplete.")]
    [InlineData("API Error: Connection closed mid-response. The response above may be incomplete.")]
    [InlineData("  API Error (Request timed out.) · Retrying in 4 seconds… (attempt 2/10)")]
    [InlineData("api error: 529 overloaded")]
    public void IsStreamErrorNotice_CliNotices_True(string text)
    {
        ClaudeCliErrors.IsStreamErrorNotice(text).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("I fixed the API error handling in the client.")]
    [InlineData("The API Error path is now covered by a retry loop.")]
    public void IsStreamErrorNotice_AgentProse_False(string? text)
    {
        ClaudeCliErrors.IsStreamErrorNotice(text).Should().BeFalse();
    }

    [Theory]
    [InlineData("error_max_turns")]
    [InlineData("success")]
    public void DescribeErrorResult_WithResultText_ReturnsTextUnchanged(string subtype)
    {
        ClaudeCliErrors.DescribeErrorResult(subtype, "API Error: 429 rate limited")
            .Should().Be("API Error: 429 rate limited");
    }

    [Fact]
    public void DescribeErrorResult_MaxTurns_ExplainsTheLimit()
    {
        var described = ClaudeCliErrors.DescribeErrorResult("error_max_turns", "");

        described.Should().Contain("maximum-turns limit");
    }

    [Fact]
    public void DescribeErrorResult_DuringExecution_SuggestsDebugging()
    {
        var described = ClaudeCliErrors.DescribeErrorResult("error_during_execution", null);

        described.Should().Contain("--debug");
    }

    [Theory]
    [InlineData(null, "none")]
    [InlineData("", "none")]
    [InlineData("error_weird_new_thing", "error_weird_new_thing")]
    public void DescribeErrorResult_UnknownSubtype_NamesTheSubtype(string? subtype, string expected)
    {
        ClaudeCliErrors.DescribeErrorResult(subtype, "")
            .Should().Be($"Unknown agent error (result subtype: {expected}).");
    }

    [Theory]
    [InlineData("API Error: Claude's response exceeded the 8192 output token maximum. To configure this behavior, set the CLAUDE_CODE_MAX_OUTPUT_TOKENS environment variable.")]
    [InlineData("claude's response exceeded the 32000 OUTPUT TOKEN MAXIMUM.")]
    public void IsOutputTokenCap_CliCapFailure_True(string error)
    {
        ClaudeCliErrors.IsOutputTokenCap(error).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Agent process failed: Command failed (exit code: 1)")]
    [InlineData("API Error: Connection lost mid-response. The response above may be incomplete.")]
    [InlineData("The agent hit its maximum-turns limit before it could finish the reply.")]
    [InlineData("API Error: 429 rate limited — output tokens per minute exceeded")]
    public void IsOutputTokenCap_OtherFailures_False(string? error)
    {
        ClaudeCliErrors.IsOutputTokenCap(error).Should().BeFalse();
    }

    [Fact]
    public void IsOutputTokenCap_IsNeverTransient()
    {
        // The retry paths key on IsTransient; the cap must not sneak in through it either.
        const string cap = "API Error: Claude's response exceeded the 8192 output token maximum.";

        ClaudeCliErrors.IsOutputTokenCap(cap).Should().BeTrue();
        ClaudeCliErrors.IsTransient(cap).Should().BeFalse();
    }
}
