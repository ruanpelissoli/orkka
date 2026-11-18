namespace Orkka.Tests.Local;

using FluentAssertions;
using Orkka.LocalRunner;

/// <summary>
/// The runner's own git settings (core.longpaths, …) go through GIT_CONFIG_* — and must be added
/// after any pairs the developer's environment already carries, never over them.
/// </summary>
public sealed class BundledGitConfigTests
{
    [Fact]
    public void Settings_are_appended_after_the_inherited_pairs()
    {
        var inherited = new Dictionary<string, string?>
        {
            ["GIT_CONFIG_COUNT"] = "2",
            ["GIT_CONFIG_KEY_0"] = "url.https://mirror.corp/.insteadOf",
            ["GIT_CONFIG_VALUE_0"] = "https://github.com/",
            ["GIT_CONFIG_KEY_1"] = "http.proxy",
            ["GIT_CONFIG_VALUE_1"] = "http://proxy.corp:3128",
        };

        var vars = BundledGit.ConfigOverrideVariables(k => inherited.GetValueOrDefault(k), [("core.longpaths", "true")]);

        vars.Should().Equal(
            ("GIT_CONFIG_KEY_2", "core.longpaths"),
            ("GIT_CONFIG_VALUE_2", "true"),
            ("GIT_CONFIG_COUNT", "3"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("0")]
    public void Without_inherited_pairs_they_start_at_zero(string? count)
    {
        var vars = BundledGit.ConfigOverrideVariables(k => k == "GIT_CONFIG_COUNT" ? count : null,
            [("core.longpaths", "true"), ("core.autocrlf", "false")]);

        vars.Should().Equal(
            ("GIT_CONFIG_KEY_0", "core.longpaths"),
            ("GIT_CONFIG_VALUE_0", "true"),
            ("GIT_CONFIG_KEY_1", "core.autocrlf"),
            ("GIT_CONFIG_VALUE_1", "false"),
            ("GIT_CONFIG_COUNT", "2"));
    }
}
