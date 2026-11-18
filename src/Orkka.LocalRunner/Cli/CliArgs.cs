namespace Orkka.LocalRunner.Cli;

/// <summary>
/// Minimal argument parsing for the <c>orkka</c> CLI: global flags are stripped before the
/// subcommand switch so they work in any position with any command
/// (<c>./orkka start --debug</c>, <c>./orkka --debug status</c>).
/// </summary>
public static class CliArgs
{
    public const string DebugFlag = "--debug";

    public sealed record Parsed(string? Command, string[] RemainingArgs, bool Debug);

    public static Parsed Parse(string[] args, Func<string, string?> getEnv)
    {
        var remaining = args
            .Where(a => !IsGlobalFlag(a))
            .ToArray();

        var debug = args.Any(a => Matches(a, DebugFlag)) || IsTruthy(getEnv("ORKKA_DEBUG"));

        return new Parsed(remaining.FirstOrDefault()?.ToLowerInvariant(), remaining, debug);
    }

    private static bool IsGlobalFlag(string arg) => Matches(arg, DebugFlag);

    private static bool Matches(string arg, string flag) =>
        string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase);

    private static bool IsTruthy(string? value) =>
        value is "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}
