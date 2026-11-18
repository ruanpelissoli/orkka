namespace Orkka.Modules.Execution.Agents;

using System.Text.RegularExpressions;

/// <summary>
/// The command guard's rule against reading the agent's own environment: dumping it
/// (<c>env</c>, <c>printenv</c>, <c>set</c>, <c>export -p</c>, <c>declare -x</c>,
/// <c>Get-ChildItem env:</c>, <c>/proc/*/environ</c>, <c>ps e</c>, <c>print(os.environ)</c>…) or
/// expanding a sign-in variable by name (<c>echo $ANTHROPIC_API_KEY</c>, <c>$env:GH_TOKEN</c>,
/// <c>%OPENAI_API_KEY%</c>, <c>os.environ['CODEX_API_KEY']</c>, <c>process.env.GITHUB_TOKEN</c>).
///
/// Why it exists: a developer who signs a CLI in through the environment (an API key, a
/// Bedrock key, a GitHub token) has that credential in the CLI's process. Each CLI is also
/// told to keep it out of the commands it runs (Claude <c>CLAUDE_CODE_SUBPROCESS_ENV_SCRUB</c>,
/// Codex <c>shell_environment_policy</c>, Copilot <c>--secret-env-vars</c>) — this is the
/// guard's second line, and the honest-agent one: the obvious ways of asking are refused and
/// audited. It is not a sandbox (SECURITY.md → Known limits).
///
/// Deliberately narrow so the toolchain keeps working: <c>env NODE_ENV=test npm test</c>,
/// <c>set -euo pipefail</c>, <c>export PATH=…</c>, <c>printenv</c>-free scripts, <c>go env</c>,
/// <c>echo $PATH</c>, <c>$env:PATH</c> and a plain <c>grep -r OPENAI_API_KEY src</c> (looking for
/// where the project reads its key) all pass.
/// </summary>
internal static partial class EnvironmentReadGuard
{
    /// <summary>True when the (lower-cased) shell line dumps the environment or expands a
    /// sign-in variable.</summary>
    public static bool ReadsEnvironment(string lowerCommand)
    {
        if (string.IsNullOrWhiteSpace(lowerCommand))
            return false;

        if (ProcEnviron().IsMatch(lowerCommand))
            return true;

        if (DumpFragments.Any(lowerCommand.Contains) || WholeEnvironmentObject().IsMatch(lowerCommand))
            return true;

        if (PowerShellEnvDrive().IsMatch(lowerCommand))
            return true;

        foreach (var segment in ShellCommandParser.SplitSegments(lowerCommand))
        {
            if (DumpsEnvironment(segment))
                return true;
        }

        return ExpandsSecret(lowerCommand);
    }

    /// <summary>Fragments that print the whole environment in a scripting one-liner.</summary>
    private static readonly string[] DumpFragments =
    [
        "[environment]::getenvironmentvariables(", "[system.environment]::getenvironmentvariables(",
        "env.to_h", "env.inspect", "env.to_a",
    ];

    /// <summary>One segment: <c>env</c> / <c>set</c> / <c>export</c> / <c>declare</c> /
    /// <c>typeset</c> in their listing forms, and BSD-style <c>ps e</c>.</summary>
    private static bool DumpsEnvironment(string segment)
    {
        var tokens = segment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim('"', '\'', '`', '(', ')'))
            .Where(t => t.Length > 0)
            .ToList();
        while (tokens.Count > 0 && tokens[0] is "sudo" or "command" or "builtin" or "exec" or "nohup" or "time")
            tokens.RemoveAt(0);
        if (tokens.Count == 0)
            return false;

        var program = tokens[0];
        var rest = tokens.Skip(1).ToList();
        switch (program)
        {
            case "env" or "/usr/bin/env" or "/bin/env":
                // A dump unless something after the flags and assignments is a program to run.
                for (var i = 0; i < rest.Count; i++)
                {
                    var t = rest[i];
                    if (t is "-u" or "--unset" or "-c" or "--chdir" or "-s" or "--split-string")
                    {
                        i++;
                        continue;
                    }
                    if (t.StartsWith('-') || IsAssignment(t))
                        continue;
                    return false;
                }
                return true;

            case "printenv":
                // Bare = every variable. `printenv NODE_ENV` reads one; a sign-in name is caught
                // by ExpandsSecret ("printenv" is one of its accessors).
                return rest.Count == 0;

            case "set":
                // `set` alone lists every variable (bash and cmd); cmd's `set NAME` lists every
                // variable starting with NAME. `set -e`, `set +x`, `set -o pipefail` are options.
                return rest.Count == 0
                       || (rest.Count == 1 && !rest[0].StartsWith('-') && !rest[0].StartsWith('+') && !rest[0].Contains('='));

            case "export":
                return rest.Count == 0 || rest.All(t => t.StartsWith('-')) && rest.Any(t => t.Contains('p'));

            case "declare" or "typeset":
                // Listing forms: flags only (`declare -x`), or any -p (`declare -p FOO`).
                return rest.Count == 0
                       || rest.Any(t => t.StartsWith('-') && t.Contains('p'))
                       || rest.All(t => t.StartsWith('-'));

            case "ps":
                // BSD-style flags (no dash) with `e` print every process's environment.
                return rest.Any(t => !t.StartsWith('-') && t.All(char.IsAsciiLetterLower) && t.Contains('e')
                                     && t.Length <= 8);

            default:
                return false;
        }
    }

    private static bool IsAssignment(string token)
    {
        var eq = token.IndexOf('=');
        return eq > 0 && token[..eq].All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    }

    /// <summary>Accessors that turn a variable NAME in the line into its VALUE.</summary>
    private static readonly string[] Accessors =
    [
        "environ", "getenv", "process.env", "env[", "env.get", "env.fetch", "env::var",
        "getenvironmentvariable", "printenv",
    ];

    /// <summary>A sign-in variable expanded or read by name: <c>$NAME</c>, <c>${NAME}</c>,
    /// <c>%NAME%</c>, <c>$env:NAME</c> / <c>env:NAME</c>, or the name next to an accessor
    /// (<c>os.environ</c>, <c>getenv</c>, <c>process.env</c>, <c>ENV[</c>, …).</summary>
    private static bool ExpandsSecret(string line)
    {
        foreach (var raw in ProviderAuthEnvironment.SecretNames())
        {
            var name = raw.ToLowerInvariant();
            var at = IndexOfWord(line, name);
            if (at < 0)
                continue;

            for (; at >= 0; at = IndexOfWord(line, name, at + 1))
            {
                var before = at > 0 ? line[at - 1] : ' ';
                if (before is '$' or '{' or '%' or ':')
                    return true;
            }

            if (Accessors.Any(line.Contains))
                return true;
        }

        return false;
    }

    /// <summary>Index of <paramref name="word"/> as a whole identifier (not part of a longer
    /// name such as <c>MY_GH_TOKEN_FILE</c>).</summary>
    private static int IndexOfWord(string line, string word, int start = 0)
    {
        var at = line.IndexOf(word, start, StringComparison.Ordinal);
        while (at >= 0)
        {
            var end = at + word.Length;
            var leftOk = at == 0 || !(char.IsAsciiLetterOrDigit(line[at - 1]) || line[at - 1] == '_');
            var rightOk = end == line.Length || !(char.IsAsciiLetterOrDigit(line[end]) || line[end] == '_');
            if (leftOk && rightOk)
                return at;
            at = line.IndexOf(word, at + 1, StringComparison.Ordinal);
        }
        return -1;
    }

    /// <summary>The whole environment object in a one-liner: <c>os.environ</c> /
    /// <c>process.env</c> not immediately indexed or read one key at a time
    /// (<c>print(os.environ)</c>, <c>os.environ.items()</c>, <c>console.log(process.env)</c>);
    /// <c>os.environ['HOME']</c>, <c>os.environ.get('HOME')</c> and <c>process.env.NODE_ENV</c>
    /// pass.</summary>
    [GeneratedRegex(@"os\.environb?(?!\s*\[|\.get\(|\.setdefault\(|\w)|process\.env(?![.\[\w])")]
    private static partial Regex WholeEnvironmentObject();

    /// <summary><c>/proc/self/environ</c>, <c>/proc/1234/environ</c>, <c>/proc/*/environ</c>.</summary>
    [GeneratedRegex(@"/proc/[^\s/]*/environ")]
    private static partial Regex ProcEnviron();

    /// <summary>The PowerShell <c>env:</c> drive listed whole: <c>gci env:</c>, <c>ls env:\</c>,
    /// <c>dir env:*</c>, <c>Get-ChildItem -Path Env:</c>. <c>$env:PATH</c> and
    /// <c>Get-Item env:PATH</c> name one variable and are left to <see cref="ExpandsSecret"/>.</summary>
    [GeneratedRegex(@"(^|[\s(;|&'""])env:(\\|/|\*)?(?=$|[\s);|&'""])")]
    private static partial Regex PowerShellEnvDrive();
}
