namespace Orkka.Tests.Services;

using FluentAssertions;
using Orkka.LocalRunner;

/// <summary>
/// Drives the pure parsing legs of OpenCode detection (internal statics via
/// InternalsVisibleTo); the plumbing around them is trivial exists+read and a bounded
/// subprocess spawn.
///
/// These two answer the question that makes OpenCode usable at all. It is a HARNESS with no
/// models of its own, so "is it ready" cannot be "is the binary on PATH" — a fresh install
/// starts happily and then fails every run. Readiness is "which providers is this machine
/// logged in to", and the selectable model set is `opencode models` narrowed to exactly
/// those.
/// </summary>
public sealed class ProviderDetectionOpenCodeTests
{
    private static Func<string, string?> NoEnv => _ => null;

    // ── auth.json → logged-in providers ──────────────────────────────────────

    [Fact]
    public void AuthFile_TopLevelKeys_AreTheLoggedInProviders()
    {
        var json = """
            {
              "anthropic": { "type": "oauth", "refresh": "r", "access": "a", "expires": 0 },
              "openai": { "type": "api", "key": "sk-example" }
            }
            """;

        ProviderDetection.ReadOpenCodeAuthProviders(json, NoEnv)
            .Should().BeEquivalentTo("anthropic", "openai");
    }

    [Fact]
    public void AuthFile_Missing_MeansNoProviders()
        // Distinct from "installed": no logins is precisely the state whose remedy is
        // `opencode auth login`, so it must not read as ready.
        => ProviderDetection.ReadOpenCodeAuthProviders(null, NoEnv).Should().BeEmpty();

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    public void AuthFile_Malformed_ReadsAsNoLogins_AndNeverThrows(string json)
        => ProviderDetection.ReadOpenCodeAuthProviders(json, NoEnv).Should().BeEmpty();

    [Fact]
    public void EnvKeys_CountAsLogins_BecauseOpenCodeLoadsThemToo()
    {
        // OpenCode reads env vars and project .env files at startup, so an env-only setup is
        // genuinely authenticated even with no auth.json — treating it as logged out would
        // block a working machine.
        var env = (string name) => name == "XAI_API_KEY" ? "xai-example" : null;

        ProviderDetection.ReadOpenCodeAuthProviders(null, env).Should().BeEquivalentTo("xai");
    }

    [Fact]
    public void EnvKeys_AndAuthFile_Union()
    {
        var env = (string name) => name == "OPENROUTER_API_KEY" ? "sk-or-example" : null;

        ProviderDetection.ReadOpenCodeAuthProviders("""{"anthropic":{"type":"oauth"}}""", env)
            .Should().BeEquivalentTo("anthropic", "openrouter");
    }

    [Fact]
    public void EnvKeys_Blank_DoNotCount()
    {
        var env = (string name) => name == "ANTHROPIC_API_KEY" ? "   " : null;

        ProviderDetection.ReadOpenCodeAuthProviders(null, env).Should().BeEmpty();
    }

    [Fact]
    public void Providers_AreMatchedCaseInsensitively()
        // The reported model ids are filtered against this set, and neither the CLI's model
        // list nor its auth file promises a casing.
        => ProviderDetection.ReadOpenCodeAuthProviders("""{"Anthropic":{"type":"oauth"}}""", NoEnv)
            .Should().Contain("anthropic");

    // ── `opencode models` output → model ids ─────────────────────────────────

    [Fact]
    public void ModelList_KeepsProviderSlashModelLines()
    {
        var stdout = """
            anthropic/claude-sonnet-4-6
            anthropic/claude-opus-5
            openai/gpt-5.6-sol
            xai/grok-4.6
            """;

        ProviderDetection.ParseOpenCodeModels(stdout).Should().Equal(
            "anthropic/claude-sonnet-4-6",
            "anthropic/claude-opus-5",
            "openai/gpt-5.6-sol",
            "xai/grok-4.6");
    }

    [Fact]
    public void ModelList_DropsDecorationAndBlankLines()
    {
        // Strict by design: a false positive here becomes a SELECTABLE model id that cannot
        // run, which is worse than a missing row (the catalog anchors still show).
        var stdout = """
            Available models:

            anthropic/claude-sonnet-4-6
            openai/gpt-5.6-sol   (default)
            no-slash-here
            /leading-slash
            trailing-slash/

            """;

        ProviderDetection.ParseOpenCodeModels(stdout).Should().Equal(
            "anthropic/claude-sonnet-4-6");
    }

    [Fact]
    public void ModelList_Deduplicates()
        => ProviderDetection.ParseOpenCodeModels("anthropic/claude-opus-5\nanthropic/claude-opus-5\n")
            .Should().ContainSingle();

    [Fact]
    public void ModelList_HandlesCrlf()
        => ProviderDetection.ParseOpenCodeModels("anthropic/claude-opus-5\r\nxai/grok-4.6\r\n")
            .Should().Equal("anthropic/claude-opus-5", "xai/grok-4.6");

    [Theory]
    [InlineData("")]
    [InlineData("\n\n\n")]
    [InlineData("No models configured.")]
    public void ModelList_EmptyOrUnparseable_YieldsNothing(string stdout)
        => ProviderDetection.ParseOpenCodeModels(stdout).Should().BeEmpty();
}
