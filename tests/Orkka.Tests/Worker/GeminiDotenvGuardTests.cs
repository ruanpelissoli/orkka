namespace Orkka.Tests.Worker;

using FluentAssertions;
using Orkka.Modules.Execution.Agents;

/// <summary>
/// The dotenv guard is the security stop for the gemini CLI's upward .env walk: a
/// controlled file at every Orkka workspace root so an agent process can never
/// dotenv-load unrelated secrets (the ~/.orkka/.env leak, verified live 2026-07-12).
/// These tests pin the three invariants: content mirrors the user's gemini env (with
/// the ORKKA fallback key only when no key is mirrored), Refresh never clobbers a
/// non-guard .env, and TryDeleteGuard only ever deletes marker-headed files.
/// </summary>
public sealed class GeminiDotenvGuardTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "orkka-guard-test-" + Ulid.NewUlid());

    public GeminiDotenvGuardTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        GeminiDotenvGuard.UserGeminiEnvPathOverride = null;
        GeminiDotenvGuard.FallbackApiKey = null;
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void BuildContent_StartsWithMarker_AndMirrorsUserEnv()
    {
        var content = GeminiDotenvGuard.BuildContent("GEMINI_API_KEY=AQ.abc123\nGOOGLE_CLOUD_PROJECT=p1", null);

        content.Should().StartWith(GeminiDotenvGuard.Marker);
        content.Should().Contain("GEMINI_API_KEY=AQ.abc123");
        content.Should().Contain("GOOGLE_CLOUD_PROJECT=p1");
    }

    [Fact]
    public void BuildContent_NoUserEnv_UsesFallbackKey()
    {
        var content = GeminiDotenvGuard.BuildContent(null, "AQ.fallback-key");

        content.Should().Contain("GEMINI_API_KEY=AQ.fallback-key");
    }

    [Fact]
    public void BuildContent_UserEnvAlreadyHasKey_IgnoresFallback()
    {
        var content = GeminiDotenvGuard.BuildContent("GOOGLE_API_KEY='user-key'", "fallback-key");

        content.Should().Contain("GOOGLE_API_KEY='user-key'");
        content.Should().NotContain("fallback-key");
    }

    [Fact]
    public void BuildContent_UserEnvWithoutKey_AppendsFallback()
    {
        // A commented-out key is not a key.
        var content = GeminiDotenvGuard.BuildContent("# GEMINI_API_KEY=old\nDEBUG=1", "AQ.fb");

        content.Should().Contain("DEBUG=1");
        content.Should().Contain("GEMINI_API_KEY=AQ.fb");
    }

    [Fact]
    public void BuildContent_NoKeyAnywhere_IsStillAValidStopFile()
    {
        var content = GeminiDotenvGuard.BuildContent(null, null);

        content.Should().StartWith(GeminiDotenvGuard.Marker);
        content.Should().NotContain("API_KEY=");
    }

    [Fact]
    public void Refresh_WritesGuard_AndIsIdempotent()
    {
        var userEnv = Path.Combine(_dir, "user-gemini.env");
        File.WriteAllText(userEnv, "GEMINI_API_KEY=k1");
        GeminiDotenvGuard.UserGeminiEnvPathOverride = userEnv;

        var root = Path.Combine(_dir, "worktrees");
        GeminiDotenvGuard.Refresh(root);
        GeminiDotenvGuard.Refresh(root);

        var guardPath = Path.Combine(root, ".env");
        File.Exists(guardPath).Should().BeTrue();
        var content = File.ReadAllText(guardPath);
        content.Should().StartWith(GeminiDotenvGuard.Marker);
        content.Should().Contain("GEMINI_API_KEY=k1");
    }

    [Fact]
    public void Refresh_NeverOverwritesAForeignEnvFile()
    {
        var foreign = Path.Combine(_dir, ".env");
        File.WriteAllText(foreign, "USER_OWNED=yes");

        GeminiDotenvGuard.Refresh(_dir);

        File.ReadAllText(foreign).Should().Be("USER_OWNED=yes");
    }

    [Fact]
    public void Refresh_PicksUpAChangedUserKey()
    {
        var userEnv = Path.Combine(_dir, "user-gemini.env");
        GeminiDotenvGuard.UserGeminiEnvPathOverride = userEnv;

        File.WriteAllText(userEnv, "GEMINI_API_KEY=old");
        GeminiDotenvGuard.Refresh(_dir);
        File.WriteAllText(userEnv, "GEMINI_API_KEY=rotated");
        GeminiDotenvGuard.Refresh(_dir);

        File.ReadAllText(Path.Combine(_dir, ".env")).Should().Contain("GEMINI_API_KEY=rotated");
    }

    [Fact]
    public void TryDeleteGuard_DeletesOwnFile_LeavesForeignFile()
    {
        GeminiDotenvGuard.Refresh(_dir);
        GeminiDotenvGuard.TryDeleteGuard(_dir).Should().BeTrue();
        File.Exists(Path.Combine(_dir, ".env")).Should().BeFalse();

        // Absent file: nothing to do, root is clear.
        GeminiDotenvGuard.TryDeleteGuard(_dir).Should().BeTrue();

        File.WriteAllText(Path.Combine(_dir, ".env"), "USER_OWNED=yes");
        GeminiDotenvGuard.TryDeleteGuard(_dir).Should().BeFalse();
        File.Exists(Path.Combine(_dir, ".env")).Should().BeTrue();
    }

    [Theory]
    [InlineData("GEMINI_API_KEY=AQ.WjKlMnOpQrStUvWxYzAbCdEfGhIjKlMnOpQrStUvWxYzAbCdEf1", true)]
    [InlineData("export GOOGLE_API_KEY=\"AIzaSyExample\"", true)]
    [InlineData("GEMINI_API_KEY=", false)]
    [InlineData("# GEMINI_API_KEY=x", false)]
    [InlineData("GEMINI_API_KEY_BACKUP=x", false)]
    [InlineData(null, false)]
    public void HasApiKey_MatchesDotenvRules(string? envText, bool expected)
    {
        GeminiDotenvGuard.HasApiKey(envText).Should().Be(expected);
    }
}
