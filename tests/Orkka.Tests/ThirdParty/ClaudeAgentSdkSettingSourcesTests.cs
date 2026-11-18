namespace Orkka.Tests.ThirdParty;

using FluentAssertions;
using ih0.Claude.Agent.SDK;
using ih0.Claude.Agent.SDK.Internal.Transport;
using ih0.Claude.Agent.SDK.Types;

/// <summary>
/// A repository's committed .claude/settings.json must never configure an Orkka-run Claude:
/// its hooks run commands outside every guard and its env can redirect the developer's key.
/// The transport always names the setting sources; by default only the developer's own.
/// </summary>
public sealed class ClaudeAgentSdkSettingSourcesTests
{
    private static List<string> Command(IReadOnlyList<SettingSource>? sources) =>
        new SubprocessCliTransport("hi", new ClaudeAgentOptions { CliPath = "claude", SettingSources = sources })
            .BuildCommand();

    private static string? SourcesArg(List<string> cmd)
    {
        var i = cmd.IndexOf("--setting-sources");
        return i < 0 ? null : cmd[i + 1];
    }

    [Fact]
    public void Default_LoadsUserSettingsOnly_NeverTheRepository()
    {
        SourcesArg(Command(null)).Should().Be("user");
    }

    [Fact]
    public void EmptyList_LoadsNoSettings()
    {
        SourcesArg(Command([])).Should().Be("");
    }

    [Fact]
    public void ExplicitSources_ArePassedThrough()
    {
        SourcesArg(Command([SettingSource.User, SettingSource.Project])).Should().Be("user,project");
    }
}
