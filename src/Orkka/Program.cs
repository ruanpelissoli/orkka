// orkka — the single entry point of Orkka on the developer's machine, run from the clone by the
// `./orkka` (or `orkka.cmd`) script. `start` runs everything in this one process: the Orkka host
// (API, engine, hubs, the built web UI, its SQLite database) on 127.0.0.1 only, and the runner,
// which executes everything that needs this machine — git with the developer's own credentials,
// and the AI CLIs with the developer's own subscriptions. Every other command is one-shot.
using Orkka.Cli;
using Orkka.LocalRunner;
using Orkka.LocalRunner.Cli;

// `--debug` (any position, any command; also ORKKA_DEBUG=1) switches from the clean end-user
// output to full structured logs.
var parsed = CliArgs.Parse(args, Environment.GetEnvironmentVariable);
string[] rest = [.. parsed.RemainingArgs.Skip(1)];

switch (parsed.Command)
{
    case "start":
        return await StartCommand.RunAsync(rest, parsed.Debug);
    case "stop":
        return await InstanceCommands.StopAsync();
    case "status":
        return await InstanceCommands.StatusAsync();
    case "open":
        return await InstanceCommands.OpenAsync(rest);
    case "connect":
        return await ConnectRunner.RunAsync(rest, parsed.Debug);
    case "doctor":
    {
        // After the env scrub, so it traces the child spawn under the exact environment real
        // agent runs get. Optional arg picks the CLI to probe; default = first connected provider.
        RunnerProcess.PrepareEnvironment(Console.WriteLine);
        return await DoctorCommand.RunAsync(rest.FirstOrDefault()?.ToLowerInvariant());
    }
    case "workspace" or "workspaces":
        // One workspace per job: its own runner token, git author and tracker tokens.
        return WorkspaceCommand.Run(rest);
    case "commands" or "command":
        // Which repository commands this machine runs is decided here, by the developer —
        // never by anything the host sends.
        return CommandsCommand.Run(rest);
    case "tracker" or "trackers":
        // Tracker tokens are added here or in Settings → Trackers, and stay on this machine.
        return await TrackerCommand.RunAsync(rest);
    case "update":
        Console.WriteLine("Orkka runs from source: `git pull`, then `./orkka start`.");
        return 0;
    case "uninstall":
        Console.WriteLine("Nothing to uninstall: Orkka installed nothing. Stop it (`./orkka stop`), then delete");
        Console.WriteLine($"this folder and Orkka's data folder ({RunnerEnvFile.OrkkaDir}). See README.md → Uninstall.");
        return 0;
    case "help" or "--help" or "-h" or null:
        Console.WriteLine(Help.Text);
        return 0;
    case var unknown:
        Console.Error.WriteLine($"Unknown command '{unknown}' — try `./orkka help`.");
        return 1;
}

namespace Orkka.Cli
{
    internal static class Help
    {
        public const string Text =
            """
            Orkka — your tickets worked on this machine, with your own git and AI subscriptions.
            Everything runs in one process that listens on 127.0.0.1 only; there is no account and no cloud.
            (Windows: `orkka <command>` in cmd, `.\orkka <command>` in PowerShell.)

              ./orkka start [--port N] [--no-browser]
                                   build and start Orkka (default port 6750) and open it in your browser.
                                   Runs in the foreground; Ctrl+C stops it
              ./orkka open [--print]
                                   sign a browser in again (a fresh one-time link); --print only prints it
              ./orkka status       where Orkka runs and whether it is healthy
              ./orkka stop         stop the running Orkka (from another terminal)
              ./orkka workspace list | remove <name>
              ./orkka workspace set-identity <name> --name "…" --email …   (or --clear)
                                   the jobs on this machine, and the git author of each
              ./orkka workspace set-env <name> KEY[=value] | unset-env <name> KEY | env <name>
                                   that job's own AI sign-in (a key, Bedrock, Vertex, a gateway, a
                                   GitHub token or Enterprise host); secrets asked for hidden and
                                   kept in the OS secret store
              ./orkka doctor [claude|codex|gemini|grok|copilot|opencode|ollama]
                                   trace one agent run through the runner's spawn path
              ./orkka connect      advanced: run only the runner, against an Orkka on another machine or
                                   port: --token orkr_... [--workspace <name>] [--host <url>]
              ./orkka update       Orkka runs from source: `git pull`, then `./orkka start`

            Ticket trackers (the token never leaves this machine):
              ./orkka tracker add jira --site <acme.atlassian.net> --email <you@acme.com>
              ./orkka tracker add ado --org <https://dev.azure.com/acme | acme>
                                   connect Jira Cloud / Azure DevOps; asks for the token (hidden)
                                   or reads it from stdin. Jira token:
                                   https://id.atlassian.com/manage-profile/security/api-tokens
                                   ADO: User settings → Personal access tokens, Work Items
                                   (Read & write) + Project and Team (Read)
              ./orkka tracker list | test [id] | remove <id>
                                   with several workspaces, add --workspace <name>

            Repository commands (setup, check, build, run targets):
              ./orkka commands                 waiting for approval / approved on this machine
              ./orkka commands approve <id>    allow one command for one repository here
              ./orkka commands revoke <id>     forget an approval
                                   commands in a repository's orkka.json on its default branch
                                   run without approval; nothing else runs until you approve it

            Flags:
              --debug              verbose structured logs — works with any command (also: ORKKA_DEBUG=1)

            Settings (environment or ~/.orkka/runner.env):
              ORKKA_MAX_CONCURRENT_RUNS   agent runs at once across all workspaces (default 2)
              ORKKA_HOME                  use this folder instead of ~/.orkka (environment only)

            Requires: git, and at least one AI provider signed in on this machine — claude (`claude /login`),
            codex (`codex login`), gemini, copilot (`copilot login`), or a local Ollama
            server (free: install Ollama + the codex CLI, no login needed). The QA
            browser-testing lane needs claude. Environment sign-ins work too — an API key,
            a company gateway, Amazon Bedrock, Google Vertex, Azure OpenAI, a GitHub token —
            set in the environment you start Orkka from, in ~/.orkka/runner.env, or per job
            with `./orkka workspace set-env`; each CLI gets only its own variables.
            """;
    }
}
