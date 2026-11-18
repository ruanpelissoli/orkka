namespace Orkka.Modules.Execution.Agents;

/// <summary>
/// Splits a shell command line into the programs it actually invokes.
///
/// Exists because the guard used to ask "does this string contain 'env'?", which is true of
/// <c>go env</c>, <c>python -m venv</c>, and any path under <c>.venv/</c> — none of which read a
/// secret. Blocking a language's own toolchain is not a security win; it is an agent that cannot
/// build the project and a human who gets asked why.
///
/// This is deliberately NOT a real shell parser. It does not need to be: the guard's job is to
/// catch an agent reaching for <c>psql</c>, not to survive an adversary who is already executing
/// arbitrary code. Layers 1, 2, 4 and 5 are what stand up to that — this one keeps an honest
/// agent out of the wrong tools and leaves an audit trail.
/// </summary>
internal static class ShellCommandParser
{
    // Segment separators. A blocked program anywhere in a chain counts: `go build && psql` is a
    // psql invocation no matter what precedes it.
    private static readonly string[] Separators = ["&&", "||", ";", "|", "\n", "\r"];

    /// <summary>
    /// The program each segment invokes, lowercased, with directories stripped.
    /// <c>/usr/bin/psql</c> and <c>psql</c> are the same tool; <c>sudo psql</c> is too.
    /// </summary>
    public static IReadOnlyList<string> ExtractPrograms(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return [];

        var programs = new List<string>();

        foreach (var segment in command.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var program = ExtractProgram(segment);
            if (program.Length > 0)
                programs.Add(program);
        }

        return programs;
    }

    /// <summary>
    /// The command line's segments, in order — the pieces separated by <c>&amp;&amp;</c>,
    /// <c>||</c>, <c>;</c> and <c>|</c>.
    ///
    /// Exists because a verdict about a whole command line cannot be attributed to one program:
    /// <c>ls x &amp;&amp; rm x</c> is a listing AND a deletion, and anything that summarises it as
    /// a single thing is wrong about one of them.
    /// </summary>
    public static IReadOnlyList<string> SplitSegments(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return [];

        return command
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    /// <summary>
    /// A segment's program and, where it has one, its subcommand — <c>("git", "push")</c>,
    /// <c>("rm", null)</c>.
    ///
    /// The subcommand is null when the next meaningful token looks like a path or a filename
    /// rather than a verb, so <c>rm obsolete.txt</c> does not read as a subcommand of <c>rm</c>.
    /// Callers that need a verb to make a decision must treat null as "cannot characterise this"
    /// rather than falling back to the bare program — for <c>git</c> that fallback would turn a
    /// grant for one subcommand into a grant for all of them.
    /// </summary>
    public static (string Program, string? Subcommand) ProgramAndSubcommand(string segment)
    {
        var tokens = segment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return ("", null);

        var index = ProgramTokenIndex(tokens);
        if (index >= tokens.Length)
            return (Normalize(tokens[^1]), null);

        var program = Normalize(tokens[index]);

        for (var i = index + 1; i < tokens.Length; i++)
        {
            var token = tokens[i].Trim('"', '\'', '`');
            if (token.Length == 0 || token.StartsWith('-'))
                continue;

            // A path or a filename is an argument, not a verb.
            if (token.Contains('/') || token.Contains('\\') || token.Contains('.'))
                return (program, null);

            return (program, token.ToLowerInvariant());
        }

        return (program, null);
    }

    /// <summary>
    /// The bare word arguments of a segment — tokens after the program that are not flags and do
    /// not look like paths or filenames. <c>git -C /srv/app push</c> yields <c>["push"]</c>.
    ///
    /// Exists because whole-token phrase matching needs the words adjacent, and a flag between
    /// them defeats it: <c>git -C /srv/app push</c> is a push that "git push" does not match.
    /// Looking for the verb anywhere in the segment's words catches it without loosening into
    /// substring matching, so <c>npm run push-check</c> still reads as one word and not a push.
    /// </summary>
    public static IReadOnlyList<string> ArgumentWords(string segment)
    {
        var tokens = segment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return [];

        var words = new List<string>();
        for (var i = ProgramTokenIndex(tokens) + 1; i < tokens.Length; i++)
        {
            var token = tokens[i].Trim('"', '\'', '`');
            if (token.Length == 0 || token.StartsWith('-'))
                continue;
            if (token.Contains('/') || token.Contains('\\') || token.Contains('.'))
                continue;

            words.Add(token.ToLowerInvariant());
        }

        return words;
    }

    /// <summary>Index of the token naming the program, walking past leading assignments and
    /// runner prefixes. Shared so <see cref="ExtractProgram"/> and
    /// <see cref="ProgramAndSubcommand"/> can never disagree about where a command starts.</summary>
    private static int ProgramTokenIndex(string[] tokens)
    {
        var index = 0;
        while (index < tokens.Length)
        {
            var token = tokens[index];

            if (token.Contains('=') && !token.StartsWith('-')
                && token.IndexOf('=') > 0
                && IsAssignmentName(token[..token.IndexOf('=')]))
            {
                index++;
                continue;
            }

            if (token is "sudo" or "command" or "exec" or "nohup" or "time" or "env"
                && index + 1 < tokens.Length
                && !tokens[index + 1].StartsWith('-'))
            {
                index++;
                continue;
            }

            return index;
        }

        return index;
    }

    private static string ExtractProgram(string segment)
    {
        var tokens = segment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var index = 0;

        while (index < tokens.Length)
        {
            var token = tokens[index];

            // `FOO=bar cmd` — a leading assignment is setup for the command that follows, so
            // keep walking. This also means `PGPASSWORD=x psql` resolves to psql, which is the
            // whole point of looking past it.
            if (token.Contains('=') && !token.StartsWith('-')
                && token.IndexOf('=') > 0
                && IsAssignmentName(token[..token.IndexOf('=')]))
            {
                index++;
                continue;
            }

            // Prefix runners: the interesting program is the next token.
            if (token is "sudo" or "command" or "exec" or "nohup" or "time" or "env"
                && index + 1 < tokens.Length
                && !tokens[index + 1].StartsWith('-'))
            {
                // `env` as a prefix (`env FOO=bar cmd`) is a runner, but bare `env` — with no
                // following program — is the environment dump the guard cares about. The check
                // above only skips it when something follows, so the bare form still surfaces.
                index++;
                continue;
            }

            return Normalize(token);
        }

        // Every token was an assignment or a runner prefix: `env` alone, `FOO=bar` alone.
        return tokens.Length > 0 ? Normalize(tokens[^1]) : "";
    }

    private static bool IsAssignmentName(string name) =>
        name.Length > 0 && name.All(c => char.IsLetterOrDigit(c) || c == '_');

    /// <summary>Strips quotes and any directory prefix, and lowercases.</summary>
    private static string Normalize(string token)
    {
        var trimmed = token.Trim('"', '\'', '`', '(', ')');

        var slash = trimmed.LastIndexOfAny(['/', '\\']);
        if (slash >= 0 && slash < trimmed.Length - 1)
            trimmed = trimmed[(slash + 1)..];

        // `psql.exe` on Windows.
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^4];

        return trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// True when any token names a dotenv file — <c>.env</c>, <c>../.env</c>, <c>.env.local</c>,
    /// <c>/srv/app/.env</c>.
    ///
    /// Its own rule rather than a list of `cat .env` / `less .env` phrases, because the command
    /// that matters is whichever one the agent thinks of: `cp ../.env .` moves a credential file
    /// just as effectively as reading it, and enumerating verbs would always be one verb behind.
    ///
    /// Token-scoped so it cannot repeat the original bug: <c>go env</c>, <c>python -m venv</c>
    /// and <c>.venv/bin/activate</c> name no dotenv file and pass.
    /// </summary>
    private static readonly string[] EnvTemplateSuffixes =
        [".example", ".sample", ".template", ".dist", ".defaults"];

    public static bool ReferencesEnvFile(string command)
    {
        // Per segment, so the bootstrap exemption below cannot be smuggled past with a chain:
        // `cp .env.example .env && cat .env` is a bootstrap AND a read, and the read blocks.
        foreach (var segment in SplitSegments(command))
        {
            if (!SegmentNamesRealEnvFile(segment))
                continue;

            // The canonical framework bootstrap — `cp .env.example .env` — creates a fresh
            // dotenv from committed placeholders. The DESTINATION token is a real .env, but
            // nothing here reads or moves a credential: the only content involved is the
            // template's. Blocking it made scaffolding any project impossible in the cloud
            // (three attempts then tripped the escalation).
            if (IsEnvTemplateBootstrap(segment))
                continue;

            return true;
        }

        return false;
    }

    private static bool SegmentNamesRealEnvFile(string segment)
    {
        foreach (var raw in segment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim('"', '\'', '`', '(', ')', ';', ',').ToLowerInvariant();
            if (token.Length == 0)
                continue;

            if (IsRealEnvFileName(FileNameOf(token)))
                return true;
        }

        return false;
    }

    /// <summary>".env", ".env.local", ".env.production" — but not ".environment", ".venv",
    /// or a committed template (".env.example" and friends).</summary>
    public static bool IsRealEnvFileName(string name)
    {
        if (name != ".env" && !name.StartsWith(".env.", StringComparison.Ordinal))
            return false;

        // Templates are the exception, and an important one: `.env.example` holds
        // placeholder values, is committed to the repo on purpose, and is what an agent
        // reads to learn which variables a project needs. Blocking `git add
        // .env.example` protects nothing and stops ordinary work.
        return !EnvTemplateSuffixes.Any(s => name.EndsWith(s, StringComparison.Ordinal));
    }

    private static bool IsEnvTemplateName(string name) =>
        (name == ".env" || name.StartsWith(".env.", StringComparison.Ordinal))
        && EnvTemplateSuffixes.Any(s => name.EndsWith(s, StringComparison.Ordinal));

    /// <summary>A copy/rename whose SOURCE is a dotenv template and whose only real-.env
    /// token is the destination: <c>cp .env.example .env</c>, <c>mv .env.sample .env.local</c>.</summary>
    private static bool IsEnvTemplateBootstrap(string segment)
    {
        var tokens = segment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return false;

        var programIndex = ProgramTokenIndex(tokens);
        if (programIndex >= tokens.Length
            || Normalize(tokens[programIndex]) is not ("cp" or "copy" or "mv" or "move"))
            return false;

        var files = tokens
            .Skip(programIndex + 1)
            .Select(t => t.Trim('"', '\'', '`', '(', ')', ';', ',').ToLowerInvariant())
            .Where(t => t.Length > 0 && !t.StartsWith('-'))
            .ToList();

        if (files.Count != 2)
            return false;

        return IsEnvTemplateName(FileNameOf(files[0]))
            && IsRealEnvFileName(FileNameOf(files[1]));
    }

    private static string FileNameOf(string token)
    {
        var slash = token.LastIndexOfAny(['/', '\\']);
        return slash >= 0 ? token[(slash + 1)..] : token;
    }

    /// <summary>
    /// True when <paramref name="phrase"/> appears in the command as a whole-token sequence.
    ///
    /// Used for the multi-word rules (<c>prisma migrate</c>, <c>ip route</c>, <c>npm install
    /// pg</c>) where the program alone is innocent and the subcommand is what matters. Token
    /// matching is what keeps <c>npm install pg-boss</c> from reading as <c>npm install pg</c>.
    /// </summary>
    public static bool ContainsPhrase(string command, string phrase)
    {
        var words = phrase.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return false;

        var tokens = command
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim('"', '\'', '`').ToLowerInvariant())
            .ToArray();

        for (var start = 0; start + words.Length <= tokens.Length; start++)
        {
            var matched = true;
            for (var offset = 0; offset < words.Length; offset++)
            {
                if (!string.Equals(tokens[start + offset], words[offset], StringComparison.Ordinal))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
                return true;
        }

        return false;
    }
}
