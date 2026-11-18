namespace Orkka.Modules.Execution.Agents;

using System.Text.Json;
using ih0.Claude.Agent.SDK.Types;
using Microsoft.Extensions.Logging;

/// <summary>
/// Pre-execution hook that screens every shell command an agent runs on the developer's machine
/// before it executes: no credential reads, no pushes or new remotes, no other hosts, no cloud
/// or cluster control planes, no forge API, nothing outside the run's own worktree in the Orkka
/// home. The project's own toolchain is untouched. Every refusal is audited, and repeated ones
/// escalate the audit signal. See SECURITY.md, "Command guard".
/// </summary>
/// <param name="worktreePath">The run's worktree — see <see cref="IsBlockedCommand"/>.</param>
public sealed class AgentBashGuard(
    string agentName,
    string agentRole,
    string? worktreePath,
    ILogger logger,
    Func<string, Task>? onBlocked = null)
{
    private int _blockedAttempts;

    /// <summary>
    /// Programs an agent must not invoke on the developer's machine, matched against the
    /// EXECUTABLE of each command segment — never as a substring of the whole line.
    ///
    /// The agent runs as the developer, next to everything the developer is signed in to. What is
    /// listed here is what reaches beyond the repository with those sign-ins: other hosts, the
    /// company's cloud accounts and clusters, the forge's API, and the machine itself. The
    /// project's own toolchain — compilers, package managers, test runners, a local database
    /// client, docker for the project's own services — is deliberately NOT here: blocking a
    /// developer's stack protects nobody and only stops the work.
    /// </summary>
    private static readonly HashSet<string> BlockedPrograms = new(StringComparer.Ordinal)
    {
        // Network reconnaissance of the company network the laptop sits on.
        "nc", "ncat", "netcat", "telnet", "nmap", "masscan", "arp-scan",

        // Remote shells and transfers: the developer's SSH keys open the company's servers.
        "ssh", "scp", "sftp", "ftp", "rsync", "plink", "pscp",

        // Forge CLIs. Their API carries the developer's token: `gh api`, `gh repo create`,
        // `gh gist create` would publish or exfiltrate with it. Orkka's runner opens the pull
        // request itself; the agent never needs them.
        "gh", "glab", "hub",

        // Cloud and infrastructure control planes, signed in as the developer. Nothing the
        // agent writes code for needs to act on the company's live accounts or clusters.
        "kubectl", "helm", "terraform", "tofu", "pulumi", "aws", "az", "azd", "gcloud", "gsutil",
        "doctl", "flyctl", "fly", "vercel", "netlify", "heroku", "oc", "eksctl",

        // OS credential stores.
        "cmdkey", "vaultcmd", "secret-tool", "keyring",

        // Host and service control, accounts, disks.
        "systemctl", "service", "launchctl", "crontab", "at", "schtasks", "shutdown", "reboot",
        "halt", "mount", "umount", "mkfs", "fdisk", "diskpart", "format", "dd", "sysctl", "modprobe",
        "insmod", "iptables", "netsh", "useradd", "usermod", "userdel", "groupadd", "passwd",
        "visudo", "reg", "regedit", "bcdedit",
    };

    /// <summary>
    /// Destructive shapes that are refused whatever else the line says.
    ///
    /// Separate from <see cref="BlockedPrograms"/> because the program alone is innocent and
    /// routine — <c>rm -rf node_modules</c> and <c>rm -rf bin obj</c> are ordinary build hygiene
    /// that an agent must be able to do. What is never ordinary is deleting outside the worktree,
    /// which is what these patterns catch.
    /// </summary>
    private static readonly string[] BlockedFragmentsDestructive =
    [
        // Fork bomb.
        ":(){ :|:& };:", ":(){:|:&};:",
        // Recursive chmod/chown of a root-ish path.
        "chmod -r 777 /", "chmod 777 /", "chown -r root /",
        // Overwriting a device or a disk.
        "> /dev/sd", "of=/dev/",
        // A container that is the host: docker is the project's own tool, but one of these
        // flags hands the container the whole machine and everything the guard protects.
        "--privileged", "--pid=host", "--pid host", "-v /:", "--volume /:", "--volume=/:",
        "type=bind,source=/,", "docker.sock", "-v c:/:", "-v c:\\:",
    ];

    /// <summary>
    /// Multi-word rules where the program alone is innocent and the subcommand is the problem.
    /// Matched as whole-token sequences, so `npm install pg-boss` is not `npm install pg`.
    /// </summary>
    private static readonly string[] BlockedPhrases =
    [
        // Printing a credential: turns a prompt injection in a ticket into a stolen token.
        "git credential-manager get", "git-credential-manager get", "git credential-manager-core get",
        "npm token", "pnpm token",

        // Publishing with the developer's registry credentials — a package or an image goes out
        // under their name, irreversibly. Never part of a task; releases are the team's call.
        "npm publish", "pnpm publish", "yarn publish", "yarn npm publish", "bun publish",
        "cargo publish", "gem push", "dotnet nuget push", "nuget push", "twine upload",
        "poetry publish", "uv publish", "docker push", "podman push", "mvn deploy",
        "gradle publish", "./gradlew publish",

        // macOS keychain reads.
        "security find-generic-password", "security find-internet-password",
        "security dump-keychain", "security export", "security unlock-keychain",

        // PowerShell credential readers.
        "get-storedcredential", "convertfrom-securestring", "[system.net.networkcredential]",
    ];

    /// <summary>
    /// git subcommands an agent never runs: the platform pushes the task branch itself, and a
    /// push — or a remote the agent added — is the one way code leaves the machine for somewhere
    /// the developer did not choose. Checked as program + verb, so <c>git -C dir push</c> is
    /// caught and <c>npm run push-check</c> is not.
    /// </summary>
    private static bool IsForbiddenGit(string segment)
    {
        var (program, verb) = ShellCommandParser.ProgramAndSubcommand(segment);
        if (program != "git")
            return false;

        var words = ShellCommandParser.ArgumentWords(segment);
        if (verb is "push" or "credential" or "send-email" or "request-pull"
            || (verb is null && words.Contains("push")))
            return true;

        // `git remote add x …`, `git remote set-url origin …`: re-pointing where the platform
        // pushes. Reading remotes (`git remote -v`) is fine.
        return verb == "remote" && words.Any(w => w is "add" or "set-url" or "rename");
    }

    /// <summary>
    /// The guard's rules as command PREFIXES, for agent CLIs that take a deny-list instead of a
    /// hook (Codex execpolicy rules, Copilot's <c>--deny-tool shell(...)</c>). A prefix list can
    /// express "this program" and "this program + verb"; the path-based rules (credential files,
    /// the Orkka home) cannot be said this way and are covered for those CLIs by their own
    /// workspace sandbox — see SECURITY.md, "Guards per provider".
    /// </summary>
    public static IReadOnlyList<string[]> CommandPrefixRules { get; } = BuildPrefixRules();

    private static IReadOnlyList<string[]> BuildPrefixRules()
    {
        var rules = new List<string[]>();
        rules.AddRange(BlockedPrograms.OrderBy(p => p, StringComparer.Ordinal).Select(p => new[] { p }));
        rules.AddRange(BlockedPhrases
            .Where(p => !p.StartsWith('[') && !p.StartsWith("./", StringComparison.Ordinal))
            .Select(p => p.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
        rules.Add(["git", "push"]);
        rules.Add(["git", "credential"]);
        rules.Add(["git", "send-email"]);
        rules.Add(["git", "remote", "add"]);
        rules.Add(["git", "remote", "set-url"]);
        rules.Add(["git", "remote", "rename"]);

        // Environment listings a prefix can say (EnvironmentReadGuard has the full rule for the
        // hook; Codex and Copilot additionally keep the sign-in variables out of their shells).
        // `printenv NODE_ENV` stays fine under the hook (only a dump or a sign-in name is
        // refused there); a prefix cannot tell them apart, so Codex and Copilot refuse it whole.
        rules.Add(["printenv"]);
        rules.Add(["export", "-p"]);
        rules.Add(["declare", "-x"]);
        rules.Add(["declare", "-p"]);
        rules.Add(["typeset", "-x"]);
        rules.Add(["typeset", "-p"]);
        rules.Add(["cat", "/proc/self/environ"]);
        rules.Add(["strings", "/proc/self/environ"]);
        foreach (var lister in new[] { "Get-ChildItem", "get-childitem", "gci", "ls", "dir" })
        {
            rules.Add([lister, "env:"]);
            rules.Add([lister, "Env:"]);
        }
        return rules;
    }

    /// <summary>Search programs whose recursive sweeps this guard characterises.</summary>
    private static readonly HashSet<string> SearchPrograms =
        new(StringComparer.Ordinal) { "grep", "rg", "ag", "ack", "find" };

    /// <summary>Secret-ish needles that make a filesystem sweep a credential hunt.</summary>
    private static readonly string[] CredentialNeedles =
        ["password", "secret", "token", "api_key", "apikey", "database_url", "credential"];

    /// <summary>
    /// A search for secret-ish content whose target leaves the worktree (absolute path, home,
    /// parent traversal). Inside the worktree the same search is ordinary programming — the
    /// worktree holds no credentials — but `grep -r password /` walks the host, including
    /// mounted service-account tokens.
    /// </summary>
    private static bool IsCredentialSweepOutsideWorktree(string segment)
    {
        var (program, _) = ShellCommandParser.ProgramAndSubcommand(segment);
        if (!SearchPrograms.Contains(program))
            return false;

        if (!CredentialNeedles.Any(segment.Contains))
            return false;

        foreach (var raw in segment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var token = raw.Trim('"', '\'', '`');
            if (token.Length == 0 || token.StartsWith('-'))
                continue;

            if (token.StartsWith('/') || token.StartsWith('~') || token.Contains(".."))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Does this line try to set who authored a commit?
    ///
    /// Three shapes, and the third is the one that matters. `git config user.email x` and
    /// `git -c user.email=x commit` are both overridden by the run's GIT_AUTHOR_*/GIT_COMMITTER_*
    /// environment, so they are futile rather than dangerous. `git commit --author="X <x@y>"`
    /// is not overridden by anything — it is the single way an agent can still put another
    /// name on a commit.
    ///
    /// Deliberately not scoped to a leading `git`: the identity flags are meaningless to any
    /// other program, and requiring the line to START with git would miss `cd sub && git ...`
    /// and every pipeline form.
    /// </summary>
    internal static bool MutatesGitIdentity(string command)
    {
        var lower = command.ToLowerInvariant();

        // --author only relabels a commit on a command that WRITES one. `git log --author=nova`
        // is an ordinary history search, and blocking it spends a strike on an agent that was
        // reading. The verb is matched as a bare token so a path like `src/commit.ts` does not
        // turn a read into an offence.
        if (lower.Contains("--author"))
        {
            // Whitespace and the shell separators that end one command and start the
            // next, so `... && git commit --author=...` is read as two commands.
            var tokens = lower.Split(
                [' ', (char)9, (char)10, (char)13, ';', '|', '&', '(', ')'],
                StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Any(t => t is "commit" or "am" or "cherry-pick" or "revert" or "rebase"))
                return true;
        }

        // user.name / user.email, however the value is attached: `config user.email x`,
        // `-c user.email=x`, `config --global user.email x`. A read of the same key
        // (`config --get user.email`) changes nothing, so it is left alone.
        if (lower.Contains("--get") || lower.Contains("--list"))
            return false;

        return (lower.Contains("user.email") || lower.Contains("user.name"))
            && (lower.Contains("git config") || lower.Contains("git -c") || lower.Contains("git  -c"));
    }

    /// <summary>
    /// The blocklist decision on its own, without the agent bookkeeping (attempt counting,
    /// notifications). Shared with <see cref="InteractiveCommandPolicy"/>, so a batch run and an
    /// interactive session can never disagree about what "restricted" means.
    /// </summary>
    /// <param name="worktreePath">The run's worktree. It lives under the Orkka home, which is
    /// otherwise off limits, so naming it is how an absolute <c>cd</c> into it stays allowed.</param>
    public static bool IsBlockedCommand(string command, string? worktreePath)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        var lower = command.ToLowerInvariant().Trim();

        if (ShellCommandParser.ReferencesEnvFile(lower))
            return true;

        if (DeveloperSecrets.ShellLineTouchesSecrets(lower, worktreePath))
            return true;

        if (BlockedFragmentsDestructive.Any(lower.Contains))
            return true;

        // Dumping the environment or expanding an AI sign-in variable by name.
        if (EnvironmentReadGuard.ReadsEnvironment(lower))
            return true;

        if (BlockedPhrases.Any(p => ShellCommandParser.ContainsPhrase(lower, p)))
            return true;

        var segments = ShellCommandParser.SplitSegments(lower);
        if (segments.Any(IsForbiddenGit))
            return true;

        if (segments.Any(DeletesOutsideTheWorktree))
            return true;

        if (segments.Any(IsCredentialSweepOutsideWorktree))
            return true;

        return ShellCommandParser.ExtractPrograms(lower).Any(BlockedPrograms.Contains);
    }

    /// <summary>
    /// True when a segment deletes something it has no business deleting: an absolute path, the
    /// home directory, a parent of the worktree, a bare wildcard, or a path built from an
    /// unexpanded variable.
    ///
    /// Deliberately NOT "any recursive delete". <c>rm -rf node_modules</c>, <c>rm -rf bin obj</c>
    /// and <c>rm -rf dist</c> are what a build does; refusing them would break ordinary work in
    /// every language and teach agents that cleaning is impossible. What matters is the target,
    /// not the flags — <c>rm /etc/passwd</c> is no better for lacking <c>-rf</c>.
    /// </summary>
    private static bool DeletesOutsideTheWorktree(string segment)
    {
        var (program, _) = ShellCommandParser.ProgramAndSubcommand(segment);
        if (program is not ("rm" or "rmdir" or "del" or "rd" or "erase"))
            return false;

        foreach (var raw in segment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var token = raw.Trim('"', '\'', '`');
            if (token.Length == 0 || token.StartsWith('-'))
                continue;

            // Unix absolute, Windows absolute (c:\ or c:/), UNC.
            if (token.StartsWith('/') || token.StartsWith("\\\\", StringComparison.Ordinal))
                return true;
            if (token.Length >= 3 && char.IsLetter(token[0]) && token[1] == ':'
                && (token[2] == '\\' || token[2] == '/'))
                return true;

            // Home, parent traversal, and anything whose target is a variable we cannot read.
            if (token.StartsWith('~') || token.Contains("..") || token.Contains('$') || token.Contains('%'))
                return true;

            // Bare wildcard or the current directory itself — "delete everything here".
            if (token is "*" or "." or "./" or "./*" or ".\\*")
                return true;
        }

        return false;
    }

    public async Task<HookOutput> EvaluateCommand(HookInput input, string? toolUseId, HookContext? context)
    {
        // Background commands are refused outright. The CLI delivers their completion as a
        // queued user message, and in a headless SDK turn that queued message makes the CLI
        // answer every later tool permission "cancelled" until the turn ends — FIN-1 lost
        // Read/Write/Bash for the rest of its run the moment a background `npm install`
        // finished. Foreground is never worse here: the agent has nothing else to do while
        // it waits, and long installs are bounded by the run's wall clock, not by the tool.
        if (IsBackgroundRun(input))
        {
            logger.LogInformation("Agent {AgentName}: refused a run_in_background command — foreground only", agentName);
            return Deny(
                "Background commands (run_in_background) are not available in this environment — "
                + "run the same command in the foreground and wait for it; for a server that must "
                + "keep running, start it as `nohup <command> > app.log 2>&1 &` in an ordinary Bash "
                + "call and poll its log/port. Your other tools are "
                + "unaffected.");
        }

        var command = ExtractCommand(input);
        if (string.IsNullOrWhiteSpace(command))
            return Allow();

        var lower = command.ToLowerInvariant().Trim();

        // Rewriting who authored the work. Refused rather than ignored: the run's
        // GIT_AUTHOR_*/GIT_COMMITTER_* already outrank every config file, so the agent's edit
        // would change nothing and it would carry on believing it had — and `--author`, which
        // those variables do NOT override, is the one shape that really can relabel a commit.
        // The author is the developer's workspace identity; a relabelled commit would put
        // someone else's name on work the developer is accountable for.
        if (MutatesGitIdentity(lower))
        {
            _blockedAttempts++;
            logger.LogWarning(
                "SECURITY: Agent {AgentName} ({Role}) tried to change the commit identity: {Command} (attempt {Count})",
                agentName, agentRole, command, _blockedAttempts);
            await NotifyBlockedAsync(command);

            return Deny(
                "The commit identity is the developer's and cannot be changed from a task. "
                + "Commit normally — `git commit -m \"...\"` — and the author is filled in for you. "
                + "Never pass --author or set user.name/user.email.");
        }

        // One decision function, shared with the interactive policy, so the two can never
        // disagree about what "restricted" means.
        if (IsBlockedCommand(lower, worktreePath))
        {
            _blockedAttempts++;

            logger.LogWarning(
                "SECURITY: Agent {AgentName} ({Role}) blocked command: {Command} (attempt {Count})",
                agentName, agentRole, command, _blockedAttempts);
            await NotifyBlockedAsync(command);

            // Repeated blocked attempts: escalate the audit signal. The wording matters — this
            // hook can only refuse the command in front of it, so telling the agent it has been
            // "terminated" is both false and actively harmful: it concludes every tool is dead,
            // stops working, and parks the task on a question no human can answer. That is
            // exactly what happened to a Go agent whose `go env` tripped the old substring
            // blocklist three times.
            if (_blockedAttempts >= 3)
            {
                logger.LogCritical(
                    "SECURITY: Agent {AgentName} — {Count} blocked command attempts. " +
                    "Last command: {Command}",
                    agentName, _blockedAttempts, command);

                return Deny(
                    "This command is restricted, and repeated attempts are being audited. "
                    + "Your other tools are unaffected — keep working inside the repository, "
                    + "and stop retrying credential reads, pushes, remote hosts and cloud CLIs.");
            }

            return Deny(BlockedReason);
        }

        // Log all commands for audit
        logger.LogDebug("Agent {AgentName} bash: {Command}", agentName, command);

        return Allow();
    }

    /// <summary>What an agent is told when a command is refused — shared with the interactive guard.</summary>
    internal const string BlockedReason =
        "This command is restricted on the developer's machine: it reads credentials (including "
        + "printing the environment or a sign-in variable), pushes or "
        + "adds a git remote, reaches another host, drives a cloud, cluster or forge CLI, or reaches "
        + "outside this task's repository in the Orkka home. Orkka pushes the branch and opens the "
        + "pull request itself. The project's own toolchain — build, test, lint, package managers, "
        + "local services — is fully available; use it freely.";

    internal static bool IsBackgroundRun(HookInput input) =>
        input is PreToolUseHookInput { ToolInput.ValueKind: JsonValueKind.Object } pre
        && pre.ToolInput.TryGetProperty("run_in_background", out var bg)
        && bg.ValueKind == JsonValueKind.True;

    private static string ExtractCommand(HookInput input)
    {
        if (input is PreToolUseHookInput preToolUse)
        {
            if (preToolUse.ToolInput.ValueKind == JsonValueKind.Object &&
                preToolUse.ToolInput.TryGetProperty("command", out var commandProp))
            {
                return commandProp.GetString() ?? "";
            }
        }

        return "";
    }

    private async Task NotifyBlockedAsync(string command)
    {
        if (onBlocked is null)
            return;

        try
        {
            await onBlocked(command);
        }
        catch
        {
            // Auditing must never interfere with the guard decision.
        }
    }

    // Every field of the response has to be populated — see AgentHookResponses for why an
    // unset (null) field makes the CLI discard the decision and run the command anyway.
    private static HookOutput Allow() => AgentHookResponses.Allow();

    private static HookOutput Deny(string reason) => AgentHookResponses.Deny(reason);
}
