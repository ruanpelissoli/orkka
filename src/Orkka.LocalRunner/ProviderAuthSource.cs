namespace Orkka.LocalRunner;

using Orkka.LocalRunner.Cli;
using Orkka.LocalRunner.Trackers;
using Orkka.Modules.Execution.Agents;

/// <summary>
/// A workspace's own AI sign-in variables (<c>./orkka workspace set-env</c>): the plain ones
/// (a region, a profile name, a base URL, a host, a mode pin) as lines in the workspace's
/// <c>profile.env</c>, the secret ones in the OS secret store under
/// <c>orkka/{workspace}/ai/{NAME}</c> — profile.env only lists their NAMES
/// (<see cref="SecretNamesKey"/>) so a lookup touches the store for what is really there. Only
/// names on a provider allowlist (<see cref="ProviderAuthEnvironment"/>) are accepted.
/// </summary>
public static class WorkspaceProviderEnv
{
    /// <summary>profile.env key listing the secret names this workspace keeps in the store.</summary>
    public const string SecretNamesKey = "ORKKA_AI_SECRET_ENV";

    private const string SecretIdPrefix = "ai-env-";

    public static string SecretId(string name) => SecretIdPrefix + name;

    public static bool IsSecretId(string id) => id.StartsWith(SecretIdPrefix, StringComparison.Ordinal);

    public static string NameOfSecretId(string id) => id[SecretIdPrefix.Length..];

    /// <summary>The plain values and the secret NAMES a workspace holds (no secret value).</summary>
    public static (IReadOnlyDictionary<string, string> Plain, IReadOnlyList<string> SecretNames) Describe(string orkkaRoot, string workspace)
    {
        var path = WorkspaceProfiles.ProfilePath(orkkaRoot, workspace);
        var values = File.Exists(path) ? WorkspaceProfiles.ReadEnv(path) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var plain = values
            .Where(kv => ProviderAuthEnvironment.IsAllowlisted(kv.Key) && !ProviderAuthEnvironment.IsSecret(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        return (plain, ParseNames(values.GetValueOrDefault(SecretNamesKey)));
    }

    /// <summary>Values for <paramref name="names"/> in this workspace: plain ones from
    /// profile.env, secret ones from the store. Never throws.</summary>
    public static IReadOnlyDictionary<string, string> Read(
        string orkkaRoot, string workspace, IReadOnlyCollection<string> names, IWorkspaceSecretStore? secrets = null)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var path = WorkspaceProfiles.ProfilePath(orkkaRoot, workspace);
            if (!File.Exists(path))
                return result;
            var values = WorkspaceProfiles.ReadEnv(path);
            var stored = ParseNames(values.GetValueOrDefault(SecretNamesKey)).ToHashSet(StringComparer.Ordinal);
            IWorkspaceSecretStore? store = null;
            foreach (var name in names)
            {
                if (stored.Contains(name))
                {
                    store ??= secrets ?? WorkspaceSecretStores.CreateDefault(orkkaRoot, workspace);
                    if (store.Get(SecretId(name)) is { Length: > 0 } secret)
                        result[name] = secret;
                }
                else if (values.TryGetValue(name, out var plain) && !string.IsNullOrWhiteSpace(plain))
                {
                    result[name] = plain;
                }
            }
        }
        catch
        {
            // A failed read means the CLI's own login — never a crashed run.
        }
        return result;
    }

    /// <summary>Sets one variable: a secret into the store (its name into profile.env), a plain
    /// one into profile.env. Rejects names on no allowlist.</summary>
    public static void Set(string orkkaRoot, string workspace, string name, string value, IWorkspaceSecretStore? secrets = null,
        IEnumerable<string>? codexConfigKeys = null)
    {
        var keys = codexConfigKeys?.ToList();
        if (!ProviderAuthEnvironment.IsAllowlisted(name, keys))
            throw new ArgumentException($"'{name}' is not an AI sign-in variable Orkka passes to a CLI.", nameof(name));
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("The value is empty.", nameof(value));

        var path = WorkspaceProfiles.ProfilePath(orkkaRoot, workspace);
        if (ProviderAuthEnvironment.IsSecret(name))
        {
            (secrets ?? WorkspaceSecretStores.CreateDefault(orkkaRoot, workspace)).Set(SecretId(name), value.Trim());
            UpdateProfile(path, values =>
            {
                values.Remove(name);
                var names = ParseNames(values.GetValueOrDefault(SecretNamesKey)).Append(name).Distinct(StringComparer.Ordinal);
                values[SecretNamesKey] = string.Join(',', names);
            });
        }
        else
        {
            UpdateProfile(path, values => values[name] = value.Trim());
        }
    }

    /// <summary>Removes one variable (store entry and profile line). True when it was there.</summary>
    public static bool Unset(string orkkaRoot, string workspace, string name, IWorkspaceSecretStore? secrets = null)
    {
        var path = WorkspaceProfiles.ProfilePath(orkkaRoot, workspace);
        if (!File.Exists(path))
            return false;
        var values = WorkspaceProfiles.ReadEnv(path);
        var stored = ParseNames(values.GetValueOrDefault(SecretNamesKey));
        var had = values.ContainsKey(name) || stored.Contains(name, StringComparer.Ordinal);
        if (stored.Contains(name, StringComparer.Ordinal))
            (secrets ?? WorkspaceSecretStores.CreateDefault(orkkaRoot, workspace)).Delete(SecretId(name));
        UpdateProfile(path, v =>
        {
            v.Remove(name);
            var rest = ParseNames(v.GetValueOrDefault(SecretNamesKey)).Where(n => n != name).ToList();
            if (rest.Count == 0)
                v.Remove(SecretNamesKey);
            else
                v[SecretNamesKey] = string.Join(',', rest);
        });
        return had;
    }

    /// <summary>Deletes every AI secret a workspace keeps in the store (workspace remove,
    /// uninstall). Never throws.</summary>
    public static void DeleteAllSecrets(string orkkaRoot, string workspace, TextWriter output, IWorkspaceSecretStore? secrets = null,
        Action<string>? removed = null)
    {
        try
        {
            var path = WorkspaceProfiles.ProfilePath(orkkaRoot, workspace);
            if (!File.Exists(path))
                return;
            var names = ParseNames(WorkspaceProfiles.ReadEnv(path).GetValueOrDefault(SecretNamesKey));
            if (names.Count == 0)
                return;
            var store = secrets ?? WorkspaceSecretStores.CreateDefault(orkkaRoot, workspace);
            foreach (var name in names)
            {
                try
                {
                    if (store.Get(SecretId(name)) is null)
                        continue;
                    store.Delete(SecretId(name));
                    removed?.Invoke(WorkspaceSecretStores.KeyFor(workspace, SecretId(name)));
                }
                catch (Exception ex)
                {
                    output.WriteLine($"  could not delete the AI sign-in secret {name} of '{workspace}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            output.WriteLine($"  could not read the AI sign-in settings of '{workspace}': {ex.Message}");
        }
    }

    internal static IReadOnlyList<string> ParseNames(string? csv) =>
        [.. (csv ?? "").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal)];

    /// <summary>Read-modify-write of profile.env that keeps every other line (the host, the git
    /// identity, comments), temp + move, owner-only on Unix.</summary>
    private static void UpdateProfile(string path, Action<Dictionary<string, string>> change)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        var values = WorkspaceProfiles.ReadEnv(lines);
        var before = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        change(values);

        var output = new List<string>();
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var idx = line.IndexOf('=');
            if (idx <= 0 || line.TrimStart().StartsWith('#'))
            {
                output.Add(line);
                continue;
            }
            var key = line[..idx].Trim();
            if (!values.TryGetValue(key, out var value) || !written.Add(key))
                continue; // removed (or a duplicate line)
            output.Add(before.TryGetValue(key, out var old) && old == value ? line : $"{key}={value}");
        }
        foreach (var (key, value) in values)
            if (written.Add(key))
                output.Add($"{key}={value}");

        var temp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, string.Join('\n', output) + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>
/// The runner's <see cref="IProviderAuthSource"/>: a run's workspace values
/// (<see cref="WorkspaceProviderEnv"/>), else the machine's — <c>~/.orkka/runner.env</c>, then
/// the environment the runner was STARTED with, captured before the startup scrub deletes every
/// sensitive variable (and every <c>CLAUDE_CODE_*</c> name) from the runner's own process. Only
/// allowlisted names are ever captured, and nothing here is written to the process environment:
/// a value reaches exactly the one CLI spawn it is resolved for.
/// </summary>
public sealed class RunnerProviderAuthSource(
    string orkkaRoot,
    IReadOnlyDictionary<string, string> startupEnvironment,
    Func<string, string?> readRunnerEnv,
    Func<string, IWorkspaceSecretStore>? secretStores = null,
    Func<string>? home = null) : IProviderAuthSource
{
    /// <summary>The allowlisted names (every provider, plus Codex config env_keys) present in
    /// <paramref name="environment"/> — called once, before the scrub.</summary>
    public static IReadOnlyDictionary<string, string> CaptureStartupEnvironment(System.Collections.IDictionary environment)
    {
        var codexKeys = ReadCodexConfigEnvKeys(environment["CODEX_HOME"] as string,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var captured = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in environment)
        {
            if (entry.Key is not string name || entry.Value is not string value || string.IsNullOrWhiteSpace(value))
                continue;
            if (ProviderAuthEnvironment.IsAllowlisted(name, codexKeys))
                captured[name] = value;
        }
        return captured;
    }

    public IReadOnlyDictionary<string, string> WorkspaceValues(string? workspace, IReadOnlyCollection<string> names)
        => string.IsNullOrWhiteSpace(workspace) || !WorkspaceProfiles.IsValidName(workspace)
            ? new Dictionary<string, string>()
            : WorkspaceProviderEnv.Read(orkkaRoot, workspace, names, secretStores?.Invoke(workspace));

    public IReadOnlyDictionary<string, string> MachineValues(IReadOnlyCollection<string> names)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var value = readRunnerEnv(name);
            if (string.IsNullOrWhiteSpace(value))
                startupEnvironment.TryGetValue(name, out value);
            if (!string.IsNullOrWhiteSpace(value))
                result[name] = value;
        }
        return result;
    }

    private readonly Lock _codexLock = new();
    private (string? Path, DateTime Written, IReadOnlyCollection<string> Keys) _codexCache = (null, default, []);

    public IReadOnlyCollection<string> CodexConfigEnvKeys()
    {
        var path = CodexConfigPath(
            readRunnerEnv("CODEX_HOME") ?? startupEnvironment.GetValueOrDefault("CODEX_HOME"),
            home?.Invoke() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        try
        {
            var written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
            lock (_codexLock)
            {
                if (_codexCache.Path == path && _codexCache.Written == written)
                    return _codexCache.Keys;
            }
            IReadOnlyCollection<string> keys = written == default ? [] : ProviderAuthEnvironment.ParseCodexEnvKeys(File.ReadAllText(path));
            lock (_codexLock)
                _codexCache = (path, written, keys);
            return keys;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>$CODEX_HOME/config.toml, else ~/.codex/config.toml.</summary>
    public static string CodexConfigPath(string? codexHome, string home)
        => Path.Combine(string.IsNullOrWhiteSpace(codexHome) ? Path.Combine(home, ".codex") : codexHome, "config.toml");

    private static IReadOnlyList<string> ReadCodexConfigEnvKeys(string? codexHome, string home)
    {
        try
        {
            var path = CodexConfigPath(codexHome, home);
            return File.Exists(path) ? ProviderAuthEnvironment.ParseCodexEnvKeys(File.ReadAllText(path)) : [];
        }
        catch
        {
            return [];
        }
    }
}
