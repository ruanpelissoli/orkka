namespace Orkka.LocalRunner.Cli;

/// <summary>
/// ~/.orkka/runner.env — optional secrets the user keeps for the runner itself (a Gemini API
/// key, a long-lived Claude setup token, a Copilot GitHub token). Read one value at a time,
/// never loaded into the process environment: the runner's env scrub would delete it anyway,
/// and a secret in the runner's env is one an agent child could inherit.
///
/// The file is deliberately NOT named ".env": the gemini CLI dotenv-loads the first `.env`
/// found walking up from its working directory, and task worktrees live under ~/.orkka/repos
/// — so a ~/.orkka/.env put secrets INTO gemini agent processes, past the env sanitizer
/// (verified live 2026-07-12). Older installs kept these keys in ~/.orkka/stack.env (the
/// retired Docker stack's file) or ~/.orkka/.env; both are migrated on first read.
/// </summary>
public static class RunnerEnvFile
{
    /// <summary>~/.orkka — the root every runner-owned file lives under. <c>ORKKA_HOME</c>
    /// moves it (a sandbox for trying the runner, or tests): on Windows the user profile folder
    /// comes from the shell, not from USERPROFILE, so overriding that is not enough.</summary>
    internal static readonly string OrkkaDir =
        Environment.GetEnvironmentVariable("ORKKA_HOME") is { Length: > 0 } home
            ? Path.GetFullPath(home)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".orkka");
    private static readonly string EnvPath = Path.Combine(OrkkaDir, "runner.env");
    private static readonly string StackEnvPath = Path.Combine(OrkkaDir, "stack.env");
    private static readonly string LegacyEnvPath = Path.Combine(OrkkaDir, ".env");

    /// <summary>Reads one value, or null when the file or key is absent. Never throws.</summary>
    public static string? TryReadValue(string key)
    {
        try
        {
            MigrateLegacyEnvFile(LegacyEnvPath, EnvPath);
            MigrateLegacyEnvFile(StackEnvPath, EnvPath);
            if (!File.Exists(EnvPath))
                return null;
            return ParseEnvLines(File.ReadAllLines(EnvPath)).GetValueOrDefault(key);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Moves an older secrets file to <paramref name="envPath"/>. When both exist,
    /// keys only the old file has are merged in before it is deleted — a ~/.orkka/.env must
    /// never linger inside the gemini CLI's dotenv walk path. Never throws.</summary>
    internal static void MigrateLegacyEnvFile(string legacyPath, string envPath)
    {
        try
        {
            if (!File.Exists(legacyPath))
                return;

            if (!File.Exists(envPath))
            {
                File.Move(legacyPath, envPath);
                Console.WriteLine($"Moved runner secrets from {Path.GetFileName(legacyPath)} to {Path.GetFileName(envPath)}.");
                return;
            }

            var existingKeys = ParseEnvLines(File.ReadAllLines(envPath)).Keys
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = File.ReadAllLines(legacyPath)
                .Where(line =>
                {
                    var idx = line.IndexOf('=');
                    return idx > 0 && !line.TrimStart().StartsWith('#')
                        && !existingKeys.Contains(line[..idx].Trim());
                })
                .ToList();

            if (missing.Count > 0)
                File.AppendAllLines(envPath, missing);
            File.Delete(legacyPath);
            Console.WriteLine($"Merged {Path.GetFileName(legacyPath)} into {Path.GetFileName(envPath)} and removed the old file.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: could not migrate {legacyPath}: {ex.Message}");
        }
    }

    private static Dictionary<string, string> ParseEnvLines(IEnumerable<string> lines)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var idx = line.IndexOf('=');
            if (idx > 0 && !line.TrimStart().StartsWith('#'))
                env[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }
        return env;
    }
}
