namespace Orkka.Modules.Execution.Agents;

/// <summary>What an interactive session should do with a Bash command before it runs.</summary>
public enum CommandDisposition
{
    /// <summary>Refused outright by the command guard (SECURITY.md). Never offered to the human.</summary>
    Blocked,

    /// <summary>Ordinary development work — runs without interrupting anyone.</summary>
    Allowed,

    /// <summary>Destroys work the session has not landed. Ask the human.</summary>
    NeedsApproval,
}

/// <summary>
/// Classifies a Bash command for an interactive session, where a human is present for every turn
/// and can therefore be asked — the third outcome the batch pipeline has no use for.
///
/// **Risk lives in the blocklist, not in prompts.** Anything that reads a credential, pushes,
/// publishes, reaches another host or a cloud account is refused outright by
/// <see cref="AgentBashGuard.IsBlockedCommand"/> and never offered to anyone — a human being
/// present does not make a push from the agent's shell acceptable; Orkka pushes the branch
/// itself. Everything else a developer's toolchain does — building, testing, installing
/// packages, running scripts, cleaning build output — simply runs.
///
/// So this asks about one narrow band: commands that destroy work the session has not landed
/// yet — a hard reset, a clean, a history rewrite. That work is the human's as much as the
/// agent's, and losing it is exactly what they will not expect.
///
/// The earlier design asked about anything it did not recognise. That reads as safer and is not:
/// a Python developer whose every command stops for approval learns to click through without
/// reading, which costs more than it buys on the commands that actually matter.
/// </summary>
public static class InteractiveCommandPolicy
{
    /// <summary>
    /// Token sequences that ask: each throws away work the session has not landed.
    ///
    /// Matched as whole-token sequences (see <see cref="ShellCommandParser.ContainsPhrase"/>), so
    /// <c>git clean</c> does not also match a script named <c>git-clean-check</c>.
    ///
    /// Note what is deliberately absent: installs. <c>pip install</c>, <c>npm install</c> and
    /// <c>go get</c> are how work gets done in every language, they only mutate a disposable
    /// worktree, and prompting for them is the fastest way to make prompts meaningless.
    /// </summary>
    private static readonly string[] AskPhrases =
    [
        "git reset --hard", "git clean", "git filter-branch", "git filter-repo",
        "git checkout -- .", "git restore .", "git stash drop", "git stash clear",
    ];

    /// <summary>
    /// Verbs that ask wherever they appear in their program's arguments, not only immediately
    /// after it. <c>git -C /srv/app push</c> is a push, and phrase matching misses it because the
    /// flag separates the two words.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> AskVerbs = new(StringComparer.Ordinal)
    {
        ["git"] = new(StringComparer.Ordinal) { "clean", "filter-branch", "filter-repo" },
    };

    /// <summary>
    /// Classifies <paramref name="command"/>. Order is a security property: the blocklist is
    /// consulted first and its answer is final, so an interactive session can never become the
    /// route by which a push, a credential read, or a delete outside the worktree gets
    /// authorised. A human being present adds an outcome for *ordinary* commands; it does not
    /// soften the blocklist.
    /// </summary>
    /// <param name="worktreePath">The run's worktree — see <see cref="AgentBashGuard.IsBlockedCommand"/>.</param>
    public static CommandDisposition Classify(string command, string? worktreePath)
    {
        if (string.IsNullOrWhiteSpace(command))
            return CommandDisposition.Allowed;

        var lower = command.ToLowerInvariant().Trim();

        if (AgentBashGuard.IsBlockedCommand(lower, worktreePath))
            return CommandDisposition.Blocked;

        // Any segment, so a chain cannot launder a push behind a safe prefix.
        return ShellCommandParser.SplitSegments(lower).Any(NeedsApproval)
            ? CommandDisposition.NeedsApproval
            : CommandDisposition.Allowed;
    }

    private static bool NeedsApproval(string segment)
    {
        if (AskPhrases.Any(phrase => ShellCommandParser.ContainsPhrase(segment, phrase)))
            return true;

        var program = ShellCommandParser.ProgramAndSubcommand(segment).Program;
        if (program.Length == 0)
            return false;

        return AskVerbs.TryGetValue(program, out var verbs)
            && ShellCommandParser.ArgumentWords(segment).Any(verbs.Contains);
    }

    /// <summary>
    /// Programs whose next token is a verb rather than an argument. For these, a grant must name
    /// the verb — "always allow git" would hand over <c>git push</c> and <c>git reset --hard</c>
    /// along with <c>git status</c>. For everything else the program IS the grant: <c>rm</c>
    /// takes filenames, and keying on one would make the permission look narrow while covering
    /// whatever arguments came next.
    /// </summary>
    private static readonly HashSet<string> SubcommandPrograms = new(StringComparer.Ordinal)
    {
        "git", "npm", "pnpm", "yarn", "bun", "npx", "dotnet", "cargo", "go", "pip", "pip3",
        "docker", "docker-compose", "podman", "kubectl", "helm", "terraform", "aws", "az",
        "gcloud", "gh", "glab", "hub", "apt", "apt-get", "brew", "gem", "systemctl", "service",
    };

    /// <summary>
    /// Every key that must already be granted for this command to run without asking — one per
    /// segment that is not ordinary work. Null means the command can never be covered by a grant.
    ///
    /// Per segment, and that is the whole point. A key can only honestly describe one program, so
    /// checking a single key against a whole command line lets the rest of the line in free:
    /// a grant for <c>git push</c> would have admitted <c>git push &amp;&amp; rm -rf /</c>,
    /// because the line still keys as "git push". Requiring every ask-worthy segment to be
    /// covered closes that, and still lets the common <c>cd app &amp;&amp; npm install</c> ride a
    /// grant for <c>npm install</c>, since <c>cd</c> needs no permission of its own.
    /// </summary>
    public static IReadOnlyList<string>? RequiredApprovalKeys(string command, string? worktreePath)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var keys = new List<string>();

        foreach (var segment in ShellCommandParser.SplitSegments(command.ToLowerInvariant()))
        {
            var disposition = ClassifySegment(segment, worktreePath);

            if (disposition == CommandDisposition.Allowed)
                continue;

            // A refused segment is not something any grant can describe. The guard denies before
            // it ever consults grants, so this is belt and braces — but a helper that answers
            // "yes, covered" about a blocked command is wrong on its own terms, and the next
            // caller may not have the guard's ordering.
            if (disposition == CommandDisposition.Blocked)
                return null;

            var key = SegmentKey(segment);
            if (key.Length == 0)
            {
                // A segment we cannot characterise (no resolvable program, or a subcommand-style
                // program used in a shape we cannot read) must not be waved through by any grant.
                return null;
            }

            keys.Add(key);
        }

        return keys.Count == 0 ? null : keys;
    }

    /// <summary>
    /// The single key a session-wide "always allow" can honestly cover, or empty when the command
    /// has no ask-worthy segment or several. With several there is no one label that describes
    /// what the user would be granting, so the UI offers only Allow once and Deny.
    /// </summary>
    public static string ApprovalKey(string command, string? worktreePath)
    {
        var keys = RequiredApprovalKeys(command, worktreePath);
        return keys is { Count: 1 } ? keys[0] : "";
    }

    /// <summary>
    /// True when every ask-worthy segment of <paramref name="command"/> is already granted for
    /// this session. This is where the grant is enforced — not at the point it was given, since
    /// grants are stored from one command and matched against every command that follows.
    /// </summary>
    public static bool IsCoveredBySessionGrants(string command, string? worktreePath, Func<string, bool> isGranted)
    {
        var keys = RequiredApprovalKeys(command, worktreePath);
        return keys is not null && keys.Count > 0 && keys.All(isGranted);
    }

    /// <summary>One segment's verdict, for grant bookkeeping.</summary>
    private static CommandDisposition ClassifySegment(string segment, string? worktreePath)
    {
        if (AgentBashGuard.IsBlockedCommand(segment, worktreePath))
            return CommandDisposition.Blocked;

        return NeedsApproval(segment)
            ? CommandDisposition.NeedsApproval
            : CommandDisposition.Allowed;
    }

    /// <summary>The grant key for one segment, or empty when it cannot be characterised.</summary>
    private static string SegmentKey(string segment)
    {
        var program = ShellCommandParser.ProgramAndSubcommand(segment).Program;
        if (program.Length == 0)
            return "";

        if (!SubcommandPrograms.Contains(program))
            return program;

        // The verb, found past any flags — so `git -C /srv/app push` keys as "git push" like the
        // plain form rather than as an unkeyable oddity. With no verb at all there is nothing to
        // name: refusing to key it costs a repeat prompt, while falling back to the bare program
        // would turn a grant for one subcommand into a grant for all of them.
        var words = ShellCommandParser.ArgumentWords(segment);
        return words.Count == 0 ? "" : $"{program} {words[0]}";
    }
}
