namespace Orkka.Modules.Execution.Agents;

/// <summary>
/// Where a developer's machine keeps the credentials an agent must never read: their forge and
/// cloud sign-ins, SSH and GPG keys, package-registry tokens, the AI CLIs' own logins, the OS
/// credential stores, browser profiles, and Orkka's own home (runner tokens, tracker settings,
/// and every OTHER workspace's clones — a developer's second employer's code is not this run's
/// to read).
///
/// One list, two consumers: <see cref="AgentSensitivePathGuard"/> checks the file tools' paths
/// against it, and <see cref="AgentBashGuard"/> refuses a shell line that names any of it. The
/// run's own worktree is always exempt — it is the repository, and a repo may well contain a
/// folder called <c>.azure</c> or <c>.kube</c> (azd and helm projects do).
/// </summary>
public static class DeveloperSecrets
{
    /// <summary>
    /// Credential locations relative to a home directory, lower-case, forward slashes. Matched as
    /// a path prefix under ANY home (the developer's, or another account's on the same disk).
    /// </summary>
    public static readonly string[] HomeRelative =
    [
        // Keys and generic secret stores.
        ".ssh", ".gnupg", ".netrc", "_netrc", ".git-credentials", ".config/git/credentials",
        ".local/share/keyrings", "library/keychains",
        "appdata/roaming/microsoft/credentials", "appdata/local/microsoft/credentials",
        "appdata/roaming/microsoft/protect", "appdata/roaming/microsoft/crypto",

        // Forge and cloud sign-ins.
        ".config/gh", ".config/glab-cli", ".aws", ".azure", ".config/gcloud", ".kube",
        ".docker/config.json", ".terraform.d/credentials.tfrc.json", ".config/doctl", ".fly",

        // Package-registry tokens.
        ".npmrc", ".yarnrc", ".yarnrc.yml", ".pypirc", ".gem/credentials", ".m2/settings.xml",
        ".m2/settings-security.xml", ".gradle/gradle.properties", ".cargo/credentials",
        ".cargo/credentials.toml", ".nuget/nuget/nuget.config", "appdata/roaming/nuget/nuget.config",
        ".composer/auth.json", ".config/composer/auth.json",

        // The AI CLIs' own logins (the subscription this run is spending).
        ".claude/.credentials.json", ".claude.json", ".codex/auth.json", ".gemini/oauth_creds.json",
        ".gemini/google_accounts.json", ".copilot", ".config/github-copilot", ".grok",
        ".local/share/opencode/auth.json",

        // Browser profiles: cookies and saved passwords.
        ".mozilla", "appdata/roaming/mozilla", ".config/google-chrome", ".config/chromium",
        ".config/microsoft-edge", "appdata/local/google/chrome/user data",
        "appdata/local/microsoft/edge/user data", "library/application support/google/chrome",
        "library/application support/firefox", "library/cookies",
    ];

    /// <summary>
    /// Entries of <see cref="HomeRelative"/> that a repository commonly carries ITSELF — a
    /// project <c>.npmrc</c>, an azd <c>.azure/</c> folder, a <c>.kube/</c> folder of manifests.
    /// These match only under the developer's own home, never as a bare path segment.
    /// </summary>
    public static readonly HashSet<string> HomeOnly = new(StringComparer.Ordinal)
    {
        ".npmrc", ".yarnrc", ".yarnrc.yml", ".azure", ".kube", ".docker/config.json", ".fly",
        ".config/doctl", ".gradle/gradle.properties", ".composer/auth.json", ".claude.json",
    };

    /// <summary>System files that are never an agent's business, on any OS.</summary>
    public static readonly string[] Absolute =
    [
        "/etc/shadow", "/etc/gshadow", "/etc/sudoers", "/etc/passwd",
        "c:/windows/system32/config",
    ];

    /// <summary>
    /// Substrings that name a credential location in a shell line (lower-case, forward slashes).
    /// Written so a relative path INSIDE the repository does not match: they either start at a
    /// home marker or carry the separator that only an absolute path has.
    /// </summary>
    public static readonly string[] ShellFragments = BuildShellFragments();

    private static string[] BuildShellFragments()
    {
        var fragments = new List<string>();
        var home = UserHome().ToLowerInvariant();
        foreach (var entry in HomeRelative)
        {
            fragments.Add(home + "/" + entry);
            if (home.Length > 2 && home[1] == ':')
                fragments.Add("/" + char.ToLowerInvariant(home[0]) + home[2..] + "/" + entry);
            // ~/.ssh, $home/.ssh, %userprofile%/.ssh, $env:userprofile/.ssh and /.ssh (any
            // absolute spelling: /home/x/.ssh, c:/users/x/.ssh).
            fragments.Add("~/" + entry);
            fragments.Add("$home/" + entry);
            fragments.Add("${home}/" + entry);
            fragments.Add("%userprofile%/" + entry);
            fragments.Add("$env:userprofile/" + entry);
            if (!HomeOnly.Contains(entry))
                fragments.Add("/" + entry);
        }

        fragments.AddRange(Absolute);
        // Cloud instance metadata — the credentials of whatever VM a dev box runs on.
        fragments.Add("169.254.169.254");
        fragments.Add("metadata.google.internal");
        return fragments.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// The Orkka home (<c>ORKKA_HOME</c>, else <c>~/.orkka</c>), normalised. Every workspace's
    /// clones, its runner token and its tracker settings live under it.
    /// </summary>
    public static string OrkkaHome()
    {
        var configured = Environment.GetEnvironmentVariable("ORKKA_HOME");
        var home = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".orkka")
            : configured;
        return AgentWorktreeBoundaryGuard.Normalize(home);
    }

    /// <summary>The user's home directory, normalised.</summary>
    public static string UserHome() =>
        AgentWorktreeBoundaryGuard.Normalize(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// True when <paramref name="normalizedPath"/> (absolute, <see cref="AgentWorktreeBoundaryGuard.Normalize"/>d)
    /// is a credential location or sits inside the Orkka home outside <paramref name="worktree"/>.
    /// </summary>
    public static bool IsSensitivePath(string normalizedPath, string? worktree, string orkkaHome)
    {
        var path = normalizedPath.ToLowerInvariant();
        var tree = string.IsNullOrWhiteSpace(worktree)
            ? null
            : AgentWorktreeBoundaryGuard.Normalize(worktree).ToLowerInvariant();

        if (tree is not null && IsUnder(path, tree))
            return false;

        if (IsUnder(path, orkkaHome.ToLowerInvariant()))
            return true;

        // A dotenv file anywhere outside the worktree: a sibling repository's `.env`, the
        // developer's checkout of this one, a parent folder's. Agents on a folder-of-repos
        // project may read the siblings for reference, never their secrets. Committed templates
        // (`.env.example`, `.env.sample`, `.env.template`, …) stay readable — they are how a
        // project says which variables it needs.
        if (IsDotenvFile(path))
            return true;

        foreach (var absolute in Absolute)
        {
            if (IsUnder(path, absolute) || (absolute.StartsWith('/') && IsUnder(path, "c:" + absolute)))
                return true;
        }

        // A home-relative entry anywhere in the path: /home/dev/.ssh/id_ed25519,
        // C:/Users/dev/AppData/Roaming/Microsoft/Credentials/… The repo-shaped ones only under
        // the developer's own home.
        var home = UserHome().ToLowerInvariant();
        foreach (var entry in HomeRelative)
        {
            if (HomeOnly.Contains(entry))
            {
                if (IsUnder(path, home + "/" + entry))
                    return true;
                continue;
            }

            var marker = "/" + entry;
            var at = path.IndexOf(marker, StringComparison.Ordinal);
            while (at >= 0)
            {
                var end = at + marker.Length;
                if (end == path.Length || path[end] == '/')
                    return true;
                at = path.IndexOf(marker, at + 1, StringComparison.Ordinal);
            }
        }

        return false;
    }

    /// <summary>True when the last segment of <paramref name="normalizedPath"/> is a real dotenv
    /// file (<c>.env</c>, <c>.env.local</c>, …) and not a committed template.</summary>
    public static bool IsDotenvFile(string normalizedPath)
    {
        var path = normalizedPath.Replace('\\', '/').TrimEnd('/');
        var slash = path.LastIndexOf('/');
        var name = (slash >= 0 ? path[(slash + 1)..] : path).ToLowerInvariant();
        return ShellCommandParser.IsRealEnvFileName(name);
    }

    /// <summary>
    /// True when a shell line names a credential location, or reaches into the Orkka home
    /// anywhere but the run's own worktree.
    /// </summary>
    public static bool ShellLineTouchesSecrets(string lowerCommand, string? worktree)
    {
        var line = lowerCommand.Replace('\\', '/');

        // The run's own worktree lives under the Orkka home on the developer's machine, so an
        // absolute `cd` into it is ordinary. Cut it out first; whatever still names the Orkka
        // home after that is another task, another workspace, or the runner's own files.
        if (!string.IsNullOrWhiteSpace(worktree))
        {
            var tree = AgentWorktreeBoundaryGuard.Normalize(worktree).ToLowerInvariant();
            line = line.Replace(tree, " ", StringComparison.Ordinal);
            // Git Bash spells C:/x as /c/x.
            if (tree.Length > 2 && tree[1] == ':')
                line = line.Replace("/" + char.ToLowerInvariant(tree[0]) + tree[2..], " ", StringComparison.Ordinal);
        }

        // The Orkka home, however it is spelled. A RELATIVE `.orkka/context/…` is the run's own
        // context folder inside the repository and stays readable; none of these match it.
        var orkkaHome = OrkkaHome().ToLowerInvariant();
        string[] orkkaSpellings =
        [
            orkkaHome, "~/.orkka", "$home/.orkka", "${home}/.orkka", "%userprofile%/.orkka",
            "$env:userprofile/.orkka", "$orkka_home", "${orkka_home}", "%orkka_home%", "$env:orkka_home",
            "/.orkka/repos", "/.orkka/workspaces", "/.orkka/hosted.env",
        ];
        if (orkkaHome.Length > 2 && orkkaHome[1] == ':')
            orkkaSpellings = [.. orkkaSpellings, "/" + char.ToLowerInvariant(orkkaHome[0]) + orkkaHome[2..]];

        if (orkkaSpellings.Any(s => ContainsPathFragment(line, s)))
            return true;

        return ShellFragments.Any(f => ContainsPathFragment(line, f));
    }

    /// <summary>A fragment matches only when it ends at a path boundary, so <c>/.aws</c> does not
    /// match <c>/.awsome-project</c>.</summary>
    private static bool ContainsPathFragment(string line, string fragment)
    {
        var at = line.IndexOf(fragment, StringComparison.Ordinal);
        while (at >= 0)
        {
            var end = at + fragment.Length;
            if (end == line.Length || !(char.IsLetterOrDigit(line[end]) || line[end] is '-' or '_' or '.'))
                return true;
            at = line.IndexOf(fragment, at + 1, StringComparison.Ordinal);
        }

        return false;
    }

    private static bool IsUnder(string path, string root)
    {
        if (path.Equals(root, StringComparison.Ordinal))
            return true;
        var prefix = root.EndsWith('/') ? root : root + "/";
        return path.StartsWith(prefix, StringComparison.Ordinal);
    }
}
