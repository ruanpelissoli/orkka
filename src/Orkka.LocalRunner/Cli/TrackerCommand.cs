namespace Orkka.LocalRunner.Cli;

using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Orkka.LocalRunner.Contracts;
using Orkka.LocalRunner.Trackers;

/// <summary>
/// `./orkka tracker add|list|test|remove` — the only way a tracker token gets onto this
/// machine. The token is typed (hidden) or piped in, tested at once, and stored in the OS
/// secret store; only the connection's metadata is written to trackers.json and reported to
/// Orkka. Nothing here ever prints the token, and every error is scrubbed of it.
/// </summary>
public static class TrackerCommand
{
    public const string JiraTokenUrl = "https://id.atlassian.com/manage-profile/security/api-tokens";

    public const string Usage =
        """
        ./orkka tracker add jira --site <acme.atlassian.net> --email <you@acme.com>
        ./orkka tracker add ado --org <https://dev.azure.com/acme | acme>
                              connect a Jira Cloud site / Azure DevOps organization; you are
                              asked for the token (hidden), or pipe it on stdin
        ./orkka tracker add github [--host <acme.ghe.com>] [--token]
                              connect GitHub Projects (github.com unless --host names your
                              GHE.com host) with your gh login — nothing is stored; it needs
                              the project scope: gh auth refresh -h <host> -s project — or,
                              with --token, a token you are asked for (hidden) or pipe in
        ./orkka tracker list            the connections on this machine
        ./orkka tracker test [id]       check the stored token(s) now
        ./orkka tracker remove <id>     delete a connection and its token

        Every command takes --workspace <name> (the job the connection belongs to); it can be
        left out while this machine has a single workspace.
        """;

    // The workspace the current command works in; set once per invocation by RunAsync.
    private static string _workspace = TrackerPaths.DefaultProfile;
    private static string _root = RunnerEnvFile.OrkkaDir;

    public const string GitHubTokenHelp =
        "Create a token at https://github.com/settings/tokens (on GHE.com: your enterprise's host): classic with the project and repo scopes,\n"
        + "or fine-grained with Projects (organization) and Issues read & write. If the organization enforces SAML SSO, authorize the token for it.";

    public static async Task<int> RunAsync(string[] args, string? orkkaRoot = null)
    {
        var root = orkkaRoot ?? RunnerEnvFile.OrkkaDir;
        _root = root;
        WorkspaceProfiles.MigrateLegacy(root);
        var (workspace, remaining) = TakeWorkspace(args);
        var sub = remaining.FirstOrDefault()?.ToLowerInvariant();
        var rest = remaining.Skip(1).ToArray();

        // `tracker list` with several workspaces and none named lists them all.
        if (workspace is null && sub is "list" or "ls" && WorkspaceProfiles.WorkspaceNames(root).Count > 1)
        {
            foreach (var name in WorkspaceProfiles.WorkspaceNames(root))
            {
                Console.WriteLine($"[{name}]");
                _workspace = name;
                List();
                Console.WriteLine();
            }
            return 0;
        }

        if (ResolveWorkspace(root, workspace, out var error) is not { } resolved)
            return Fail(error!);
        _workspace = resolved;
        try
        {
            return sub switch
            {
                "add" => await AddAsync(rest),
                "list" or "ls" => List(),
                "test" or "check" => await TestAsync(rest),
                "remove" or "rm" or "delete" => Remove(rest),
                null or "help" or "--help" or "-h" => PrintUsage(0),
                _ => Fail($"Unknown tracker command '{sub}'.\n\n{Usage}"),
            };
        }
        catch (OperationCanceledException)
        {
            return Fail("Cancelled.");
        }
    }

    /// <summary>Pulls <c>--workspace &lt;name&gt;</c> (or <c>-w</c>, or <c>--workspace=name</c>) out of the arguments.</summary>
    internal static (string? Workspace, string[] Remaining) TakeWorkspace(string[] args)
    {
        string? workspace = null;
        var remaining = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--workspace=", StringComparison.OrdinalIgnoreCase))
                workspace = arg["--workspace=".Length..];
            else if (arg is "--workspace" or "-w" && i + 1 < args.Length)
                workspace = args[++i];
            else
                remaining.Add(arg);
        }
        return (workspace?.Trim().ToLowerInvariant(), [.. remaining]);
    }

    /// <summary>The named workspace, or — when none is named — the only one this machine has
    /// ("default" when it has none yet). Several and none named is an error: a tracker token
    /// belongs to one job.</summary>
    internal static string? ResolveWorkspace(string root, string? named, out string? error)
    {
        error = null;
        var known = WorkspaceProfiles.WorkspaceNames(root)
            .Union(WorkspaceProfiles.List(root).Select(p => p.Name))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (named is not null)
        {
            if (!WorkspaceProfiles.IsValidName(named))
            {
                error = $"'{named}' is not a workspace name — use lowercase letters, digits and hyphens.";
                return null;
            }
            if (known.Count > 0 && !known.Contains(named))
            {
                error = $"There is no workspace '{named}' on this machine (it has: {string.Join(", ", known)}). "
                    + $"Create it in Orkka first; a running `./orkka start` adds it to this machine within a minute.";
                return null;
            }
            return named;
        }
        if (known.Count > 1)
        {
            error = $"This machine has several workspaces ({string.Join(", ", known)}) — say which job this is for: --workspace <name>.";
            return null;
        }
        return known.Count == 1 ? known[0] : TrackerPaths.DefaultProfile;
    }

    /// <summary>Deletes every stored tracker token of every workspace — `uninstall` removes
    /// ~/.orkka, but an OS keyring entry outlives the folder. Never throws.</summary>
    public static void RemoveAllSecrets(string orkkaRoot, TextWriter output, Action<string>? removed = null)
    {
        var names = WorkspaceProfiles.WorkspaceNames(orkkaRoot);
        foreach (var name in names.Count > 0 ? names : [TrackerPaths.DefaultProfile])
        {
            RemoveAllSecrets(orkkaRoot, name, output, removed: removed);
            WorkspaceProviderEnv.DeleteAllSecrets(orkkaRoot, name, output, removed: removed);
            WorkspaceProfiles.DeleteToken(orkkaRoot, name, output, removed: removed);
        }
    }

    /// <summary>Deletes one workspace's tracker tokens. Never throws.</summary>
    public static void RemoveAllSecrets(string root, string workspace, TextWriter output, IWorkspaceSecretStore? secrets = null,
        Action<string>? removed = null)
    {
        try
        {
            var service = CreateService(root, secrets, workspace: workspace);
            foreach (var entry in service.Store.List())
            {
                try
                {
                    if (service.Secrets.Get(entry.Id) is null)
                        continue;
                    service.Secrets.Delete(entry.Id);
                    removed?.Invoke(WorkspaceSecretStores.KeyFor(workspace, entry.Id));
                }
                catch (Exception ex) { output.WriteLine($"  could not delete the token for {entry.Id}: {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            output.WriteLine($"  could not read the tracker connections of '{workspace}': {ex.Message}");
        }
    }

    internal static TrackerService CreateService(string? orkkaRoot = null, IWorkspaceSecretStore? secrets = null, HttpClient? http = null,
        string? workspace = null)
    {
        var root = orkkaRoot ?? _root;
        var profile = workspace ?? _workspace;
        http ??= TrackerHttp.CreateDefault();
        return new TrackerService(
            new TrackerStore(root, profile),
            secrets ?? WorkspaceSecretStores.CreateDefault(root, profile),
            new JiraCloudClient(http),
            new AzureDevOpsClient(http),
            NullLogger<TrackerService>.Instance);
    }

    private static async Task<int> AddAsync(string[] args)
    {
        var kind = TrackerConnectionManager.NormalizeKind(args.FirstOrDefault());
        if (kind is null)
            return Fail($"Say which tracker to add: jira, ado or github.\n\n{Usage}");
        if (kind == TrackerKinds.GitHub)
            return await AddGitHubAsync(args.Skip(1).ToArray());

        var options = ParseOptions(args.Skip(1));
        string siteUrl;
        string? email = null;
        if (kind == TrackerKinds.Jira)
        {
            if (TrackerSites.NormalizeJiraSite(options.GetValueOrDefault("site")) is not { } site)
                return Fail("Give your Jira Cloud site: --site acme.atlassian.net");
            email = options.GetValueOrDefault("email")?.Trim();
            if (string.IsNullOrEmpty(email) || !email.Contains('@'))
                return Fail("Give the email of your Atlassian account: --email you@acme.com (Jira API tokens authenticate as email + token).");
            siteUrl = site;
        }
        else
        {
            if (TrackerSites.NormalizeAdoOrg(options.GetValueOrDefault("org") ?? options.GetValueOrDefault("site")) is not { } org)
                return Fail("Give your Azure DevOps organization: --org https://dev.azure.com/acme (or just acme).");
            siteUrl = org;
        }

        var service = CreateService();
        var interactive = !Console.IsInputRedirected;
        if (interactive)
        {
            Console.WriteLine(kind == TrackerKinds.Jira
                ? $"Create an API token at {JiraTokenUrl} (signed in as {email})."
                : "Create a personal access token in Azure DevOps: User settings → Personal access tokens → New Token,\n"
                    + $"for the organization {siteUrl[(siteUrl.LastIndexOf('/') + 1)..]}, with the Work Items (Read & write) and Project and Team (Read) scopes.");
            Console.WriteLine("The token stays on this machine — Orkka's servers never see it.");
        }
        var token = ReadToken(kind == TrackerKinds.Jira ? "Jira API token: " : "Personal access token: ", interactive);
        if (string.IsNullOrWhiteSpace(token))
            return Fail("No token given — nothing was saved.");
        Console.WriteLine($"Checking the token against {siteUrl} ...");
        // The same test-and-store the settings page uses (AddTrackerConnection).
        var existing = service.Store.FindSame(kind, siteUrl, email);
        var result = await new TrackerConnectionManager(service).AddAsync(
            new AddTrackerConnectionRequest(kind, siteUrl, email, token), CancellationToken.None);
        if (!result.Ok || result.Connection is not { } added)
            return Fail(result.Error ?? "Nothing was saved.");

        Console.WriteLine();
        Console.WriteLine(existing is null
            ? $"Added {added.Id} — signed in as {added.DisplayName ?? "(unknown)"}."
            : $"Updated the token for {added.Id} — signed in as {added.DisplayName ?? "(unknown)"}.");
        Console.WriteLine($"The token is stored in {service.Secrets.Description}, for the workspace '{_workspace}'.");
        Console.WriteLine("Next: in Orkka, pick it in your project's Ticket source settings. A running `./orkka start` "
            + "reports it within a minute.");
        return 0;
    }

    /// <summary>`tracker add github`: the gh login by default (nothing stored), or --token.</summary>
    private static async Task<int> AddGitHubAsync(string[] args)
    {
        var options = ParseOptions(args);
        var useToken = args.Any(a => string.Equals(a, "--token", StringComparison.OrdinalIgnoreCase));
        if (TrackerSites.NormalizeGitHubHost(options.GetValueOrDefault("host") ?? options.GetValueOrDefault("site")) is not { } siteUrl)
            return Fail(TrackerConnectionManager.GitHubServerNotSupported);
        var host = TrackerSites.GitHubCliHost(siteUrl);

        var service = CreateService();
        var interactive = !Console.IsInputRedirected;
        string token = "";
        if (useToken)
        {
            if (interactive)
            {
                Console.WriteLine(GitHubTokenHelp);
                Console.WriteLine("The token stays on this machine — Orkka's servers never see it.");
            }
            token = ReadToken("GitHub token: ", interactive) ?? "";
            if (string.IsNullOrWhiteSpace(token))
                return Fail("No token given — nothing was saved.");
            Console.WriteLine($"Checking the token against {siteUrl} ...");
        }
        else
        {
            Console.WriteLine($"Using your GitHub CLI login for {host} (`gh auth token -h {host}`) — Orkka stores nothing.");
        }

        var existing = service.Store.FindSame(TrackerKinds.GitHub, siteUrl, null);
        var result = await new TrackerConnectionManager(service).AddAsync(
            new AddTrackerConnectionRequest(TrackerKinds.GitHub, siteUrl, null, token,
                useToken ? TrackerAuthModes.Token : TrackerAuthModes.GhCli), CancellationToken.None);
        if (!result.Ok || result.Connection is not { } added)
            return Fail(result.Error ?? "Nothing was saved.");

        Console.WriteLine();
        Console.WriteLine(existing is null
            ? $"Added {added.Id} — signed in as {added.DisplayName ?? "(unknown)"}."
            : $"Updated {added.Id} — signed in as {added.DisplayName ?? "(unknown)"}.");
        Console.WriteLine(useToken
            ? $"The token is stored in {service.Secrets.Description}, for the workspace '{_workspace}'."
            : $"Orkka asks gh for the token at each call; `gh auth logout -h {host}` stops it.");
        Console.WriteLine("Next: in Orkka, pick it in your project's Ticket source settings (an owner, then one of its projects). "
            + "A running `./orkka start` reports it within a minute.");
        return 0;
    }

    private static int List()
    {
        var service = CreateService();
        var entries = service.Store.List();
        if (entries.Count == 0)
        {
            Console.WriteLine("No tracker connections on this machine yet. Add one with:");
            Console.WriteLine("  ./orkka tracker add jira --site acme.atlassian.net --email you@acme.com");
            Console.WriteLine("  ./orkka tracker add ado --org https://dev.azure.com/acme");
            Console.WriteLine("  ./orkka tracker add github             (your gh login; --host acme.ghe.com, --token)");
            return 0;
        }
        PrintTable(entries);
        return 0;
    }

    private static async Task<int> TestAsync(string[] args)
    {
        var service = CreateService();
        var id = args.FirstOrDefault();
        var entries = id is null
            ? service.Store.List().ToList()
            : service.Store.Get(id) is { } one ? [one] : [];
        if (entries.Count == 0)
            return id is null ? List() : Fail($"No tracker connection '{id}' on this machine — see `./orkka tracker list`.");

        var failed = false;
        foreach (var entry in entries)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var result = await service.CheckAsync(entry, timeout.Token);
            failed |= !result.Healthy;
            Console.WriteLine(result.Healthy
                ? $"{result.Id}: ok — signed in as {result.DisplayName ?? "(unknown)"}"
                : $"{result.Id}: failing — {result.HealthDetail}");
        }
        return failed ? 1 : 0;
    }

    private static int Remove(string[] args)
    {
        var id = args.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(id))
            return Fail("Say which connection to remove: ./orkka tracker remove <id> (see `./orkka tracker list`).");

        var service = CreateService();
        var entry = service.Store.Get(id);
        if (entry is null)
            return Fail($"No tracker connection '{id}' on this machine — see `./orkka tracker list`.");

        var removed = new TrackerConnectionManager(service).Remove(entry.Id);
        if (!removed.Ok)
            return Fail(removed.Error ?? "The connection could not be removed.");
        Console.WriteLine($"Removed {entry.Id} and its token. Projects using it in Orkka stop picking up tickets from it.");
        return 0;
    }

    internal static void PrintTable(IReadOnlyList<TrackerConnectionEntry> entries)
    {
        string[] headers = ["ID", "KIND", "SITE", "ACCOUNT", "HEALTH"];
        var rows = entries.Select(e => new[]
        {
            e.Id,
            e.UsesGhCli ? $"{e.Kind} (gh login)" : e.Kind,
            e.SiteUrl,
            e.DisplayName ?? e.Email ?? "-",
            e.CheckedAt is null ? "unchecked" : e.Healthy ? "ok" : $"failing: {e.HealthDetail}",
        }).ToList();
        var widths = headers.Select((h, i) => rows.Select(r => r[i].Length).Prepend(h.Length).Max()).ToArray();
        string Line(string[] cells) =>
            string.Join("  ", cells.Select((c, i) => i == cells.Length - 1 ? c : c.PadRight(widths[i]))).TrimEnd();
        Console.WriteLine(Line(headers));
        foreach (var row in rows)
            Console.WriteLine(Line(row));
    }

    internal static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var arg = list[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
                continue;
            var name = arg[2..];
            var eq = name.IndexOf('=');
            if (eq >= 0)
                options[name[..eq]] = name[(eq + 1)..];
            else if (i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal))
                options[name] = list[++i];
        }
        return options;
    }

    /// <summary>
    /// Reads the token without echoing it. Piped stdin (scripts, CI) is read as one line, so
    /// `pass show jira | ./orkka tracker add …` works; a terminal gets a hidden prompt —
    /// never a command-line flag, which would land in shell history and the process list.
    /// </summary>
    internal static string? ReadToken(string prompt, bool interactive)
    {
        if (!interactive)
            return Console.In.ReadLine();

        Console.Write(prompt);
        var buffer = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                    buffer.Length--;
                continue;
            }
            if (key.Key == ConsoleKey.Escape)
            {
                buffer.Clear();
                break;
            }
            if (!char.IsControl(key.KeyChar))
                buffer.Append(key.KeyChar);
        }
        Console.WriteLine();
        return buffer.ToString();
    }

    private static int PrintUsage(int code)
    {
        Console.WriteLine(Usage);
        return code;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
