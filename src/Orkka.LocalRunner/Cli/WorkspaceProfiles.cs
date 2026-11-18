namespace Orkka.LocalRunner.Cli;

using System.Text.RegularExpressions;
using Orkka.LocalRunner.Trackers;

/// <summary>
/// One job's connection to Orkka: which host, which <c>orkr_</c> token (bound server-side to
/// one org and user), and optionally the git identity commits for that job are made as.
/// Not a positional record on purpose: a record's generated ToString would print the token.
/// </summary>
public sealed class WorkspaceProfile
{
    public required string Name { get; init; }
    public required string HostUrl { get; init; }
    public required string Token { get; init; }
    public string? GitAuthorName { get; init; }
    public string? GitAuthorEmail { get; init; }

    /// <summary>The Orkka workspace this profile serves, when `./orkka start` created it from
    /// the local host's bootstrap — how a later bootstrap finds the profile again. Null for a
    /// profile added by hand with `connect --token`.</summary>
    public string? WorkspaceId { get; init; }

    public bool HasIdentity => !string.IsNullOrWhiteSpace(GitAuthorName) && !string.IsNullOrWhiteSpace(GitAuthorEmail);

    public override string ToString() => $"{Name} ({HostUrl}, token hidden)";
}

/// <summary>
/// The workspace profiles on this machine: <c>~/.orkka/workspaces/{name}/profile.env</c> (host
/// and git identity — nothing secret), next to that workspace's trackers.json and ticket cache.
/// The runner token itself lives in the OS secret store
/// (<see cref="WorkspaceSecretStores.RunnerTokenId"/>), like the tracker tokens. One
/// The runner (<c>./orkka start</c> runs it in its own process; <c>./orkka connect</c> runs it alone) runs a session per profile. Machine-wide settings (the AI CLI
/// tokens in runner.env) are not per workspace — one subscription serves every job.
/// </summary>
public static partial class WorkspaceProfiles
{
    public const string DefaultName = TrackerPaths.DefaultProfile;
    public const string ProfileFileName = "profile.env";

    private const string HostKey = "ORKKA_HOSTED_URL";
    private const string TokenKey = "ORKKA_HOSTED_TOKEN";
    private const string AuthorNameKey = "ORKKA_GIT_AUTHOR_NAME";
    private const string AuthorEmailKey = "ORKKA_GIT_AUTHOR_EMAIL";
    private const string WorkspaceIdKey = "ORKKA_WORKSPACE_ID";

    /// <summary>Lowercase letters, digits and hyphens, starting with a letter or digit — a
    /// folder name on every OS and a word you can type.</summary>
    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    public static string ProfilePath(string orkkaRoot, string name)
        => Path.Combine(TrackerPaths.WorkspaceDir(orkkaRoot, name), ProfileFileName);

    /// <summary>Every profile, "default" first, then by name. A profile file without a token
    /// is skipped — it cannot connect.</summary>
    public static IReadOnlyList<WorkspaceProfile> List(string orkkaRoot)
    {
        var dir = Path.Combine(orkkaRoot, "workspaces");
        if (!Directory.Exists(dir))
            return [];
        return [.. Directory.EnumerateDirectories(dir)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(IsValidName)
            .Select(name => Get(orkkaRoot, name))
            .OfType<WorkspaceProfile>()
            .OrderBy(p => p.Name == DefaultName ? 0 : 1)
            .ThenBy(p => p.Name, StringComparer.Ordinal)];
    }

    /// <summary>Workspace folders on this machine, with or without a profile (a folder can
    /// hold trackers added before the profile was).</summary>
    public static IReadOnlyList<string> WorkspaceNames(string orkkaRoot)
    {
        var dir = Path.Combine(orkkaRoot, "workspaces");
        return Directory.Exists(dir)
            ? [.. Directory.EnumerateDirectories(dir).Select(Path.GetFileName).OfType<string>().Where(IsValidName).Order(StringComparer.Ordinal)]
            : [];
    }

    public static WorkspaceProfile? Get(string orkkaRoot, string name, IWorkspaceSecretStore? secrets = null)
    {
        var path = ProfilePath(orkkaRoot, name);
        if (!File.Exists(path))
            return null;
        var values = ReadEnv(path);
        secrets ??= WorkspaceSecretStores.CreateDefault(orkkaRoot, name);

        // A profile written before tokens moved to the secret store still holds it in the
        // file: move it across once, then rewrite the file without it.
        var token = secrets.Get(WorkspaceSecretStores.RunnerTokenId);
        if (values.GetValueOrDefault(TokenKey) is { Length: > 0 } legacy)
        {
            token ??= legacy;
            secrets.Set(WorkspaceSecretStores.RunnerTokenId, token);
            values.Remove(TokenKey);
            WriteFile(path, values.GetValueOrDefault(HostKey), values.GetValueOrDefault(AuthorNameKey),
                values.GetValueOrDefault(AuthorEmailKey), values.GetValueOrDefault(WorkspaceIdKey));
        }

        if (string.IsNullOrWhiteSpace(token))
            return null;
        return new WorkspaceProfile
        {
            Name = name,
            HostUrl = (values.GetValueOrDefault(HostKey) is { Length: > 0 } host ? host : ConnectCommand.DefaultHostUrl).TrimEnd('/'),
            Token = token,
            GitAuthorName = NullIfEmpty(values.GetValueOrDefault(AuthorNameKey)),
            GitAuthorEmail = NullIfEmpty(values.GetValueOrDefault(AuthorEmailKey)),
            WorkspaceId = NullIfEmpty(values.GetValueOrDefault(WorkspaceIdKey)),
        };
    }

    /// <summary>The token to the workspace's secret store; host and identity to profile.env.</summary>
    public static void Save(string orkkaRoot, WorkspaceProfile profile, IWorkspaceSecretStore? secrets = null)
    {
        if (!IsValidName(profile.Name))
            throw new ArgumentException($"'{profile.Name}' is not a valid workspace name.", nameof(profile));
        (secrets ?? WorkspaceSecretStores.CreateDefault(orkkaRoot, profile.Name))
            .Set(WorkspaceSecretStores.RunnerTokenId, profile.Token);
        WriteFile(ProfilePath(orkkaRoot, profile.Name), profile.HostUrl, profile.GitAuthorName, profile.GitAuthorEmail,
            profile.WorkspaceId);
    }

    /// <summary>Replaces only the stored runner token — what a rotation does.</summary>
    public static void SaveToken(string orkkaRoot, string name, string token, IWorkspaceSecretStore? secrets = null)
        => (secrets ?? WorkspaceSecretStores.CreateDefault(orkkaRoot, name)).Set(WorkspaceSecretStores.RunnerTokenId, token);

    /// <summary>Removes the workspace's runner token from the secret store. Never throws.</summary>
    public static void DeleteToken(string orkkaRoot, string name, TextWriter output, IWorkspaceSecretStore? secrets = null,
        Action<string>? removed = null)
    {
        try
        {
            var store = secrets ?? WorkspaceSecretStores.CreateDefault(orkkaRoot, name);
            if (store.Get(WorkspaceSecretStores.RunnerTokenId) is null)
                return;
            store.Delete(WorkspaceSecretStores.RunnerTokenId);
            removed?.Invoke(WorkspaceSecretStores.KeyFor(name, WorkspaceSecretStores.RunnerTokenId));
        }
        catch (Exception ex)
        {
            output.WriteLine($"  could not delete the runner token of '{name}': {ex.Message}");
        }
    }

    /// <summary>Whole-file replace (temp + move), owner-only on Unix.</summary>
    private static void WriteFile(string path, string? hostUrl, string? authorName, string? authorEmail, string? workspaceId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = new List<string>
        {
            "# Orkka workspace profile — written by `./orkka start` / `connect` / `workspace`.",
            "# The runner token is in the OS secret store, not here.",
            $"{HostKey}={(string.IsNullOrWhiteSpace(hostUrl) ? ConnectCommand.DefaultHostUrl : hostUrl.TrimEnd('/'))}",
        };
        if (!string.IsNullOrWhiteSpace(authorName))
            lines.Add($"{AuthorNameKey}={authorName.Trim()}");
        if (!string.IsNullOrWhiteSpace(authorEmail))
            lines.Add($"{AuthorEmailKey}={authorEmail.Trim()}");
        if (!string.IsNullOrWhiteSpace(workspaceId))
            lines.Add($"{WorkspaceIdKey}={workspaceId.Trim()}");

        // Everything else the file holds — the workspace's AI sign-in variables
        // (`workspace set-env`) — survives a rewrite of the connection lines.
        if (File.Exists(path))
        {
            string[] managed = [HostKey, TokenKey, AuthorNameKey, AuthorEmailKey, WorkspaceIdKey];
            lines.AddRange(File.ReadAllLines(path).Where(line =>
            {
                var idx = line.IndexOf('=');
                return idx > 0 && !line.TrimStart().StartsWith('#')
                    && !managed.Contains(line[..idx].Trim(), StringComparer.OrdinalIgnoreCase);
            }));
        }

        var temp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, string.Join('\n', lines) + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// One-time move from the single-connection layout: <c>~/.orkka/hosted.env</c> (host +
    /// token) and the git author in <c>~/.orkka/runner.env</c> become
    /// <c>workspaces/default/profile.env</c> plus the token in the secret store. hosted.env is
    /// deleted — a backup of it would be a plaintext token; runner.env is kept as a
    /// <c>.migrated</c> backup (it holds no Orkka token), and itself stays, minus the author keys, because its AI CLI tokens are
    /// machine-wide. Legacy stack.env / .env are first folded into runner.env as before.
    /// Never throws; returns what it did, for the console.
    /// </summary>
    public static string? MigrateLegacy(string orkkaRoot)
    {
        try
        {
            var runnerEnv = Path.Combine(orkkaRoot, "runner.env");
            RunnerEnvFile.MigrateLegacyEnvFile(Path.Combine(orkkaRoot, ".env"), runnerEnv);
            RunnerEnvFile.MigrateLegacyEnvFile(Path.Combine(orkkaRoot, "stack.env"), runnerEnv);

            var hostedEnv = Path.Combine(orkkaRoot, "hosted.env");
            if (!File.Exists(hostedEnv) || File.Exists(ProfilePath(orkkaRoot, DefaultName)))
                return null;

            var hosted = ReadEnv(hostedEnv);
            var runner = File.Exists(runnerEnv) ? ReadEnv(runnerEnv) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var token = hosted.GetValueOrDefault(TokenKey);
            if (string.IsNullOrWhiteSpace(token))
                return null;

            Save(orkkaRoot, new WorkspaceProfile
            {
                Name = DefaultName,
                HostUrl = (hosted.GetValueOrDefault(HostKey) is { Length: > 0 } host ? host : ConnectCommand.DefaultHostUrl).TrimEnd('/'),
                Token = token,
                GitAuthorName = NullIfEmpty(runner.GetValueOrDefault(AuthorNameKey)),
                GitAuthorEmail = NullIfEmpty(runner.GetValueOrDefault(AuthorEmailKey)),
            });

            File.Delete(hostedEnv);
            if (runner.ContainsKey(AuthorNameKey) || runner.ContainsKey(AuthorEmailKey))
            {
                File.Copy(runnerEnv, runnerEnv + ".migrated", overwrite: true);
                var kept = File.ReadAllLines(runnerEnv).Where(line =>
                {
                    var idx = line.IndexOf('=');
                    var key = idx > 0 ? line[..idx].Trim() : "";
                    return !string.Equals(key, AuthorNameKey, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(key, AuthorEmailKey, StringComparison.OrdinalIgnoreCase);
                });
                File.WriteAllLines(runnerEnv, kept);
            }
            return $"Moved this machine's Orkka connection into the workspace '{DefaultName}' ({ProfilePath(orkkaRoot, DefaultName)}).";
        }
        catch (Exception ex)
        {
            return $"Warning: could not move the Orkka connection into a workspace profile: {ex.Message}";
        }
    }

    internal static Dictionary<string, string> ReadEnv(string path) => ReadEnv(File.ReadAllLines(path));

    internal static Dictionary<string, string> ReadEnv(IEnumerable<string> lines)
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

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial Regex NamePattern();
}
