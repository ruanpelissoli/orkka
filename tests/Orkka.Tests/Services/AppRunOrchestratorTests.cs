namespace Orkka.Tests.Services;

using System.Text;
using FluentAssertions;
using Orkka.Runners.Server.AppRun;

/// <summary>
/// The pure edges of the host-side app-run state: what counts as a failed start versus an
/// app that ran and stopped, what the "Live app" link points at, and how a scrollback turns
/// into a one-glance error. Each is a decision the UI renders differently, so each is pinned.
/// </summary>
public sealed class AppRunOrchestratorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IsEarlyFailure_NonZeroExitSoonAfterStart_IsAFailedStart()
    {
        AppRunOrchestrator.IsEarlyFailure(1, T0, T0.AddSeconds(3)).Should().BeTrue();
    }

    [Fact]
    public void IsEarlyFailure_ZeroExit_IsNeverAFailure()
    {
        AppRunOrchestrator.IsEarlyFailure(0, T0, T0.AddSeconds(3)).Should().BeFalse();
    }

    [Fact]
    public void IsEarlyFailure_NonZeroExitAfterRunningAWhile_IsJustAnExit()
    {
        // A dev server the user Ctrl-C'd an hour in is not a start that failed.
        AppRunOrchestrator.IsEarlyFailure(130, T0, T0.AddMinutes(30)).Should().BeFalse();
    }

    [Fact]
    public void IsEarlyFailure_UnknownExitCode_IsNotAFailure()
    {
        AppRunOrchestrator.IsEarlyFailure(null, T0, T0.AddSeconds(1)).Should().BeFalse();
    }

    [Theory]
    [InlineData("http://localhost:3000", null, "http://localhost:3000")]
    [InlineData("http://localhost:3000", "/", "http://localhost:3000")]
    [InlineData("http://localhost:3000/", "/admin", "http://localhost:3000/admin")]
    [InlineData("http://localhost:3000", "admin", "http://localhost:3000/admin")]
    [InlineData(null, "/admin", null)]
    [InlineData("  ", "/admin", null)]
    public void PreviewUrlOf_JoinsUrlAndPath(string? url, string? path, string? expected)
    {
        AppRunOrchestrator.PreviewUrlOf(url, path).Should().Be(expected);
    }

    [Fact]
    public void LastLines_ReturnsTheTailWithoutAnsiOrRunnerNarration()
    {
        var text = "\x1b[2m[orkka] Created .env from .env.example\x1b[0m\r\n"
            + "\x1b[32m> app@1.0.0 dev\x1b[0m\r\n"
            + "\r\n"
            + "node:internal/modules/cjs/loader:1228\r\n"
            + "  throw err;\r\n"
            + "  ^\r\n"
            + "Error: Cannot find module 'C:\\app\\nope.js'\r\n"
            + "    at Module._resolveFilename (node:internal/modules/cjs/loader:1225:15)\r\n"
            + "    at Module._load (node:internal/modules/cjs/loader:1055:27)\r\n"
            + "{\r\n"
            + "  code: 'MODULE_NOT_FOUND',\r\n"
            + "  requireStack: []\r\n"
            + "}\r\n"
            + "Node.js v24.18.0\r\n";
        var buffer = new ScrollbackBuffer(64 * 1024);
        buffer.Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(text)));

        var tail = buffer.LastLines(4);

        // Frames and punctuation dropped: the line that says what went wrong stays in view.
        tail.Should().Be(
            "Error: Cannot find module 'C:\\app\\nope.js'\ncode: 'MODULE_NOT_FOUND',\nrequireStack: []\nNode.js v24.18.0");
        tail.Should().NotContain("\x1b");
        tail.Should().NotContain("[orkka]");
    }

    [Fact]
    public void ScrollbackBuffer_SurvivesIndependentlyPaddedChunks()
    {
        // The runner base64-encodes each PTY read on its own, so most chunks carry padding.
        // Concatenating those strings is not valid base64; the buffer must decode per chunk
        // and re-encode as one, or neither the browser replay nor the failure tail decodes.
        var buffer = new ScrollbackBuffer(64 * 1024);
        buffer.Append(Convert.ToBase64String(Encoding.UTF8.GetBytes("a")));      // "YQ=="
        buffer.Append(Convert.ToBase64String(Encoding.UTF8.GetBytes("bc")));     // "YmM="
        buffer.Append(Convert.ToBase64String(Encoding.UTF8.GetBytes("\nboom\n")));

        var all = buffer.ToBase64();

        all.Should().NotBeNull();
        Encoding.UTF8.GetString(Convert.FromBase64String(all!)).Should().Be("abc\nboom\n");
        buffer.LastLines(1).Should().Be("boom");
    }

    [Fact]
    public void ScrollbackBuffer_KeepsOnlyTheTailAndReportsNullWhenEmpty()
    {
        var buffer = new ScrollbackBuffer(8);
        buffer.ToBase64().Should().BeNull();

        buffer.Append(Convert.ToBase64String(Encoding.UTF8.GetBytes("12345")));
        buffer.Append(Convert.ToBase64String(Encoding.UTF8.GetBytes("67890")));

        Encoding.UTF8.GetString(Convert.FromBase64String(buffer.ToBase64()!)).Should().Be("67890");
    }

    [Fact]
    public void FailureReason_IncludesTheTailWhenThereIsOne()
    {
        AppRunOrchestrator.FailureReason(1, "Cannot find module 'next'")
            .Should().Be("Exited with code 1 right after starting.\nCannot find module 'next'");
        AppRunOrchestrator.FailureReason(1, "")
            .Should().Be("Exited with code 1 right after starting.");
    }
}
