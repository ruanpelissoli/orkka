namespace Orkka.Tests.Worker;

using Orkka.Modules.Execution.Agents;
using FluentAssertions;

public sealed class AgentEnvironmentTests
{
    [Fact]
    public void BuildSanitizedOverrides_BlanksSensitiveVariables()
    {
        var source = new Dictionary<string, string?>
        {
            ["DATABASE_URL"] = "postgres://user:pass@host/db",
            ["GITHUB_TOKEN"] = "ghs_abc",
            ["STRIPE_SECRET_KEY"] = "sk_live_x",
            ["CLERK_SECRET_KEY"] = "sk_clerk",
            ["ConnectionStrings__Default"] = "Host=...;Password=...",
            ["PATH"] = "/usr/bin",
            ["HOME"] = "/home/agent",
        };

        var overrides = AgentEnvironment.BuildSanitizedOverrides(source);

        overrides.Should().ContainKey("DATABASE_URL").WhoseValue.Should().BeEmpty();
        overrides.Should().ContainKey("GITHUB_TOKEN").WhoseValue.Should().BeEmpty();
        overrides.Should().ContainKey("STRIPE_SECRET_KEY").WhoseValue.Should().BeEmpty();
        overrides.Should().ContainKey("CLERK_SECRET_KEY").WhoseValue.Should().BeEmpty();
        overrides.Should().ContainKey("ConnectionStrings__Default").WhoseValue.Should().BeEmpty();

        // Non-sensitive variables required by the CLI are left untouched (not in the override set).
        overrides.Should().NotContainKey("PATH");
        overrides.Should().NotContainKey("HOME");
    }

    [Theory]
    [InlineData("DATABASE_URL", true)]
    [InlineData("PGPASSWORD", true)]
    [InlineData("ANTHROPIC_API_KEY", true)]
    [InlineData("GH_TOKEN", true)]
    [InlineData("AWS_SECRET_ACCESS_KEY", true)]
    [InlineData("PATH", false)]
    [InlineData("HOME", false)]
    [InlineData("LANG", false)]
    public void IsSensitive_ClassifiesVariables(string name, bool expected)
    {
        AgentEnvironment.IsSensitive(name).Should().Be(expected);
    }
}
