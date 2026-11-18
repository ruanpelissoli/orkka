namespace Orkka.LocalRunner.Cli;

/// <summary>
/// Startup banner for the runner terminal. Plain ASCII only — must render on legacy
/// Windows conhost and macOS Terminal alike; color is ANSI and skipped when output is
/// redirected or NO_COLOR is set.
/// </summary>
public static class ConsoleBanner
{
    private const string Logo =
        """
          ____   _____   _  __ _  __
         / __ \ |  __ \ | |/ /| |/ /    /\
        | |  | || |__) || ' / | ' /    /  \
        | |  | ||  _  / |  <  |  <    / /\ \
        | |__| || | \ \ | . \ | . \  / ____ \
         \____/ |_|  \_\|_|\_\|_|\_\/_/    \_\
        """;

    public static void Print()
    {
        var useColor = !Console.IsOutputRedirected
            && Environment.GetEnvironmentVariable("NO_COLOR") is null;

        if (useColor)
            Console.Write("\x1b[36m");
        Console.WriteLine(Logo);
        if (useColor)
            Console.Write("\x1b[0m");
        Console.WriteLine($"Orkka v{OrkkaVersion.Current()}");
        Console.WriteLine();
    }
}
