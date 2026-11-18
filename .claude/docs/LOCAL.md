# LOCAL.md — how Orkka runs on the developer's machine

Orkka runs **from source, as one process.** `git clone` + `./orkka start` (`orkka.cmd start` on
Windows) builds it with the developer's own .NET SDK and runs `dotnet orkka.dll` in the foreground.
Nothing is installed, downloaded at runtime, supervised or added to PATH; there is no account and
no cloud, and the one listener is on **127.0.0.1**.

```
./orkka start  →  dotnet src/Orkka/bin/Release/net10.0/orkka.dll start      (one process)
  ├─ the host (Orkka.Local, OrkkaHost)  127.0.0.1:6750   API + background engine + /hubs/tasks +
  │                                                      /hubs/runner + the built UI (static files);
  │                                                      its database is ~/.orkka/data/orkka.db
  └─ the runner (Orkka.LocalRunner, RunnerHost)  (no listener)  dials http://127.0.0.1:6750/hubs/runner
                                                      with its orkr_ tokens; spawns git and the AI CLIs
```

The runner dials the host over loopback deliberately: the same protocol, token checks, guards,
`CommandTrust` and token rotation as a runner on a second machine (`./orkka connect`). The two are
separate composition roots (a `WebApplication` and a generic `IHost`) that share only the process.

## The launcher scripts (`orkka`, `orkka.cmd`)

Plain sh / cmd (no PowerShell: no execution-policy prompt), ~50 commented lines each:

1. `dotnet` must exist and satisfy `global.json` (SDK 10.0.300 band, `rollForward: latestFeature`);
   otherwise a message with the download link.
2. **`start` only:** the UI is rebuilt when `ui/apps/local/dist/index.html` is missing or the
   **UI stamp** changed — `git hash-object` of `git ls-files -s -- ui`, `git diff -- ui` and the
   untracked files under `ui/` (so uncommitted edits count), compared with
   `ui/apps/local/dist/.orkka-build-stamp`. Then `node` ≥ 20 + `npm` are required; `npm ci` runs
   only when `ui/package-lock.json`'s hash differs from `ui/node_modules/.orkka-lock-stamp`;
   `npm run build` in `ui/`; a build that does not leave `dist/index.html` stops with a clear error.
   `ORKKA_SKIP_UI_BUILD=1` skips this step (the host then serves its "UI isn't built" page, or
   whatever `Local__UiDir` points at).
3. `dotnet build src/Orkka -c Release` — for `start` always (a no-op when nothing changed), for any
   other command only when `orkka.dll` is missing, so `./orkka status` answers at once. A build
   that fails while Orkka runs (its DLLs are in use on Windows) says to `./orkka stop` first.
4. `dotnet src/Orkka/bin/Release/net10.0/orkka.dll "$@"` (`exec` in sh: the process IS Orkka, so
   Ctrl+C and SIGTERM reach it directly).

They write only inside the clone and the Orkka home.

## Commands (`src/Orkka`, assembly `orkka`)

| Command | What it does |
|---|---|
| `start [--port N] [--no-browser]` | Runs Orkka in the foreground (below). Ctrl+C / SIGTERM stops it |
| `open [--print]` | `POST /local/launch-codes` → opens `http://127.0.0.1:{port}/launch?code=…` (a one-time, 60-second code); `--print` prints only the URL (scripts, CI) |
| `status` | The running instance (from `run/instance.json`), `/health` with its checks, data and log paths. Exit 0 healthy, 1 not answering, 3 not running |
| `stop` | `POST /local/shutdown` (install-secret bearer), then waits up to 60 s for the PID to exit; a process that does not answer or end in time is killed — only when its PID still has the recorded start time |
| `workspace`, `tracker`, `commands`, `doctor` | Unchanged runner-side commands (Orkka.LocalRunner/Cli). `doctor` also lists leftovers of the retired installer (`LegacyInstall`: `~/.orkka/bin` on PATH, its lines in `~/.bashrc`/`~/.zshrc`/`~/.profile`, `~/.orkka/bin|app|runtime`) with manual steps — it never edits PATH or a shell file |
| `connect [--token orkr_… --workspace <name> --host <url>]` | Advanced: the runner alone, against an Orkka elsewhere |
| `update` | Prints "Orkka runs from source: `git pull`, then `./orkka start`." |
| `uninstall` | Prints what to delete (the clone and `~/.orkka`); deletes nothing |

## `start`, in order (`StartCommand`)

1. **One per Orkka home.** A live `run/instance.json` (its PID still has the recorded start time)
   → "already running at …, `./orkka open` / `./orkka stop`". Then `run/orkka.lock` is opened
   exclusively for the life of the process (the OS releases it however it ends). The port must be
   free: when it is not, `GET /health` tells another Orkka (its JSON has `"service":"orkka"`, i.e.
   one with a different `ORKKA_HOME`) from some other program; both get a message with `--port`.
2. **Secrets** (`LocalSecrets`, OS secret store, scope `_local` → `orkka/local/{id}`):
   `install-secret` (URL-safe random) and `encryption-key` (base64 of 32 bytes), generated on first
   start; the retired `proxy-secret` is deleted. A sandbox `ORKKA_HOME`, or
   `ORKKA_SECRET_STORE=file`, uses the owner-only file store (`~/.orkka/workspaces/_local/secrets/`).
3. **The host** — `OrkkaHost.BuildAsync` with the secrets, `Local:Port` as **in-memory settings**
   (never environment variables). It applies every module's migrations to `{DataDir}/orkka.db`
   before it listens (DATABASE.md). Logging: everything to `logs/orkka.log`; the terminal shows
   warnings and errors only, unless `--debug`.
4. **Environment scrub** (`RunnerProcess.PrepareEnvironment`) — once the host has read its
   configuration: captures the runner's settings and the AI sign-in variables it was started with,
   then deletes every sensitive variable from the process (agents inherit it), sets
   `GIT_TERMINAL_PROMPT=0` & co., arms the Gemini dotenv guard and the Claude setup-token source.
5. **Kestrel starts** (127.0.0.1:port); `run/instance.json` is written (pid, port, start times).
6. **Bootstrap** — `POST /local/bootstrap` over loopback with the install secret → `[{workspaceId,
   name, token|null}]` (ensures the local user and a default workspace, mints an `orkr_` token per
   workspace with no live one named for this machine). A minted token becomes a runner profile
   (`RunnerProfileSync`: found again by `ORKKA_WORKSPACE_ID` in its `profile.env`, else named after
   the workspace, suffixed on a clash); existing profiles are pointed at the current port.
   Repeated every 30 s, so a workspace created in the UI gets a profile; the runner picks new
   profiles up within 10 s (`RunnerSupervisor` hot-add).
7. **The runner** — `RunnerHost.Build` (its own `IHost`, a lifetime that ignores Ctrl+C so the
   owner stops it in order) with every profile, started.
8. **Browser** — a launch code → the `/launch?code=…` URL, always printed, opened unless
   `--no-browser`.

**Stopping** (Ctrl+C, SIGTERM, or `./orkka stop` → `/local/shutdown` → `StopApplication`): the
runner stops first (15 s window), then the host (10 s), then `instance.json` is deleted and the
process exits. Each runner session cancels the agent runs it has in flight and waits up to 10 s
for their CLIs to exit; any agent CLI still running after that (`AgentProcesses`, every CLI the
runner started) is killed with its whole tree — its shells, MCP servers, test runners — so no
agent outlives Orkka. The host parks those tasks (`WaitingForRunner`) and re-queues them when the
runner is back on the next `./orkka start`. There is no supervisor, no restart loop, no detached
mode and no Job Object: closing the terminal ends Orkka.

## The host's paths

`/api/*` (FastEndpoints), `/hubs/tasks`, `/hubs/runner`, `/internal/runner/*` (loopback + the
per-process relay key), `/mcp`, `/blobs/*`, `/launch`, `/local/*` (install-secret bearer),
`/health`. **Everything else is the web UI** (`StaticUi`, from `Local:UiDir`, default
`<repo>/ui/apps/local/dist`, found by walking up from the app's folder to `Orkka.slnx`):

The contract is the UI's own (`ui/apps/local/README.md` → "How it is served"):

- a file of the build → served publicly (hashed `/assets/*` and `/excalidraw/fonts/*`
  `Cache-Control: public, max-age=31536000, immutable`; other files `no-cache`), with content
  types for `.woff2`, `.webmanifest`, `.svg`, `.mjs`; a missing file under those two folders is a
  plain 404, never the app (a tab on an older build then reloads);
- `/` and any path whose first segment is not a locale (`en|es|pt|fr|de`) → 302 to
  `/{locale}{path}{query}` (`/` → `/{locale}`; the `NEXT_LOCALE` cookie, else the best
  `Accept-Language` match, else `en`);
- any other GET under `/{locale}/…` → `index.html` (`no-cache`) for a caller with the session
  cookie or the install-secret bearer; anyone else gets 401 and the signed-out page ("run
  `./orkka open`");
- no `index.html` in the UI folder → 503 "The UI isn't built";
- HTML carries `Content-Security-Policy` (`StaticUi.ContentSecurityPolicy(port)`: the UI's
  policy — `default-src 'self'`, `script-src 'self'`, inline styles, data/blob images, data fonts,
  `ws://127.0.0.1:{port}`/`ws://localhost:{port}`, blob/data workers, `base-uri 'none'`,
  `frame-ancestors 'none'` — plus `frame-src 'self' blob:` for the PDF viewer's object-URL iframe),
  `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `X-Frame-Options: DENY`.

`LocalRequestGuard` runs first on every path (Host allowlist, Origin + CSRF header on
cookie-authenticated writes, Origin on cookie WebSockets) — see CLAUDE.md → Hosts.

## Layout (`~/.orkka`, or `ORKKA_HOME`)

| Path | What |
|---|---|
| `data/` | `Local:DataDir` — `orkka.db` (+ `-wal`/`-shm`), `blobs/`, `sessions.json` |
| `logs/orkka.log` | The process's log (host + runner; 20 MB × 5) |
| `run/orkka.lock`, `run/instance.json` | The running instance: exclusive lock; pid, port, start times |
| `repos/` | Mirrors and task clones (RUNNER_GIT.md) |
| `workspaces/{name}/profile.env` | Runner profiles (host URL, git identity, workspace id) and that job's plain AI sign-in settings (`workspace set-env`: region, profile, base URL, host, `ORKKA_*_AUTH=login`, and the NAMES of its secrets in `ORKKA_AI_SECRET_ENV`) |
| `runner.env` | Machine-wide runner settings and AI sign-in variables (`CLAUDE_CODE_OAUTH_TOKEN`, `ANTHROPIC_API_KEY`, `GH_TOKEN`, …) — read one value at a time, never loaded into the process environment |
| `command-approvals.json` | Repository commands approved on this machine (`./orkka commands approve`) |
| `git/` | Only on installs from before 2026-11: a PortableGit an older installer unpacked. Still used when the machine has no git (`BundledGit`), never fetched or updated |
| `bin/`, `app/`, `runtime/`, `local.json`, `run/state.json` | Leftovers of the retired installer; unused — `./orkka doctor` lists them, deleting them is safe |

### AI sign-in

The runner works with whatever sign-in each AI CLI has on this machine: its own login
(`claude` → `/login`, `codex login`, `copilot` → `/login`), or an environment-based one — an API
key, a company gateway, Amazon Bedrock, Google Vertex, Microsoft Foundry, Azure OpenAI, a GitHub
token or GitHub Enterprise host (the full table: AGENTS.md → Sign-in modes). Set those variables
in the shell you run `./orkka start` from (captured at start, before the scrub), in
`~/.orkka/runner.env`, or per job with `./orkka workspace set-env <workspace> KEY` (secrets:
hidden prompt or piped stdin, stored in the OS secret store; plain settings: `KEY=value`).
`workspace env <workspace>` lists what a job overrides, `unset-env` removes one. Each CLI gets
only its own variables, the workspace's when it sets any for that provider, else the machine's;
the Machine page shows every CLI's version, sign-in mode and readiness. `./orkka doctor` prints
the same table plus the variable NAMES the probed CLI would get. Orkka never stores or transmits
the credential itself (SECURITY.md → AI sign-in).

## Development

- Everything above is the dev loop too: `./orkka start --debug` (structured logs on the console).
- Try it in a sandbox: `ORKKA_HOME=<temp dir> ORKKA_SECRET_STORE=file ./orkka start --port 6791
  --no-browser`. `USERPROFILE`/`HOME` do not sandbox on Windows — always set `ORKKA_HOME`.
- `Local__UiDir=<folder>` serves another UI build (environment configuration is read before the
  scrub); `ORKKA_SKIP_UI_BUILD=1` keeps the script from building `ui/`.
- `ConnectionStrings__DefaultConnection` points the database at another SQLite file.
- The integration tests build the same host (`OrkkaHost.BuildAsync`) on a test server
  (`OrkkaApiFactory`), one SQLite file per factory.

## Troubleshooting

| Symptom | Look at |
|---|---|
| "Your .NET SDK does not match global.json" | Install a .NET 10 SDK of the 10.0.300 band or newer |
| "Port … is in use by another program" / "Another Orkka … on port" | `--port`; or stop the other one |
| "Orkka is already running" | `./orkka open` / `./orkka stop`; a stale `run/instance.json` whose PID is gone is ignored |
| The build fails with files in use | Orkka is running from this clone: `./orkka stop`, then `./orkka start` |
| "The UI isn't built" (503) | `./orkka start` builds it; or `npm ci && npm run build` in `ui/` |
| Host never healthy / start fails | `logs/orkka.log` (migrations, `orkka.db` not writable) |
| "database is locked" in the log | A write waited past the busy timeout — something held the one write lock too long (DATABASE.md → Concurrency). Stop any external tool that has `orkka.db` open for writing |
| A workspace has no runner | The console: "has no runner token on this machine" — the host must mint a new one |
| An AI CLI shows "not ready" though it works in your terminal | Machine page / `./orkka doctor`: the mode it detected. An env sign-in set only in a terminal opened after `start` is not in Orkka's environment — put it in `~/.orkka/runner.env` or `workspace set-env`. A launcher stub on PATH (e.g. a `copilot` that says the CLI "was not found") reads as not installed |
| Everything stuck | Ctrl+C (or `./orkka stop`), then `./orkka start` |
