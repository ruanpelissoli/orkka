namespace Orkka.LocalRunner.Cli;

using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Execution.Agents;

/// <summary>
/// `./orkka workspace list | remove &lt;name&gt; | set-identity &lt;name&gt; --name … --email …`
/// — the jobs this machine works for. A running <c>./orkka start</c> gives every workspace
/// created in Orkka a profile by itself; `./orkka connect --workspace &lt;name&gt; --token
/// orkr_…` adds one by hand (advanced). Nothing here prints a token.
/// </summary>
public static class WorkspaceCommand
{
    public const string Usage =
        """
        ./orkka workspace list                  the workspaces (jobs) on this machine
        ./orkka workspace remove <name>         forget a workspace: its connection, tracker
                                                connections and their tokens, ticket cache
        ./orkka workspace set-identity <name> --name "Dana Silva" --email dana@acme.com
                                                the git author for that job's commits
        ./orkka workspace set-identity <name> --clear
                                                use your git config's identity again
        ./orkka workspace set-env <name> KEY    this job's AI sign-in for one provider (e.g.
                                                ANTHROPIC_API_KEY, CODEX_API_KEY, GH_TOKEN): a
                                                secret is asked for with no echo (or read from
                                                piped stdin) and kept in the OS secret store
        ./orkka workspace set-env <name> KEY=value
                                                a plain setting: CLAUDE_CODE_USE_BEDROCK=1,
                                                AWS_REGION=us-east-1, AWS_PROFILE=work,
                                                ANTHROPIC_BASE_URL=…, GH_HOST=acme.ghe.com
        ./orkka workspace set-env <name> ORKKA_CLAUDE_AUTH=login
                                                this job uses the CLI's own login, never a
                                                machine-wide key (also ORKKA_CODEX_AUTH,
                                                ORKKA_COPILOT_AUTH)
        ./orkka workspace unset-env <name> KEY  remove one
        ./orkka workspace env <name>            what this job overrides (secret values never shown)
        """;

    /// <summary>Test seam: reads a value (prompt, secret?) — no echo for a secret; piped
    /// stdin is read as one line.</summary>
    internal static Func<string, bool, string?> ReadValue { get; set; } = (prompt, secret) =>
    {
        var interactive = !Console.IsInputRedirected;
        if (secret)
            return TrackerCommand.ReadToken(prompt, interactive);
        if (interactive)
            Console.Write(prompt);
        return Console.In.ReadLine();
    };

    public static int Run(string[] args, string? orkkaRoot = null, TextWriter? output = null, TextWriter? errors = null,
        Func<string, Trackers.IWorkspaceSecretStore>? secretStores = null)
    {
        var root = orkkaRoot ?? RunnerEnvFile.OrkkaDir;
        var @out = output ?? Console.Out;
        var err = errors ?? Console.Error;
        WorkspaceProfiles.MigrateLegacy(root);

        var sub = args.FirstOrDefault()?.ToLowerInvariant();
        var rest = args.Skip(1).ToArray();
        return sub switch
        {
            "list" or "ls" or null => List(root, @out),
            "remove" or "rm" or "delete" => Remove(root, rest, @out, err),
            "set-identity" or "identity" => SetIdentity(root, rest, @out, err),
            "set-env" => SetEnv(root, rest, @out, err, secretStores),
            "unset-env" => UnsetEnv(root, rest, @out, err, secretStores),
            "env" => ShowEnv(root, rest, @out, err),
            "help" or "--help" or "-h" => Print(@out, Usage, 0),
            _ => Print(err, $"Unknown workspace command '{sub}'.\n\n{Usage}", 1),
        };
    }

    private static int List(string root, TextWriter @out)
    {
        var profiles = WorkspaceProfiles.List(root);
        if (profiles.Count == 0)
        {
            @out.WriteLine("No workspaces on this machine yet. Run `./orkka start`: every workspace you create in");
            @out.WriteLine("Orkka gets a profile here within a minute. (By hand, advanced:");
            @out.WriteLine("  ./orkka connect --workspace <name> --token orkr_...)");
            return 0;
        }
        string[] headers = ["WORKSPACE", "HOST", "GIT AUTHOR", "TRACKERS"];
        var rows = profiles.Select(p => new[]
        {
            p.Name,
            p.HostUrl,
            p.HasIdentity ? $"{p.GitAuthorName} <{p.GitAuthorEmail}>" : "(git config)",
            TrackerCount(root, p.Name).ToString(System.Globalization.CultureInfo.InvariantCulture),
        }).ToList();
        var widths = headers.Select((h, i) => rows.Select(r => r[i].Length).Prepend(h.Length).Max()).ToArray();
        string Line(string[] cells) =>
            string.Join("  ", cells.Select((c, i) => i == cells.Length - 1 ? c : c.PadRight(widths[i]))).TrimEnd();
        @out.WriteLine(Line(headers));
        foreach (var row in rows)
            @out.WriteLine(Line(row));
        return 0;
    }

    private static int Remove(string root, string[] args, TextWriter @out, TextWriter err)
    {
        var name = args.FirstOrDefault()?.Trim().ToLowerInvariant();
        if (!WorkspaceProfiles.IsValidName(name) || !WorkspaceProfiles.WorkspaceNames(root).Contains(name))
            return Print(err, $"No workspace '{args.FirstOrDefault()}' on this machine — see `./orkka workspace list`.", 1);

        // Tracker tokens live in the OS secret store, which outlives the folder.
        TrackerCommand.RemoveAllSecrets(root, name!, @out);
        WorkspaceProviderEnv.DeleteAllSecrets(root, name!, @out);
        WorkspaceProfiles.DeleteToken(root, name!, @out);
        try
        {
            Directory.Delete(Trackers.TrackerPaths.WorkspaceDir(root, name!), recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Print(err, $"Could not remove the workspace folder: {ex.Message}", 1);
        }
        @out.WriteLine($"Removed the workspace '{name}'. A running Orkka stops serving it after a restart (`./orkka stop`, then `./orkka start`).");
        return 0;
    }

    private static int SetIdentity(string root, string[] args, TextWriter @out, TextWriter err)
    {
        var name = args.FirstOrDefault()?.Trim().ToLowerInvariant();
        if (!WorkspaceProfiles.IsValidName(name) || WorkspaceProfiles.Get(root, name!) is not { } profile)
            return Print(err, $"No workspace '{args.FirstOrDefault()}' on this machine — see `./orkka workspace list`.", 1);

        var options = TrackerCommand.ParseOptions(args.Skip(1));
        var clear = args.Any(a => string.Equals(a, "--clear", StringComparison.OrdinalIgnoreCase));
        string? authorName = null, authorEmail = null;
        if (!clear)
        {
            authorName = options.GetValueOrDefault("name")?.Trim();
            authorEmail = options.GetValueOrDefault("email")?.Trim();
            if (string.IsNullOrEmpty(authorName) || string.IsNullOrEmpty(authorEmail) || !authorEmail.Contains('@'))
                return Print(err, $"Give both: --name \"Your Name\" --email you@company.com (or --clear).\n\n{Usage}", 1);
        }

        WorkspaceProfiles.Save(root, new WorkspaceProfile
        {
            Name = profile.Name,
            HostUrl = profile.HostUrl,
            Token = profile.Token,
            GitAuthorName = authorName,
            GitAuthorEmail = authorEmail,
            WorkspaceId = profile.WorkspaceId,
        });
        @out.WriteLine(clear
            ? $"Commits for '{profile.Name}' use your git config's identity again."
            : $"Commits for '{profile.Name}' are authored as {authorName} <{authorEmail}> (new clones; restart Orkka: `./orkka stop`, then `./orkka start`).");
        return 0;
    }

    private static int SetEnv(string root, string[] args, TextWriter @out, TextWriter err,
        Func<string, Trackers.IWorkspaceSecretStore>? secretStores)
    {
        var name = args.FirstOrDefault()?.Trim().ToLowerInvariant();
        if (!WorkspaceProfiles.IsValidName(name) || !File.Exists(WorkspaceProfiles.ProfilePath(root, name!)))
            return Print(err, $"No workspace '{args.FirstOrDefault()}' on this machine — see `./orkka workspace list`.", 1);
        var spec = args.Skip(1).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(spec))
            return Print(err, $"Name the variable: ./orkka workspace set-env {name} KEY\n\n{Usage}", 1);

        var eq = spec.IndexOf('=');
        var key = (eq > 0 ? spec[..eq] : spec).Trim();
        var inline = eq > 0 ? spec[(eq + 1)..] : null;
        var codexKeys = CodexConfigKeys();
        if (!ProviderAuthEnvironment.IsAllowlisted(key, codexKeys))
            return Print(err,
                $"'{key}' is not an AI sign-in variable Orkka passes to a CLI. The accepted names are in "
                + "AGENTS.md → Sign-in modes (Claude: ANTHROPIC_API_KEY, CLAUDE_CODE_USE_BEDROCK, AWS_REGION, …; "
                + "Codex: CODEX_API_KEY, AZURE_OPENAI_API_KEY or your config.toml env_key; Copilot: "
                + "COPILOT_GITHUB_TOKEN, GH_TOKEN, GH_HOST).", 1);

        var secret = ProviderAuthEnvironment.IsSecret(key);
        if (secret && inline is not null)
            return Print(err,
                $"{key} is a secret — never put it on the command line (shell history, process list). "
                + $"Run `./orkka workspace set-env {name} {key}` and type it at the prompt, or pipe it in.", 1);

        var value = inline ?? ReadValue($"{key} for '{name}': ", secret);
        if (string.IsNullOrWhiteSpace(value))
            return Print(err, "Nothing entered — nothing changed.", 1);
        if (key.StartsWith("ORKKA_", StringComparison.Ordinal) && key.EndsWith("_AUTH", StringComparison.Ordinal)
            && !string.Equals(value.Trim(), ProviderAuthEnvironment.LoginOnlyValue, StringComparison.OrdinalIgnoreCase))
            return Print(err, $"{key} takes one value: login (this job uses the CLI's own sign-in).", 1);

        try
        {
            WorkspaceProviderEnv.Set(root, name!, key, value, secretStores?.Invoke(name!), codexKeys);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Print(err, $"Could not save {key}: {ex.Message}", 1);
        }

        var providers = ProviderAuthEnvironment.ProvidersOf(key).Select(ProviderName).DefaultIfEmpty("Codex");
        @out.WriteLine(secret
            ? $"Saved {key} for '{name}' in the OS secret store ({string.Join(", ", providers)}). It is handed only to that CLI, for this workspace's runs."
            : $"Saved {key}={value.Trim()} for '{name}' ({string.Join(", ", providers)}).");
        @out.WriteLine("A workspace that sets any of a provider's variables uses only its own for that provider; the machine-wide ones stop applying to it. Takes effect on the next run.");
        return 0;
    }

    private static string ProviderName(AiProvider provider) => provider switch
    {
        AiProvider.Anthropic => "Claude",
        AiProvider.OpenAI => "Codex",
        _ => "Copilot",
    };

    private static int UnsetEnv(string root, string[] args, TextWriter @out, TextWriter err,
        Func<string, Trackers.IWorkspaceSecretStore>? secretStores)
    {
        var name = args.FirstOrDefault()?.Trim().ToLowerInvariant();
        var key = args.Skip(1).FirstOrDefault()?.Trim();
        if (!WorkspaceProfiles.IsValidName(name) || !File.Exists(WorkspaceProfiles.ProfilePath(root, name!)))
            return Print(err, $"No workspace '{args.FirstOrDefault()}' on this machine — see `./orkka workspace list`.", 1);
        if (string.IsNullOrWhiteSpace(key) || !ProviderAuthEnvironment.IsAllowlisted(key, CodexConfigKeys()))
            return Print(err, $"Name an AI sign-in variable: ./orkka workspace unset-env {name} KEY", 1);
        var had = WorkspaceProviderEnv.Unset(root, name!, key, secretStores?.Invoke(name!));
        @out.WriteLine(had ? $"Removed {key} from '{name}'." : $"'{name}' had no {key}.");
        return 0;
    }

    private static int ShowEnv(string root, string[] args, TextWriter @out, TextWriter err)
    {
        var name = args.FirstOrDefault()?.Trim().ToLowerInvariant();
        if (!WorkspaceProfiles.IsValidName(name) || !File.Exists(WorkspaceProfiles.ProfilePath(root, name!)))
            return Print(err, $"No workspace '{args.FirstOrDefault()}' on this machine — see `./orkka workspace list`.", 1);
        var (plain, secrets) = WorkspaceProviderEnv.Describe(root, name!);
        if (plain.Count == 0 && secrets.Count == 0)
        {
            @out.WriteLine($"'{name}' uses the machine's AI sign-in (the CLIs' own logins, ~/.orkka/runner.env).");
            return 0;
        }
        foreach (var (key, value) in plain.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            @out.WriteLine($"{key}={value}");
        foreach (var key in secrets.Order(StringComparer.Ordinal))
            @out.WriteLine($"{key}=(set, in the OS secret store)");
        return 0;
    }

    /// <summary>The env_key names the developer's Codex config declares (names only).</summary>
    private static IReadOnlyCollection<string> CodexConfigKeys()
    {
        try
        {
            var path = RunnerProviderAuthSource.CodexConfigPath(
                Environment.GetEnvironmentVariable("CODEX_HOME"),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            return File.Exists(path) ? ProviderAuthEnvironment.ParseCodexEnvKeys(File.ReadAllText(path)) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static int TrackerCount(string root, string name)
    {
        try
        {
            return new Trackers.TrackerStore(root, name).List().Count;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static int Print(TextWriter writer, string text, int code)
    {
        writer.WriteLine(text);
        return code;
    }
}
