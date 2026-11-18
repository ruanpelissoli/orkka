namespace Orkka.Tests.Features.Agents;

using FluentAssertions;
using Orkka.Modules.Execution.Agents;

/// <summary>
/// The auth classifier decides two things at once: what the user reads in the failed run's
/// timeline, and whether the runner stops offering the provider until they sign back in.
/// So the per-CLI signature tables must catch each CLI's real "not signed in" wording, and
/// — just as important — everything transient (overloads, rate limits, dropped streams,
/// budget caps) must stay null, or a flaky afternoon would lock a signed-in provider out.
/// </summary>
public sealed class CliAuthFailureTests
{
    [Theory]
    [InlineData("codex", "Error: Not logged in. Run `codex login` to authenticate.")]
    [InlineData("codex", "unexpected status 401 Unauthorized")]
    [InlineData("codex", "{\"error\":{\"message\":\"Invalid API key provided\"}}")]
    [InlineData("codex", "OAuth refresh failed: invalid_grant")]
    [InlineData("gemini", "[API Error: 16 UNAUTHENTICATED: Request had invalid authentication credentials.]")]
    [InlineData("gemini", "Error: PERMISSION_DENIED: the caller does not have permission")]
    [InlineData("gemini", "API key not valid. Please pass a valid API key.")]
    [InlineData("gemini", "IneligibleTierError: this account is not eligible for the free tier")]
    [InlineData("copilot", "Error: You are not logged in. Run `copilot login` first.")]
    [InlineData("copilot", "GitHub authentication failed — token is invalid")]
    [InlineData("copilot", "HTTP 401: Bad credentials (https://api.github.com/user)")]
    [InlineData("grok", "Error: invalid api key")]
    [InlineData("grok", "Not signed in — run `grok` to log in.")]
    [InlineData("grok", "401 Unauthorized")]
    [InlineData("claude", "API Error: 401 {\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"Invalid authentication credentials\"}}")]
    [InlineData("claude", "Not logged in · Please sign in to continue")]
    [InlineData("claude", "OAuth token expired. Please run /login")]
    [InlineData("ollama", "unexpected status 401 Unauthorized")]
    public void Classify_RecognisesAuthShapedFailures(string cli, string stderr)
    {
        var message = CliAuthFailure.Classify(cli, 1, stderr, null);

        message.Should().NotBeNull();
        message.Should().StartWith($"{cli} refused this run because it isn't signed in on this machine (");
        message.Should().Contain($"Run `{CliAuthFailure.LoginCommand(cli)}` there, then retry.");
    }

    [Theory]
    [InlineData("codex", "API Error: 529 overloaded_error: Overloaded")]
    [InlineData("codex", "rate_limit_exceeded: You exceeded your current quota, please retry in 20s")]
    [InlineData("gemini", "Error: 503 Service Unavailable. The model is overloaded.")]
    [InlineData("gemini", "Error: 429 RESOURCE_EXHAUSTED: Quota exceeded for quota metric")]
    [InlineData("copilot", "read ECONNRESET")]
    [InlineData("copilot", "Connection lost mid-response. The response above may be incomplete.")]
    [InlineData("grok", "context window exceeded: prompt is 240123 tokens")]
    [InlineData("claude", "API Error: Claude's response exceeded the 4012 output token maximum")]
    [InlineData("codex", "wrote 4013 bytes to auth.log")]
    [InlineData("codex", "")]
    public void Classify_LeavesTransientAndUnrelatedFailuresAlone(string cli, string stderr)
    {
        // "4012 output token" and "4013 bytes" carry the digits without being an HTTP
        // status — the word-bounded match must not fire on them.
        CliAuthFailure.Classify(cli, 1, stderr, null).Should().BeNull();
    }

    [Fact]
    public void Classify_QuotesTheFirstMatchingStderrLine_NotTheWholeTail()
    {
        var stderr = string.Join('\n',
            "warning: update available (1.2.3 -> 1.2.4)",
            "  Error: Not logged in. Run `codex login`.  ",
            "    at main (index.js:12)");

        var message = CliAuthFailure.Classify("codex", 1, stderr, null);

        message.Should().Be(
            "codex refused this run because it isn't signed in on this machine "
            + "(Error: Not logged in. Run `codex login`.). Run `codex login` there, then retry.");
    }

    [Fact]
    public void Classify_FallsBackToTheStreamError_WhenStderrIsClean()
    {
        var message = CliAuthFailure.Classify(
            "gemini", 0, "", "UNAUTHENTICATED: Request had invalid authentication credentials");

        message.Should().NotBeNull();
        message.Should().Contain("Run `gemini` there");
    }

    [Fact]
    public void Classify_CapsAVeryLongEvidenceLine()
    {
        var line = "401 Unauthorized " + new string('x', 600);

        var message = CliAuthFailure.Classify("codex", 1, line, null)!;

        message.Length.Should().BeLessThan(400);
        message.Should().Contain("…");
    }

    [Theory]
    [InlineData("codex", "codex login")]
    [InlineData("gemini", "gemini")]
    [InlineData("copilot", "copilot login")]
    [InlineData("ollama", "ollama serve")]
    [InlineData("grok", "grok")]
    [InlineData("opencode", "opencode auth login")]
    [InlineData("claude", "claude /login")]
    public void LoginCommand_IsTheOneTableTheRunnerAlsoQuotes(string cli, string expected)
    {
        CliAuthFailure.LoginCommand(cli).Should().Be(expected);
        Orkka.LocalRunner.ProviderDetection.LoginCommand(cli).Should().Be(expected,
            "the status panel's Connect button and the failed run's remedy must never drift");
    }
}
