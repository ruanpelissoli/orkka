namespace Orkka.Tests.Services;

using FluentAssertions;
using Orkka.LocalRunner.Cli;

/// <summary>
/// The runner CLI's global-flag parsing: `--debug` must work in any position with any
/// command without disturbing command resolution, and ORKKA_DEBUG must be an equivalent
/// switch for environments where editing the launch command is awkward.
/// </summary>
public sealed class CliArgsTests
{
    private static string? NoEnv(string _) => null;

    [Theory]
    [InlineData("start", "--debug")]
    [InlineData("--debug", "start")]
    [InlineData("START", "--DEBUG")]
    public void Debug_flag_is_stripped_and_command_resolved(string first, string second)
    {
        var parsed = CliArgs.Parse([first, second], NoEnv);

        parsed.Debug.Should().BeTrue();
        parsed.Command.Should().Be("start");
        parsed.RemainingArgs.Should().NotContain(a => a.Contains("debug", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Bare_debug_flag_means_no_command()
    {
        var parsed = CliArgs.Parse(["--debug"], NoEnv);

        parsed.Debug.Should().BeTrue();
        parsed.Command.Should().BeNull();
        parsed.RemainingArgs.Should().BeEmpty();
    }

    [Fact]
    public void No_flag_and_no_env_means_debug_off()
    {
        var parsed = CliArgs.Parse(["start"], NoEnv);

        parsed.Debug.Should().BeFalse();
        parsed.Command.Should().Be("start");
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Env_var_enables_debug(string? value, bool expected)
    {
        var parsed = CliArgs.Parse(["start"], _ => value);

        parsed.Debug.Should().Be(expected);
    }

    [Fact]
    public void Doctor_provider_arg_survives_flag_stripping()
    {
        var parsed = CliArgs.Parse(["doctor", "codex", "--debug"], NoEnv);

        parsed.Command.Should().Be("doctor");
        parsed.RemainingArgs.Should().Equal("doctor", "codex");
        parsed.Debug.Should().BeTrue();
    }
}
