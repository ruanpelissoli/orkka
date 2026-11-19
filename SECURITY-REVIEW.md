# Orkka — security review datasheet

For a corporate security / EDR reviewer. Every statement here can be checked against the source
in this repository; the agent guards are detailed in [.claude/docs/SECURITY.md](.claude/docs/SECURITY.md).

**In one line:** Orkka is a developer tool that runs **from a git clone**, built on the developer's
own .NET SDK, as **one process** that listens on **127.0.0.1 only**. It installs nothing, changes
nothing outside the clone and `~/.orkka`, has no server, no account and no telemetry, and talks only
to services the developer already uses (their tracker, their git remotes, their AI CLI's vendor).

## 1. What you are running

| | |
|---|---|
| Distribution | Source only: `git clone`, then `./orkka start` (`orkka.cmd` on Windows). There is no installer, no binary download, no signed release to trust — you build what you read |
| The launcher | `orkka` (POSIX sh) and `orkka.cmd` (Windows cmd, not PowerShell — no execution-policy prompt or bypass), ~50 commented lines each. They check for `dotnet` (and `node`/`npm` when the UI needs building), run `npm ci` + `npm run build` in `ui/` when the UI sources changed, `dotnet build src/Orkka -c Release`, then `dotnet src/Orkka/bin/Release/net10.0/orkka.dll <command>` |
| Toolchain | The developer's own .NET 10 SDK (`global.json` pins the band) and Node.js 20+ (build time only: nothing runs on Node) |
| Rights | None beyond the signed-in user's. No administrator/root, no elevation prompt |

## 2. Dependencies and where they come from

| | Source | Pinned by |
|---|---|---|
| .NET packages | nuget.org only (`nuget.config` clears every other source) | `packages.lock.json` in every project (`RestorePackagesWithLockFile`); CI restores in locked mode, so an unpinned change fails the build |
| npm packages (UI build) | the machine's npm registry (`registry.npmjs.org` or the company mirror) | `ui/package-lock.json`, installed with `npm ci` |
| Vendored code | `src/ThirdParty/ih0.Claude.Agent.SDK` (MIT, with Orkka's patches listed in its `VENDORED.md`) | in the repository |

Audit: `dotnet list package --vulnerable --include-transitive` (CI runs it) and `npm audit` in `ui/`.

## 3. What is written, and where

Only two places: **the clone** (build output under `src/*/bin|obj`, `ui/node_modules`,
`ui/apps/local/dist`) and **one data folder, `~/.orkka`** (`%USERPROFILE%\.orkka`; relocatable with
`ORKKA_HOME`):

| Path | Contents |
|---|---|
| `data/orkka.db` (+ `-wal`, `-shm`) | Orkka's database, SQLite, opened only by the Orkka process (see §7) |
| `data/blobs/`, `data/sessions.json` | Attachments; SHA-256 hashes of browser sessions |
| `repos/` | The git clones agents work in (one mirror + one clone per task) |
| `workspaces/<name>/` | Per-job settings (`profile.env`, `trackers.json`), cached ticket attachments |
| `logs/orkka.log` | The log (rotated at 20 MB, 5 files) |
| `run/orkka.lock`, `run/instance.json` | The running instance's lock, PID and port |
| `runner.env`, `command-approvals.json` | Optional settings the developer writes; repository commands they approved |

One exception: for a repository linked from a local folder (e.g. `C:\Code\app`), task folders are
created next to it (`C:\Code\app-worktrees\task-…`).

**Never touched:**

- PATH or any other persistent environment variable (`HKCU\Environment` / `HKLM` included);
- the registry in general, Run/RunOnce keys, autostart entries;
- shell rc/profile files (`.bashrc`, `.zshrc`, `.profile`) and the PowerShell `$PROFILE`;
- services, daemons, scheduled tasks, cron, launchd/systemd units;
- global or system git config, and the developer's repositories' config (git settings are passed
  per process via `git -c` / `GIT_CONFIG_*`);
- firewall rules (nothing listens beyond 127.0.0.1, so no firewall prompt appears);
- PowerShell execution policy (never bypassed; a script the policy blocks stays blocked).

`./orkka doctor` reports leftovers of the retired `orkka-local` installer (a PATH entry it added,
lines in a shell file, `~/.orkka/bin`) and prints how to remove them by hand; it changes nothing.

## 4. Processes and the one port

`./orkka start` runs **one process** (`dotnet orkka.dll`), in the foreground, as the signed-in user.
It holds:

- **the web host**, listening on **127.0.0.1:6750** (`--port` to change; never another interface).
  It serves:

  | Path | What | Who may call it |
  |---|---|---|
  | `/` and `/{en,es,pt,fr,de}/…` | The built web UI (static files) | Pages need a session cookie; build assets (JS, CSS, fonts) are public — they contain no data |
  | `/api/*`, `/hubs/tasks` | The app's API and realtime hub | Session cookie (browser) or an `orkka_key_…` API key the developer created |
  | `/hubs/runner` | The runner's connection | `orkr_…` runner tokens (90-day, rotated) |
  | `/internal/*` | In-process relays | Loopback address **and** a random key that exists only in this process's memory |
  | `/mcp` | MCP endpoint for Claude Code / Cursor / scripts | `orkka_key_…` API keys only |
  | `/launch`, `/local/*` | Sign-in link; CLI control (launch code, runner bootstrap, shutdown) | One-time 60-second codes; the install secret (OS secret store) |
  | `/blobs/*` | Attachments | HMAC-signed URLs or a session |
  | `/health` | Liveness | Anyone on the machine (no detail beyond check names) |

  Every request passes a Host-header allowlist (`127.0.0.1`, `localhost`, `[::1]` on this port —
  stops DNS rebinding); cookie-authenticated writes need a same-origin `Origin` and an
  `X-Orkka-Csrf` header; cookie WebSockets need a same-origin `Origin`. The session cookie is
  `HttpOnly`, `SameSite=Strict`. HTML pages carry `Content-Security-Policy: default-src 'self';
  script-src 'self'; …; frame-ancestors 'none'` (no inline or remote script), `X-Content-Type-Options:
  nosniff`, `Referrer-Policy: no-referrer` and `X-Frame-Options: DENY`.
- **the runner** — no listener. It dials the host's `/hubs/runner` over loopback, deliberately the
  same protocol and token checks as a runner on another machine, and starts, as the user: `git`;
  the AI CLI the developer chose (`claude`, `codex`, `copilot`, …); `gh`/`az`/`glab` to open pull
  requests; a repository's own setup/check/run commands **only** if the repo's `orkka.json` on its
  default branch declares them or the developer approved them (`./orkka commands approve <id>`);
  for the QA lane with Claude, `npx @playwright/mcp@0.0.85` (pinned). The developer's own app,
  when they press Run, listens on the port their repository declares.

Ctrl+C or `./orkka stop` drains the runner, then stops the host; the process exits. An agent run
in flight is cancelled and its CLI killed with everything it started (shells, MCP servers). Nothing
keeps running in the background, and nothing starts Orkka again by itself.

## 5. Outbound network destinations

Orkka has **no server, no account, no telemetry, no analytics, no crash reporting, no update
check**.

| Destination | When | Made by |
|---|---|---|
| nuget.org; the npm registry | Build time only (`dotnet build`, `npm ci`) | The developer's own `dotnet` / `npm` |
| The developer's Jira Cloud site / `dev.azure.com/<org>` | Polling assigned tickets, writing status, ticket attachments (same site only) | The runner, with the developer's own token |
| `api.github.com` (github.com) or `api.<enterprise>.ghe.com` (GHE.com data residency) — GraphQL only | Polling a GitHub project's issues assigned to the developer, setting the project's status field | The runner, with the developer's own token or their `gh` login (`gh auth token --hostname <host>`, read at each call, never stored) |
| The developer's git remotes and forge | Clone, fetch, push, open PRs | The developer's own `git` / `gh` / `az` / `glab` and their credentials |
| The AI vendor of the CLI in use (Anthropic, Amazon Bedrock, Google Vertex, Microsoft Foundry, Azure OpenAI, a company LLM gateway, OpenAI, GitHub Copilot incl. GHE) | Every agent run | That CLI, under the developer's own sign-in; Orkka never proxies it |
| The npm registry | QA lane with Claude: `npx @playwright/mcp@0.0.85` | The developer's own `npx` |
| Local Ollama (`localhost:11434`) | Only if the developer chose Ollama | codex CLI |

What Orkka sends to the AI vendor is what the CLI sends: the task, the repository files the agent
reads. Tool results never leave the runner for the host's database; Orkka stores no source code
(diffs are read live from the clones).

## 6. Secrets

In the **OS secret store** — Windows Credential Manager, macOS Keychain (service `orkka`), Linux
Secret Service (service `orkka`); an owner-only file under `~/.orkka/workspaces/<ws>/secrets/` only
where none exists (or when `ORKKA_SECRET_STORE=file` / a sandbox `ORKKA_HOME` asks for it):

| Entry | What |
|---|---|
| `orkka/local/install-secret` | The bearer the CLI (`open`, `status`, `stop`) presents to the running host |
| `orkka/local/encryption-key` | AES key protecting secrets at rest in the database |
| `orkka/<workspace>/runner-token` | The runner's token for its workspace (expires after 90 days, rotated while connected) |
| `orkka/<workspace>/tracker/<connection>` | The Jira API token / Azure DevOps PAT / GitHub token the developer entered (none for a GitHub connection that uses the `gh` login) |
| `orkka/<workspace>/ai/<VARIABLE>` | Only if one job gets its own AI sign-in (`./orkka workspace set-env`) |

**In memory, never in the environment.** The install secret and the encryption key are read from
the store and handed to the host as in-memory configuration; the relay key is generated per process
in memory. None of them is ever written to a file Orkka creates, printed, or put in the process
environment — and before the runner starts any agent, every sensitive environment variable
(`*SECRET*`, `*TOKEN*`, `*API_KEY*`, cloud and connection-string variables, …) is deleted from the
process. An automated test asserts that no host secret is in the environment after startup.

Not stored by Orkka: git and forge credentials, and AI CLI logins — they stay in their own tools.
An environment-based AI sign-in (an API key, Bedrock, Vertex, a gateway, a GitHub token) is handed
only to that vendor's own CLI, from a fixed allowlist per provider (SECURITY.md → AI sign-in).

## 7. Agent guards (summary)

Agents run as the developer, so the runner confines them regardless of what a ticket says: a
command guard refuses credential reads, `git push`/new remotes, package publishing, forge CLIs,
other hosts (`ssh`, scanners) and cloud/cluster CLIs; a read guard keeps them away from SSH/GPG
keys, cloud sign-ins, CLI logins, the OS credential store, browser profiles, the Orkka data folder
and other tasks' folders; writes stay inside the task folder; secret-looking environment variables
are blanked and printing the environment is refused. Claude Code is guarded with pre-tool hooks,
Codex with exec-policy rules and its sandbox, Copilot with tool deny rules; CLIs that cannot be
guarded (Gemini, Grok, OpenCode) are off unless the developer explicitly allows them. Repository
commands run only when the repository's default branch declares them or the developer approved
them on this machine. Details and known limits: [.claude/docs/SECURITY.md](.claude/docs/SECURITY.md).

## 8. Data at rest

- `~/.orkka/data/orkka.db`: tickets' text and comments, tasks, plans, agent messages, review
  comments, usage counts, settings. **No source code** and **no plain credentials**. A file, not a
  server: no listener, no password. Protected by the user's file permissions.
- `~/.orkka/repos` (and `-worktrees` folders next to linked repos): the clones agents work in — the
  same code the developer already has.
- `~/.orkka/workspaces/<ws>/tickets/`: cached ticket attachments. `~/.orkka/logs`: local logs.

## 9. How to audit

- Read the launcher scripts (`orkka`, `orkka.cmd`) and `src/Orkka/` (the entry point:
  `StartCommand.cs`, `InstanceCommands.cs`).
- Before/after `./orkka start`, compare: PATH, `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`,
  services (`Get-Service`), scheduled tasks (`Get-ScheduledTask`); on macOS/Linux your rc files,
  `crontab -l`, launchd/systemd user units. They do not change.
- While it runs: `netstat -ano` (Windows) / `lsof -iTCP -sTCP:LISTEN` shows one listener,
  `127.0.0.1:6750`, owned by the `dotnet` process.
- After `./orkka stop`: no `dotnet` process of Orkka's and no agent CLI it started remains.

## 10. Uninstall

Nothing was installed: stop Orkka, delete the clone, delete `~/.orkka`, and optionally the `orkka/…`
entries in the OS secret store. See [README → Uninstall](README.md#uninstall).
