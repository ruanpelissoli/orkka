# SECURITY.md — Orkka on the developer's machine

## The model in one paragraph

Orkka is a control plane (board, pipeline, approvals, history) plus a runner, both in one
process (`./orkka start`, run from a git clone) on the developer's own machine. **Every agent run, every git operation and every tracker call
happens on that machine**, with the developer's own git sign-in, their own AI CLI subscription
(Claude Code, Codex, Copilot) and their own Jira/ADO/GitHub token (or `gh` login). There is no Orkka server: the
control plane is the local host (`Orkka.Local`, 127.0.0.1), the runner — in the same process —
dials it over loopback (SignalR, with its own `orkr_` tokens, exactly as a runner on another
machine would), and nothing listens beyond 127.0.0.1. "The host" below means that web host — it
never holds a forge or tracker credential and never stores code. For a corporate reviewer, the
short datasheet is `SECURITY-REVIEW.md` at the repository root.

The two questions this document answers, for a security reviewer:

1. **What leaves the machine?** — [Data that leaves the machine](#data-that-leaves-the-machine)
2. **What can the cloud make the machine do?** — [What the host can ask the runner to do](#what-the-host-can-ask-the-runner-to-do)

---

## Data that leaves the machine

| Data | Sent to Orkka? | Stored by Orkka? |
|---|---|---|
| Ticket title, description, acceptance criteria, comments (Jira/ADO/GitHub) | Yes — it is the task | Yes, as the task's text (capped: 20k chars description, 4k per comment) |
| Ticket attachments | Name, size and type only | Metadata only. Files are cached under `~/.orkka/workspaces/{ws}/tickets/` and copied into the task's worktree |
| Agent's own words and reasoning | Yes, streamed live | The final message is stored as the run's log |
| Agent tool calls | Tool name + small arguments (path, command line, search pattern). **File bodies are stripped on the machine** (`RunnerChunkPrivacy`) | Not stored |
| Agent tool results (file contents read, command output) | **No — dropped on the machine** | No |
| Plans, review comments, QA reports | Yes — they are the pipeline | Yes |
| Diffs and file contents | In transit only, held in memory for the one request: (1) when a person opens a diff, file or history in Orkka; (2) while the host assembles an agent's prompt — review and QA get the branch diff, planning and the cross-repo split get selected repository files — which goes straight back to the same developer's runner; (3) while a run's change statistics are computed | **No.** Change history keeps paths and line counts only (`TaskCodeChangeSnapshot` — see "Code at rest") |
| Output of the quality-gate command (lint/test) when it fails | Yes — posted to the task so the dev agent can fix it | Yes, in the task thread (truncated) |
| Commit author names | Yes (repo history view) | No. **Author e-mails are not sent** |
| Repository URL, branch names | Yes | Yes (the repo's settings) |
| Runner "Hello": machine name, which AI CLIs are installed/signed in, their version and sign-in mode (`api-key`, `bedrock`, … with a region/host/variable NAME — never a value), forge CLI logins (account names, no tokens), tracker connection metadata (site, account display name — no tokens), runner version | Yes, every 60 s | Presence row (latest Hello) |
| Git credentials, `gh`/`az`/`glab` tokens, AI CLI credentials | **Never** | Never |
| Tracker tokens | **Never from the machine.** The one exception runs the other way: a token the developer types into Settings → Trackers goes browser → local host → their own runner over loopback (`AddTrackerConnection`), write-only | **Never** — not stored, logged, echoed or audited by the host; the runner stores it in the OS secret store and answers with metadata only |
| The runner's own token | As the bearer credential of its connection only | SHA-256 hash only |

Where the code itself goes: to the **AI provider of the developer's own subscription**, by the
vendor's CLI, exactly as when the developer runs that CLI in a terminal. That relationship —
and whether the company allows it — is between the developer's employer and that vendor; Orkka
is not in it and never proxies it.

### Code at rest

Orkka stores no source code. `TaskCodeChangeSnapshot` keeps, per dev cycle, the branch, its
base, a hash, and per-file paths with added/removed line counts — the "what changed" history.
Opening a diff asks the developer's runner for it, live (`GetCodeChangeSnapshotDiffEndpoint`); a
finished snapshot shows the branch as it is now, and when the runner is offline or the branch is
gone the screen says so instead of showing a copy. Prompt assembly and change statistics pass code
through the host in memory (table above); nothing writes it to a database, a log or a cache.

---

## What the host can ask the runner to do

The runner only executes **named operations** (`RunnerProtocol`), each with typed arguments that
the runner validates. There is no "run this shell" operation, no remote terminal and no file
upload outside a task's worktree.

| Operation family | What the host controls | What the runner decides |
|---|---|---|
| Clone, fetch, worktree, commit, push, open PR | Which repo/branch (by id) | Credentials (the developer's own), paths (always under `~/.orkka/repos/{org}/{repo}` or the linked folder), never force-push |
| Agent run | The prompt, phase and model | Which guards apply (all, always — see below), the working directory (the task's worktree), whether the provider may run at all |
| Repository commands — worktree setup, quality gate (`check`), merge build, run targets (start/install/stop) | Which command it would like | **Runs it only if the repository's `orkka.json` on its default branch declares it, or the developer approved that exact command for that repository on this machine** (`CommandTrust`). Otherwise refused and listed under `./orkka commands` with an id; the task shows "approve on MACHINE: `./orkka commands approve <id>`" |
| Vet repository commands (`VetRepoCommands`, the project setup's command check) | Which repo (by id) and which setup/check/build command it would like | Runs nothing: answers whether `CommandTrust` would allow it (declared / approved) against the repo's mirror, and records a refused one as pending — the same pending entry a task would create — so the developer can approve it at the terminal before the first task. It cannot approve |
| Tracker dry run (the project setup's preview, `TrackerQuery` kind `dry-run`) | The poll the host would hand out on `SyncTrackers` (scope, board, epics, intake statuses, tracked keys) | Reads the tracker with the token exactly as the poll does, returns the normalized tickets; downloads no attachment, fetches no comment, caches and remembers nothing |
| Tracker query / status transition | Which connection, which scope and board (a Jira board id / ADO team id / GitHub project id — non-secret settings), which epics, which target status (GitHub: and which status field) | The token (never leaves), the tracker site (fixed at `tracker add`), whether a GitHub connection uses the `gh` login |
| Add / remove a tracker connection (Settings → Trackers) | Kind, site, email and — write-only — the token the developer typed (none when a GitHub connection uses the `gh` login) | Tests the token against that tracker first and stores only a working one, in the OS secret store; answers with metadata only; the host can never read a token back |
| Repo browsing (tree, file, history, diff) | Which repo/branch/path | Read-only, from the repo's own clone |
| Open in editor | Which task | Only editors from a fixed allowlist, only that task's worktree, only for the runner's owner |
| Provider sign-in | Which provider | Only the vendor CLI's own login command |

**Why repository commands need a vouch:** they are arbitrary shell, run outside any agent guard,
as the developer. A command committed to the default branch has been through the team's own
review — the same trust as the code itself. A task branch never counts (an agent wrote it), and
the host cannot approve anything: approvals live in `~/.orkka/command-approvals.json`, written
only by `./orkka commands approve` at the machine's terminal. So a compromised Orkka account
or server can *ask*, but cannot introduce a command.

---

## Agent guards

Agents run in a per-task clone under `~/.orkka/repos/…`, headless, with the CLI's own permission
prompts off (there is nobody to answer them). What they may do is decided by Orkka's guards,
which cannot be switched off from the host or by configuration.

### Per provider

| Provider | Shell commands | File reads | File writes |
|---|---|---|---|
| **Claude Code** | `AgentBashGuard` PreToolUse hook on `Bash` and `PowerShell`, every phase | `AgentSensitivePathGuard` hook on Read/Glob/Grep/LS/NotebookRead | `AgentWorktreeBoundaryGuard`: inside the worktree only |
| **Codex** (and Ollama, which runs through it) | Orkka's rules as Codex execpolicy (`.codex/rules/orkka.rules` in the worktree, git-excluded) + Codex's OS sandbox (`workspace-write`) | Not restricted by Codex | Codex sandbox: inside the worktree only |
| **GitHub Copilot CLI** | Orkka's rules as `--deny-tool shell(…)` | Copilot's path confinement to the working directory (`--allow-all-paths` is never passed) | Same confinement |
| Gemini, Grok, OpenCode | No hook or deny list Orkka can drive | — | — |

**Gemini, Grok and OpenCode are off by default.** They are reported "not ready" unless the
developer opts in on that machine (`ORKKA_ALLOW_UNGUARDED_PROVIDERS=gemini` in
`~/.orkka/runner.env`), and the runner refuses to start them otherwise — whatever the host sends.

### Command guard (`AgentBashGuard`)

Blocked, for every phase, and not approvable by anyone (including a human in an interactive
session):

- **Credentials**: forge CLIs (`gh`, `glab`, `hub`), `git credential`, credential-manager reads,
  `npm token`, keychain/credential-store tools (`security find-*-password`, `cmdkey`,
  `secret-tool`, `Get-StoredCredential`), and any path into a credential location (below).
- **Code leaving by another door**: `git push`, `git remote add/set-url/rename`, registry
  publishing (`npm publish`, `dotnet nuget push`, `docker push`, `twine upload`, …). Orkka's
  runner pushes the task branch itself and opens the PR with the developer's CLI.
- **Other hosts and clouds**: `ssh`/`scp`/`sftp`/`rsync`, network scanners, cloud and cluster CLIs
  (`aws`, `az`, `gcloud`, `kubectl`, `helm`, `terraform`, …), cloud metadata endpoints.
- **The machine**: service/account/disk control, deleting outside the worktree, containers that
  take over the host (`--privileged`, `-v /:/…`, the docker socket).
- **Secrets in the repo**: any command naming a real dotenv file (`.env`, `.env.local`; the
  `.env.example` bootstrap is allowed).
- **The environment** (`EnvironmentReadGuard`): dumping it — `env`, `printenv`, `set`, `export
  -p`, `declare -x`/`-p`, `typeset -x`, `Get-ChildItem env:` / `gci env:` / `ls env:`,
  `[Environment]::GetEnvironmentVariables()`, `/proc/*/environ`, `ps e…`, `print(os.environ)`,
  `console.log(process.env)` — and expanding an AI sign-in variable by name: `$ANTHROPIC_API_KEY`,
  `${OPENAI_API_KEY}`, `%CODEX_API_KEY%`, `$env:GH_TOKEN`, `env:AWS_SECRET_ACCESS_KEY`,
  `os.environ['…']`, `getenv('…')`, `process.env.…`, `ENV['…']`, `printenv …`. `env X=1 cmd`,
  `set -e`, `export PATH=…`, `printenv NODE_ENV`, `$env:PATH` and a plain `grep -r OPENAI_API_KEY
  src` stay allowed. Codex and Copilot get the prefix forms (`printenv`, `export -p`, `declare
  -x`/`-p`, `typeset -x`/`-p`, `cat /proc/self/environ`, `Get-ChildItem env:` …).
- **Commit identity**: setting `user.name`/`user.email` or `--author`.

Allowed: the project's own toolchain — builds, tests, linters, package installs, local database
clients, `docker compose` for the project's services.

### Protected locations (`DeveloperSecrets`)

Never readable by the file tools and never nameable in a shell line: `~/.ssh`, `~/.gnupg`,
`~/.aws`, `~/.azure`, `~/.config/gh`, `~/.config/gcloud`, `~/.kube`, `~/.docker/config.json`,
`~/.git-credentials`, `~/.netrc`, registry tokens (`~/.npmrc`, `~/.pypirc`, `~/.m2/settings.xml`,
`~/.cargo/credentials`, NuGet config, …), the AI CLIs' own logins (`~/.claude/.credentials.json`,
`~/.codex/auth.json`, …), OS credential stores and keyrings, browser profiles, system secrets
(`/etc/shadow`, …) — and **the whole Orkka home except this task's worktree**: other tasks,
other workspaces (a developer's other employer), the runner's settings and approvals.

Reads elsewhere are allowed on purpose (package caches are how an agent sees the API it codes
against) — with one more exception: **a real dotenv file outside this task's worktree**
(`.env`, `.env.local`, `.env.production`, … — not the committed templates `.env.example`,
`.env.sample`, `.env.template`) is refused by the read guard (`DeveloperSecrets.IsDotenvFile`),
and the command guard refuses any shell line naming one (`cat ../web/.env`, `cp ../web/.env .`).

### Sibling repositories (base-folder projects)

A project created from a folder of repositories (`Project.BaseFolderShape = MultiRepo`) lets its
agents READ the other repositories for reference:

- The host sends the base folder as `ReadOnlyDirs` on agent and chat runs
  (`RunnerAgentRunRequest.ReadOnlyDirs`, `RunnerChatRunRequest.ReadOnlyDirs`). The runner keeps
  only real, absolute folders that are not a drive root, the home folder or inside the Orkka home
  (`RunnerOperations.ExistingDirs`) — a host can widen reads to the developer's own folder, never
  to another workspace's clones.
- Claude gets `--add-dir`; its writes stay confined to the worktree by
  `AgentWorktreeBoundaryGuard`, and the read guard still refuses dotenv files and credentials
  there. Codex already reads anywhere (writes stay in its `workspace-write` sandbox).
- The prompt lists the siblings ("Sibling repositories (read-only reference)") and tells the
  agent its working copy is its clone: the developer's own checkout of the same repository in the
  base folder is never to be read or edited (it may hold their uncommitted work).
- Task folders and mirrors stay under `~/.orkka/repos`; nothing is ever written into the base
  folder (`DiscoverRepos` only asks git questions, and the developer's checkout only seeds a
  mirror by being cloned FROM).

### Environment

The agent process gets a sanitized environment (`AgentEnvironment`): every variable whose name
looks like a secret (`*TOKEN*`, `*SECRET*`, `*PASSWORD*`, `*API_KEY*`, `GITHUB*`, `GH_*`, `AZURE*`,
`AWS_*`, connection strings) is blanked. The developer's exported shell secrets do not reach it —
with one deliberate exception, the AI sign-in below.

### AI sign-in (environment-based modes)

A developer may sign a CLI in through its environment instead of an interactive login: an API
key, a company gateway, Amazon Bedrock, Google Vertex, Microsoft Foundry, Azure OpenAI, a GitHub
token, a GitHub Enterprise host (AGENTS.md → Sign-in modes). Then, per spawn, after the sanitizer:

- **Only an explicit allowlist, only for its own provider** (`ProviderAuthEnvironment`). Claude
  never gets `OPENAI_API_KEY` or a GitHub token, Codex never gets AWS or Anthropic credentials,
  Copilot never gets an Anthropic key; Gemini, Grok, OpenCode and Ollama get none. Codex's list
  also takes the `env_key` names of the developer's own `config.toml` providers, never a GitHub,
  cloud, other-vendor, database or registry name.
- **Values come from the machine or the run's workspace, never the host.** `~/.orkka/runner.env`,
  the environment `./orkka start` was started from (captured in memory before the startup scrub, only
  allowlisted names, never written back to the runner's own environment), or the workspace's own
  overrides (`./orkka workspace set-env` — secrets in the OS secret store under
  `orkka/{workspace}/ai/{NAME}`, plain settings in its profile.env). A workspace that sets any of a
  provider's variables uses only its own for that provider, so one job's credential never reaches
  another job's runs. No host→runner field carries any of it; the run request names no workspace
  either (`AgentRequest.Workspace` is set by the runner from the session the run arrived on).
- **Kept out of the agent's own commands, by the CLI itself**: Claude Code
  `CLAUDE_CODE_SUBPROCESS_ENV_SCRUB=1` (set when a credential is passed — strips credentials from
  Bash, hooks and stdio MCP servers; on Linux also an isolated PID namespace so `/proc` of other
  processes is unreadable); Codex `shell_environment_policy` with its default `*KEY*`/`*SECRET*`/
  `*TOKEN*` excludes on plus every passed name excluded; Copilot `--secret-env-vars` with every
  passed credential. The command guard's environment rule (above) is the second line.
- **Never shown.** The Hello reports the mode and a non-secret qualifier (region, host, variable
  name); `ProviderAuthResolution.ToString` names variables, never values; `workspace env` lists
  secret names only; `set-env` refuses a secret on the command line.

What is not covered — see Known limits.

### Interactive sessions

Same guard. The only extra outcome is "ask the human", used for commands that destroy work the
session has not landed (`git reset --hard`, `git clean`, history rewrites).

### Known limits (stated, not hidden)

- Agents have network access (package registries need it), so a determined agent could send what
  it may read — the repository — somewhere with `curl`. The guards close credential and
  identity theft and the obvious exfiltration doors (push, remotes, publish, gists); they do not
  sandbox the network. The repository is already going to the developer's AI provider.
- Codex does not restrict file reads outside the worktree; Orkka's path rules apply to its shell
  only via the command prefixes it can express — so the dotenv rule above does not stop Codex
  from reading a sibling repository's `.env`.
- Copilot's `--add-dir` has no read-only form, so Copilot gets the sibling repositories only in
  the phases where its write and shell tools are denied (PM, planning, review, chat). While it
  develops or tests it sees its own worktree only.
- Claude's Grep over a sibling FOLDER is not filtered file by file: ripgrep skips files the
  repository ignores (a `.env` normally is), but a sibling that commits its `.env` is readable
  that way.
- The guards are policy hooks inside the vendor CLI, not an OS sandbox. Claude Code's optional
  OS sandbox (`AgentSandbox:Enabled`) can be layered on.
- **An environment-based AI credential is reachable by the agent process itself.** It has to be:
  the CLI authenticates with it. The CLI keeps it out of the commands it runs, and the guard refuses
  the obvious reads, but code running inside the CLI's own process — or a command that reads another
  process's memory or `/proc/<cli-pid>/environ` where the OS allows it (Codex and Copilot run
  commands as the same user, without Claude's Linux PID namespace) — could still reach it, and a
  one-liner the guard does not recognise (an interpreter reading the environment some other way) is
  not stopped by the guard; only by the CLI's scrub. **A subscription login stored by the CLI**
  (`claude /login`, `codex login`, `copilot login`) **is preferable**: the agent's process carries
  no key, and the read guard keeps the CLIs' credential files out of reach.
- Codex's scrub needs a Codex that honours `shell_environment_policy` (all current releases); the
  exclude list only names what Orkka passed — a key the developer configured in Codex some other way
  (an `auth` command) never passes through Orkka at all. Copilot's `--secret-env-vars` redacts the
  shell and MCP environments; Copilot's own extensions and hooks are off in prompt mode.
- Claude Code reads `env` blocks and `apiKeyHelper` from the developer's OWN settings
  (`~/.claude/settings.json`) on its own; Orkka does not pass or filter those. It never reads the
  repository's: every Orkka-run Claude gets `--setting-sources user` (vendored SDK patch 4), so a
  committed `.claude/settings.json` / `settings.local.json` — its hooks, `env` (an
  `ANTHROPIC_BASE_URL` that would route the key elsewhere), MCP servers and permissions — is
  ignored. Verified with Claude Code 2.1.326: a committed SessionStart hook ran without the flag
  and did not with it.

---

## Secrets

| Secret | Where it lives | Leaves the machine? |
|---|---|---|
| Runner token (`orkr_…`) | OS secret store — Windows Credential Manager, macOS Keychain, libsecret (`~/.orkka/workspaces/{ws}/secrets/` 0600 file only when the machine has none, with a warning) | Only as the bearer of its own connection. Server keeps a SHA-256 hash; tokens expire (90 days) and the runner rotates them in use; revoking one drops the connection within a minute |
| Tracker tokens (Jira API token, ADO PAT, GitHub token) | Same OS secret store, entered at `./orkka tracker add` (hidden prompt or stdin) or in Settings → Trackers | Never — not in the Hello, not in errors (scrubbed), not to agents. From the settings page the token crosses the local host once, in memory, on its way to the runner (see below) |
| A GitHub connection's `gh` login (the default for GitHub) | The GitHub CLI's own store. The runner runs `gh auth token --hostname <host>` (read-only; no login, refresh or config change) for each tracker call and keeps the token in memory for that call only | Never — Orkka stores nothing for it. It is sent only to that GitHub host's API as a bearer header, scrubbed from every error, never logged, and never handed to an agent: agents' environments drop `GH_*`/`GITHUB*`/`*TOKEN*`, and the command guard refuses `gh` and `gh auth token` outright |
| Git / forge credentials | The developer's own (credential manager, `gh` login, SSH agent) | Never seen by Orkka |
| AI CLI credentials — interactive logins | The vendor CLI's own store | Never read by Orkka |
| AI CLI credentials — environment-based (API key, Bedrock/Vertex/Foundry keys, gateway token, GitHub token) | Where the developer put them: the shell `./orkka start` runs from, `~/.orkka/runner.env`, or a workspace's override in the OS secret store (`orkka/{workspace}/ai/{NAME}`, `workspace set-env`) | **Never.** Not stored or transmitted by Orkka: handed only to that vendor's own CLI on this machine, for that provider's runs (above). The CLI sends it to the vendor, as it would in a terminal |
| Orkka's own secrets (install secret, `Encryption:Key`) | Same OS secret store, scope `orkka/local/…`, generated by the first `./orkka start` | Never — handed to the host as in-memory configuration, never through the process environment (a test asserts none is there after startup); the `/internal/*` relay key is generated per process in memory |

### The one host → runner field that carries a credential

No runner operation carries a git, forge or AI credential (an AI sign-in variable is resolved on the
runner, from the machine or the run's workspace — never from the request). The single exception is
`AddTrackerConnection` (`AddTrackerConnectionRequest.Token`): at the developer's own request, the
token they type into Settings → Trackers (a client component, `type=password`, never in a URL,
a server action or browser storage) is posted same-origin to `POST /api/trackers/connections`,
relayed over loopback (`/internal/runner/tracker-connections`, per-process worker key, loopback
remote address only) to **their own** runner, tested against the tracker and stored in the OS
secret store. The host keeps it in memory for that one request only: the request body is not
logged (Serilog request logging records method, path and status only), validation messages never
quote it, it is absent from audit metadata (`TrackerConnectionChanged` records kind, site and
machine), the relay never echoes an exception message, and the request records' `ToString` print
`***`. Nothing ever sends a token back: the response, `trackers.connections` and every Hello
carry metadata only. `RemoveTrackerConnection` deletes the token on the machine.

---

## Distribution and updates

Orkka runs **from source**: `git clone`, then `./orkka start` (`orkka.cmd` on Windows). There is no
installer, no release binary and no self-update. The reviewer-facing datasheet is
**`SECURITY-REVIEW.md` at the repository root** (what runs, the one port and what it serves, what
is written where, every outbound destination, credentials, data at rest, how to audit). Keep it
true when anything below changes.

- **The launcher is readable.** `orkka` (POSIX sh) and `orkka.cmd` (cmd — no PowerShell, so no
  execution-policy prompt or bypass), ~50 commented lines each: check `dotnet` against
  `global.json` (and `node` ≥ 20 + `npm` only when the UI must be built), `npm ci` (only when
  `ui/package-lock.json` changed) and `npm run build` in `ui/` (only when a hash of `ui/`'s
  tracked files, uncommitted edits and new files changed), `dotnet build src/Orkka -c Release`,
  then `dotnet src/Orkka/bin/Release/net10.0/orkka.dll`.
- **Pinned dependencies.** NuGet packages come from nuget.org only (`nuget.config`) and are pinned
  by a committed `packages.lock.json` per project (`RestorePackagesWithLockFile`; CI restores in
  locked mode); npm packages by `ui/package-lock.json` through `npm ci`. Nothing is downloaded at
  runtime.
- **Zero persistent footprint.** Orkka writes only into the clone (build output) and the Orkka
  home (`~/.orkka`, `ORKKA_HOME`), plus the `{repo}-worktrees` task folders next to a linked local
  repo. It never changes PATH or any environment variable, the registry, shell rc/profile files or
  `$PROFILE`, services, scheduled tasks, autostart or git config (global or the user's repos' —
  the local-folder merge passes `receive.denyCurrentBranch=updateInstead` for one push through
  `--receive-pack`), and needs no admin rights. Environment changes are process-scoped
  (`Environment.SetEnvironmentVariable` on Orkka's own process, `GIT_CONFIG_*`). Orkka's own OS
  secret store entries (see Secrets) are the secure store, not a footprint to avoid.
  `./orkka doctor` reports what the retired `orkka-local` installer may have left (its opt-in PATH
  entry, shell lines, `~/.orkka/bin|app|runtime`) with manual steps, and changes nothing.
- **No git download.** The runner prints a hint when git is missing. A PortableGit an older
  Windows installer put in `~/.orkka/git` is still used when the machine has no git
  (`BundledGit`), never fetched or updated.
- **No execution-policy bypass.** Run commands use `pwsh -NoLogo -NoProfile -Command` (a
  `-Command` string is not subject to the policy) or `cmd /c`; a provider CLI found only as a
  `.ps1` runs through its sibling `.cmd` shim when there is one, else `-NoProfile -File` under the
  machine's policy. A refusal is explained in the run's log (`ShellResolver.ExecutionPolicyHint`),
  never worked around.
- **One process, one 127.0.0.1 listener.** `./orkka start` runs the host and the runner in one
  foreground process; the host binds `127.0.0.1:{port}` only (default 6750) and serves the API,
  the hubs and the built UI itself (static files — no Node.js at runtime, no proxy). Pages need the
  session cookie; build assets are public. HTML responses carry a Content-Security-Policy without
  inline or remote script (`StaticUi.ContentSecurityPolicy`), `nosniff`, `Referrer-Policy:
  no-referrer` and `X-Frame-Options: DENY`. The database is a file (`~/.orkka/data/orkka.db`, SQLite) that only this process
  opens — no database listener, no database password. Ctrl+C or `./orkka stop` drains the runner
  and stops the host; nothing stays running and nothing restarts Orkka by itself.
- **Secrets in memory.** The install secret and data-encryption key are generated on first start
  into the OS secret store and handed to the host as in-memory configuration; the `/internal/*`
  relay key exists only in the process's memory. Before the runner spawns anything, every
  sensitive variable is deleted from the process environment (the same scrub as ever). Secrets at
  rest in the database stay encrypted with that key.
- **Updates are `git pull`.** Nothing checks for a newer version; `./orkka update` only says to
  `git pull`, then `./orkka start`.
- Orkka sends no telemetry and no crash reports (nothing exports `CacheMetrics`). It logs to
  `~/.orkka/logs/orkka.log` and its own console.

---

## Orkka's side

- The database is a local SQLite file opened only by the host; it has no listener and no credentials, and agents never run on Orkka's side. The Read guard keeps agents out of the Orkka home (`~/.orkka`), where it lives.
- Every row carries `OrgId` with a global query filter; workspaces (one org per job) share nothing.
- Org audit log: per-org SHA-256 hash chain (`agents.agent_audit_events`), exportable.
- The runner hub authorises every runner call against the connection's bound (org, user);
  project ids are never taken from the payload.

---

## Checklist for a change that touches the runner

- Does anything new leave the machine? Update the table above, and prefer "read live, never store".
- Does the host gain a way to make the machine do something? It must be a named, typed operation;
  if it runs a command, it goes through `CommandTrust`.
- Does a new agent CLI get added? It ships unguarded-by-default until its guard is wired.
- Does an agent gain a tool? Check the guards' matchers cover it.
- Does a CLI gain a sign-in mode? Add its variables to that provider's allowlist only
  (`ProviderAuthEnvironment`), mark which are secret, teach `ProviderAuthModeDetector` the mode, and
  check the CLI still scrubs them from its subprocesses.
