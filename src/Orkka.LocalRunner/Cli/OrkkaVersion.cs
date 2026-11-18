namespace Orkka.LocalRunner.Cli;

using System.Reflection;

/// <summary>The version of this build (what the runner reports in its Hello and the banner prints).
/// Orkka runs from source, so this is the assembly's informational version without build metadata.</summary>
public static class OrkkaVersion
{
    public static string Current()
    {
        var info = typeof(OrkkaVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = info.IndexOf('+'); // strip source-link build metadata
        return plus >= 0 ? info[..plus] : info;
    }
}
