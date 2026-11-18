The background scan confirmed the finding: no `playwright.config.*`, no `@playwright/test`, no Cypress, no puppeteer in any `package.json` across the workspace — every hit is QA-agent Playwright MCP prose or model-catalog text.

---

# Orkka E2E Test-Case Catalogue — Source Inventory

> **2026-11-18 — R4, run from source (supersedes everything below about installing or running Orkka).** There is no `orkka-local` binary any more: Orkka is a git clone, `./orkka start` (`orkka.cmd` on Windows) builds it and runs ONE foreground process (`dotnet src/Orkka/bin/Release/net10.0/orkka.dll` — the `src/Orkka.Local` host with API, hubs, `/internal/runner/*` relays and the static Vite UI from `ui/apps/local/dist`, plus the in-process runner; SQLite at `~/.orkka/data/orkka.db`). Verbs (`src/Orkka/Program.cs`): `start`, `stop`, `status`, `open`, `connect` (advanced: a runner alone), `doctor`, `workspace(s)`, `commands`/`command`, `tracker(s)`, `update` (prints "Orkka runs from source: `git pull`, then `./orkka start`."), `uninstall` (prints how to delete the folder), `help`. GONE: release downloads and signature verification (`ReleaseVerification`), the newer-release check (`--no-update-check`, `ORKKA_NO_UPDATE_CHECK`), `path add|remove|status`, `--detach`, `start`/`up`/`down` as stack verbs, the default `connect` verb, the supervisor, portable Postgres, the Next.js standalone UI server, YARP, `Orkka.Local.Host`, `Orkka.AppHost`, `Orkka.Worker`, the `orkka-local-cli` distribution repo. UI routes are declared in `ui/apps/local/src/routes.tsx`. Sibling repo paths below (`orkka-api`, `orkka-ui`) are now `src/` and `ui/` of this monorepo; command lines below were renamed to `./orkka …`, but descriptions of the removed pieces are history only.
>
> **2026-10-26 pivot (tracker-driven, runner-only).** Removed: managed/hosted apps (brief, design brief, provisioning, trials, Vercel/Neon, custom domains, env vars, platform tokens, `/platform/v1`), the GitHub App (install, webhooks, issue/CI triggers, create-branch), Orkka Local (Docker stack, `/api/local/*`, `apps/local`, `./orkka start|stop|status`), milestone integration branches / ship sweep / retry-ship, cloud agent runs (dormant behind `Execution:CloudEnabled`) and the platform-key AI fallback. Sections about those were deleted below; what remains predates the pivot and may name them in passing.
>
> **2026-11-17 update (orkka-api e3a3a06 … 10707ed, orkka-ui 6c17c16 / 8ddcd6f).** Cloud execution is GONE, not dormant: no `ExecutionTarget`/`ExecutionTargetReason` on tasks, no execution preference (`GET|PUT /api/me/execution-preference`), no execution-target preview/switch (`POST /api/projects/{id}/tasks/execution-target`, `POST /api/tasks/{id}/execution-target`), no org `allowHybridExecution`, no project `cloudFallbackAfterMinutes`, no cloud fallback sweep / provider capacity gate / free-tier ceiling / cloud jobs, forecasts are time-only (cost always null, no `executionTarget*`), admin usage has no `cloudExecutions`/`localExecutions`. Every task runs on its owner's runner (`RunnerUserId`); `IRunnerCoverageResolver` only explains whether that runner has the model's provider. Orkka stores no code: code-change snapshots keep paths/counts/hash, `GET /api/tasks/{id}/code-changes/{snapshotId}` reads the diff live from the runner (`currentBranch`, `unavailable`); commit history carries no `authorEmail`. Runner tokens expire after 90 days (`expiresAt` in list/create) and renew while connected (hub `RotateToken`). The runner vets every repository command (`command-trust`: orkka.json on the default branch or `./orkka commands approve <id>`; refusals post a task-thread note), has no remote terminal, streams no tool results, always enforces the agent guard, treats Gemini/Grok/OpenCode as not ready unless `ORKKA_ALLOW_UNGUARDED_PROVIDERS` lists them in `~/.orkka/runner.env`, keeps its token in the OS secret store, and only `./orkka update` (signature-verified) replaces the binary — `connect` just reports a newer release (`--no-update-check` / `ORKKA_NO_UPDATE_CHECK`). Catalogue: RUN-030…042, AIR-007/008, GRD-007/008, HYB-047, NEG-031, MKT-024.
>
> **New surface** (catalogue areas in brackets):
> - Workspaces — one org per job: `GET /api/me/workspaces`, `POST /api/workspaces`, `POST /api/me/active-workspace`, header `X-Orkka-Workspace` (400 malformed / 403 not a member), cookie `orkka_ws`, top-bar WorkspaceSwitcher, `/onboarding` five-step wizard; CLI `./orkka connect --workspace <name>`, `./orkka workspace list|remove|set-identity` [WSP, ONB].
> - Repos by URL — any https/ssh/scp remote (`RepoUrl`), `provider` override + `cliAccount`, embedded credentials refused, `GET /api/projects/{id}/repos/access-check` through the runner [RURL].
> - Forge CLIs — runner Hello reports gh/az/glab (`GET /api/me/runner` → `forgeClis`); landing a remote's PR PUBLISHES it with the developer's CLI (idempotent per head branch); Orkka PRs are its own records (`orkka-local://pull/N`) [FRG].
> - Trackers — `./orkka tracker add jira|ado|list|test|remove` (token in the OS secret store, metadata in `~/.orkka/workspaces/<ws>/trackers.json`), `GET /api/trackers/connections`, `POST /api/trackers/connections/{id}/query` (test/scopes/epics/statuses/preview, `supported:false` when the runner is offline), `GET|PUT|DELETE /api/projects/{id}/tracker`; hub `SyncTrackers` / `ReportTickets` / `AckTransitions`; tickets → `[KEY] title` tasks with `externalKey/Url/Epic`, comments → `ExternalComment` messages, status write-back Started / PR opened / Merged [TSRC, TCLI, TING, TWB].
> - Readiness — no AI CLI signed in ⇒ `WaitingForRunner` + one note; Bash guard blocks `gh auth token`, `az account get-access-token`, `git credential fill`, `~/.ssh`, `.orkka/workspaces`, `trackers.json` [AIR, GRD].


`C:\Projects\orkka\orkka-e2e-tests` exists but is **empty** — greenfield target for the harness.

---

## A. Orkka Local runner / CLI

> **Obsolete since R4 (2026-11-18).** A1–A4 describe the installed `orkka-local` binary and its Docker/hosted modes. Current truth: `src/Orkka/Program.cs` (the verbs), `src/Orkka.LocalRunner/Cli/*` (workspace, tracker, commands, doctor, connect), `.claude/docs/LOCAL.md`. Catalogue: RUN (45), TCLI (19), WSP-013, LWF-001/015/016.

### A1. Source layout
| Component | Path |
|---|---|
| Runner daemon + CLI (single binary `./orkka`) | `C:\Projects\orkka\orkka-api\src\Orkka.LocalRunner\` |
| Entry point / composition root | `C:\Projects\orkka\orkka-api\src\Orkka.LocalRunner\Program.cs` |
| Wire protocol + capabilities + provider keys | `C:\Projects\orkka\orkka-api\src\Orkka.LocalRunner.Contracts\RunnerProtocol.cs` |
| Runner op contracts (git / app-run) | `...\Orkka.LocalRunner.Contracts\RunnerOps.cs`, `AppRunOps.cs` |
| Containerized local API ("local-host") | `C:\Projects\orkka\orkka-api\src\Orkka.Local.Host\` |
| Local UI (Next.js) | `C:\Projects\orkka\orkka-ui\apps\local\` (workspace `@orkka/local-ui`) |
| Hybrid relay (hosted side) | `C:\Projects\orkka\orkka-api\src\Orkka.Worker\Runner\HostedRunnerHub.cs`, `RunnerRelayEndpoints.cs` |
| Shared relay/link plumbing | `C:\Projects\orkka\orkka-api\src\Orkka.Runners.Server\` — `RunnerLink.cs`, `RunnerProxies.cs`, `MultiTenantRunnerLinkProvider.cs`, `RunnerOpBudgets.cs`, `ProjectToolRegistry.cs`, `ChatToolServerMcpAdapter.cs`, `RunnerChunkPump.cs`, `AppRun\AppRunOrchestrator.cs`, `BareCloneFailureCache.cs`, `ProviderKeySource.cs` |
| Hosted runners module (tokens, presence, local-repo endpoints) | `C:\Projects\orkka\orkka-api\src\Modules\Orkka.Modules.Runners\` |
| Distribution repo | `C:\Projects\orkka\orkka-local-cli\` — `README.md`, `install.ps1`, `install.sh` (no workflows, no source) |

### A2. CLI commands (exhaustive — `Program.cs` switch + help text)
| Command | Aliases | Behaviour |
|---|---|---|
| `start` | `up` | Banner → `StackCommands.UpAsync` (writes `~/.orkka/compose.yaml` + `~/.orkka/stack.env`, `docker compose pull`, `up -d`, waits for API health, opens `http://localhost:6750`) → **continues as the runner**. Auto-update checked first. |
| `connect` | — | Hybrid mode. `--host <url> --token orkr_...`. No Docker. Persists to `~/.orkka/hosted.env`. Auto-update checked first. |
| `stop` | `down` | `docker compose down`. Data volumes kept. Instant / offline-safe (no update check). |
| `status` | — | Prints `Containers: N running` \| `not provisioned`, `API: healthy (http://localhost:6752)` \| `unhealthy (<code>)` \| `unreachable`, per-provider state lines, warnings (no provider connected; Claude shim trap). |
| `update` | — | `UpdateCommand.RunAsync` — resolve latest GitHub release, version-compare, download asset, swap binary, then `StackCommands.PullImagesAsync()` (skipped on hybrid-only installs). Prints "Restart with: ./orkka start". |
| `doctor [provider]` | — | Traces ONE agent run through the real invoker spawn path. Optional arg: `claude｜codex｜gemini｜grok｜copilot｜opencode｜ollama`. Default = first authenticated provider, else claude. |
| `uninstall` | — | Interactive: must type `delete`. Removes containers, **data volumes**, images, `{repo}-worktrees` folders, `~/.orkka`. Binary self-delete step is printed (Windows can't self-delete). |
| `help` | `--help`, `-h` | Prints the help block. |
| *(no args)* | — | Runs the runner only (stack assumed already up). |
| unknown | — | `Unknown command '<x>' — try './orkka help'.` → exit 1 |

**Global flags** (`Cli\CliArgs.cs`, stripped before the subcommand switch, any position, case-insensitive):
- `--debug` — full structured logs; also `ORKKA_DEBUG=1`
- `--no-update-check` — skip the "a newer release exists" check on `connect`; also `ORKKA_NO_UPDATE_CHECK=1` (replaced `--no-update` / `ORKKA_NO_AUTO_UPDATE` on 2026-11-17). `connect` never updates the binary; only `./orkka update` does, after verifying the release's ECDSA signature over `SHA256SUMS` and the asset checksum
- `./orkka commands [list|approve <id> [--yes]|revoke <id>]` (alias `command`) — the developer's approvals for repository commands (`~/.orkka/command-approvals.json`); orkka.json on the default branch vouches without approval

There is **no** `disconnect`, `link`, `login`, or `logout` command. Hybrid "unlink" = revoke the token in the web UI (or delete `~/.orkka/hosted.env`).

**Env vars honoured**: `ORKKA_DEBUG`, `ORKKA_NO_UPDATE_CHECK`, `ORKKA_ALLOW_UNGUARDED_PROVIDERS` (runner.env; comma-separated list of gemini/grok/opencode the developer accepts without a command guard), `ORKKA_HOST_URL`, `ORKKA_RUNNER_TOKEN`, `ORKKA_REPO_CACHE`, `ORKKA_PG_PORT`, `ORKKA_IMAGE_TAG`, `ORKKA_HOST_IMAGE`, `ORKKA_UI_IMAGE`, `ORKKA_GEMINI_API_KEY`, `CLAUDE_CODE_OAUTH_TOKEN` — the last several read **from `~/.orkka/stack.env` on every use, never process env** (the startup env scrub is authoritative).

### A4. `./orkka connect` / device pairing
`C:\Projects\orkka\orkka-api\src\Orkka.LocalRunner\Cli\ConnectCommand.cs`
- **No device-code / OAuth pairing flow.** Pairing = a **manually pasted personal access token**, prefix `orkr_` (validated client-side before any network call).
- Config file `~/.orkka/hosted.env`: `ORKKA_HOSTED_URL=` / `ORKKA_HOSTED_TOKEN=`, written on every successful resolve; a bare `./orkka connect` reconnects from it. `IsConfigured()` is what `update` uses to skip image pulls on hybrid-only machines.
- Usage/error text names the UI location: *"Generate a token in the Orkka web app under Settings → My Runner."*
- Relay URL: `wss://runner.orkka.ai/hubs/runner` (`NEXT_PUBLIC_RUNNER_HUB_URL`).

**Pairing UI (hosted):** `C:\Projects\orkka\orkka-ui\apps\remote\app\[locale]\(dashboard)\settings\runner\page.tsx` + `RunnerClient.tsx` (630 lines). Controls:
- **Connect your machine** card — step1 "Install the Orkka runner CLI" + Installation guide link, step2 generate a token, step3 the ready-to-paste `./orkka connect --host … --token …` command with a **Copy** button (`copy` / `copied`).
- (Removed 2026-11-17: the "Run my tasks on my machine" preference toggle — every task runs on its owner's runner.) Token rows show "Expires {date} · renews automatically while connected" and an "Expired" badge.
- **Runner status** card — Online/Offline Badge, Machine, Version, Last seen, **Update available** note, **Git push identity** (`Pushes as {account}`, shared-across-all-repos vs per-repo, or "No GitHub credential detected on this machine"), **AI providers** chips with four states: `ready` / `not signed in` / `not installed` / `signed in, but CLI not found on the runner's PATH`; plus `readySummary`, `readyNone`, `copilotNote` (Copilot spends premium requests, never credits).
- **Runner tokens** card — name Input ("e.g. Work laptop"), **Generate token**, one-time reveal Modal (`oneTimeWarning` "Copy this token now", `connectCommand`, Copy, Done), per-token rows with Created/Last used/Never used, **Active**/**Revoked** badges, **Revoke** + ConfirmDialog ("Any runner using it will disconnect within a minute").

**Is there an org-level runners/machines page?** Yes — a section, not a page: `C:\Projects\orkka\orkka-ui\apps\remote\components\runner\OrgRunnersSection.tsx`, rendered by `settings\organization\page.tsx`. i18n `dashboard.orgRunners`: *"Runners / Machines your members have connected to run tasks with their own AI subscriptions"*, per-member Online now / Last seen {time} / Never connected / No providers ready / Update available / You / Unknown member. Existing unit test: `apps\remote\__tests__\pages\org-runners.test.tsx`.

**Endpoints** (`Modules\Orkka.Modules.Runners\Features\`):
- `GET /api/me/runner-tokens`, `POST /api/me/runner-tokens`, `DELETE /api/me/runner-tokens/{tokenId:ulid}` — `RunnerTokens\RunnerTokenEndpoints.cs`
- `GET /api/me/runner` (own presence), `GET /api/org/runners` — `Presence\RunnerPresenceEndpoints.cs`
- `GET /api/projects/{projectId}/repos/access-check` — `Presence\RepoAccessCheckEndpoint.cs`
- Hub auth `Infrastructure\Services\RunnerHubAuth.cs`; token hashing `RunnerTokenValues.cs`; presence writes `RunnerPresenceWriter.cs`; domain `Domain\RunnerToken.cs`, `RunnerPresence.cs`, `RunnerProviderSnapshot.cs`, routing/coverage `PublicApi\RunnerCoverageResolver` (`IRunnerCoverageResolver`), `RunnerFleet.cs`, `RunnerRelayLocator.cs`, `RelayLocalRepoGitOps.cs`, `RelayUserRunnerUtility.cs`
- Migrations: `InitialRunners`, `AddRunnerGitIdentity`, `AddRunnerProviderSnapshots`

### A5. Runner presence / capability list
Advertised in `RunnerHello.Capabilities` — `Orkka.LocalRunner\RunnerConnection.cs:222-244`, defined in `Orkka.LocalRunner.Contracts\RunnerProtocol.cs:213-345`:

`utility-provider` · `agent-resume` · `context-files` · `cli-resume` · `hybrid` · `editor` · `command-trust` · `base-sync` · `merge-verify` · `working-tree-diff` · `app-run` · `app-run-prepare` · `project-tools` · `chat-tools` · `managed-git` · `task-cancel` · `local-folders` · `repo-access-check` · `platform-git`

Every doc-comment states the **degradation when absent** — a ready-made negative-test matrix. Highlights:
- no `app-run` → UI **hides** the Run button rather than failing the call; `app-run-prepare` adds `.env` from `.env.example`, `InstallCommand`, Docker probe, dependency port readiness, `StopCommand`, `Ready` flag
- no `local-folders` → UI hides "add a local folder"
- no `editor` → UI hides "open in editor"
- no `repo-access-check` → UI hides the preflight
- no `command-trust` → the host sends no setup/check/build/run-target command (retired: `setup-command`, `worktree-command`, `terminal`, `managed-git`)
- no `platform-git` → host **refuses** git work rather than degrading
- no `hybrid` → the multi-tenant hosted hub **refuses the runner**
- no `agent-resume` → host must NOT slim prompts (cold runs)
- no `chat-tools` → fallback to fenced ```document JSON blocks, which hit the model output ceiling on a 26-document consolidation (2026-09-07)
- remote terminal sessions were deleted (2026-11-17); app-run output still rides `StreamRunChunks` as `Pty` chunks. Tool-result chunks are dropped on the machine and file bodies stripped from tool calls (`RunnerChunkPrivacy.cs`)

`RunnerHello` also carries `DaemonVersion`, `Providers`, `GitIdentity` (`RunnerGitIdentity`).

### A6. Linking a local folder to a hosted project (hybrid)
Endpoints — `Modules\Orkka.Modules.Runners\Features\LocalRepos\LocalRepoEndpoints.cs`:
| Route | Purpose |
|---|---|
| `POST /api/runners/fs/browse` | Relayed directory listing → `BrowseEntryResponse(Name, FullPath, IsGitRepo)` |
| `POST /api/runners/fs/init-repo` | `git init` a new folder on the user's machine (`InitLocalRepoRequestDto(ParentPath, Name)`) |
| `POST /api/projects/{projectId}/repos/local` | **Link a PC folder** as a project repo (`LinkLocalRepoRequest(Path)`) |
| `POST /api/projects/{projectId}/repos/{repoId:ulid}/attach-remote` | `AttachRemoteRequestDto(RemoteUrl?, HostedByOrkka = false)` — **"Let Orkka host it"** when `HostedByOrkka = true` |
| `GET /api/projects/{projectId}/repos/{repoId:ulid}/sync-status` | ahead/behind vs origin |
| `POST /api/projects/{projectId}/repos/{repoId:ulid}/push` | push the linked folder (`PushRepoRequest(Branch?)`) |

Publishing logic: `Features\LocalRepos\RepoPublishing.cs`. Runner-side ops: `ListDirectory` / `ValidateRepoPath` / `InitLocalRepo` / `InitRepoInPlace` / `AttachRemote` in `Orkka.LocalRunner\RunnerOperations.cs` (2011 lines) — includes build-system marker detection, build-output folder detection, and `.gitignore` seeding on git-init.

UI — `C:\Projects\orkka\orkka-ui\apps\remote\app\[locale]\(dashboard)\p\[projectId]\repos\`:
- `page.tsx` → `GitHubRepoConnect.tsx`, `LocalFolderConnect.tsx`, `RepoFileBrowser.tsx`, `LocalRepoRemotePanel.tsx`, `RepoCloneButton.tsx`, `RepoDeleteButton.tsx`, `RepoSetupCommandButton.tsx`
- `apps\remote\components\repos\LocalFolderPickerModal.tsx` — **Browse…** (`api.runnerFs.browse`), or paste an absolute path, or create a new repo (`api.runnerFs.initRepo`)
- `LocalRepoRemotePanel.tsx` — two mutually exclusive publish paths:
  - **"Let Orkka host it"** button → `attachRemote(projectId, repoId, null, token, true)` → private repo created under `orkka-repos` and kept in sync; *"No GitHub account or setup needed."*
  - **"Or connect a GitHub repository you created:"** → URL input + **Connect & push** → `attachRemote(..., url, ...)`; hint `createEmptyHint`: *"Create an empty repository on GitHub first — no README, .gitignore or licence — then paste its URL here."*
  - then **Push** with `aheadBy` ("{n} commits to push on {branch}") / `upToDate`
  - **Load-bearing constraint to test** (file header): Orkka authenticates as a GitHub **App installation**, and an installation token **cannot create a repository on a personal account — only inside an organization**. That is exactly why "Let Orkka host it" (org `orkka-repos`) exists and why the BYO path demands a pre-created empty repo. The folder stays the working copy throughout; the remote is where it is published, not where agents work.
- i18n keys `dashboard.localRepos.*` in `C:\Projects\orkka\orkka-ui\packages\i18n\messages\en\dashboard.json`: `browse`, `browseHint`, `orPastePath`, `addLocalRepository`, `addRepository`, `add`, `adding`, `hostedHint`, `startRunnerHint`, `runnerOfflineHint` ("Start Orkka on that machine (`./orkka connect`) to manage this folder."), `connectHint`, `createEmptyHint`, `hostByOrkka`, `hostByOrkkaHint`, `hosting`, `orConnectYourOwn`, `connectAndPush`, `connecting`, `connectFailed`, `pushedTo`, `pushed`, `pushFailed`, `push`, `pushing`, `aheadBy`, `upToDate`

### A7. How hybrid runs execute
- Repo cache base: `ORKKA_REPO_CACHE` ?? `~/.orkka/repos`, bound to config key `Worker:RepoBasePath`.
- **Bare mirror** per repo whose **origin IS the user's own repo**; agent branches `orkka/task-…` land directly there; merging a PR squash-merges inside the user's repo (needs a clean tree with base checked out).
- Long-lived default-branch checkout at `<repoDir>/worktrees/main` — a git worktree of the bare mirror, fetched `--ff-only` (`RunnerOperations.cs:1079-1112`).
- **Dev-phase task worktrees live next to the user's repo**: `C:\Code\my-app` → `C:\Code\my-app-worktrees\task-…`, removed after merge. Age-based GC must run on the runner's disk: `Orkka.LocalRunner\WorktreeGcService.cs`.
- **Chat workspace** (read-only per-repo worktrees): `{base}/{orgId}/chat-workspace[/{projectId}]` — `RunnerOperations.cs:1882-1883`; host side `Modules\Orkka.Modules.Agents\Features\AgentChat\AgentChatService.cs:69` and `HybridChatAgentInvoker.cs:59` → `POST {relay}/internal/runner/chat-workspace` (`Orkka.Worker\Runner\RunnerRelayEndpoints.cs:173`).
- Merge path: scratch clone at `{base}/merge-scratch/{guid}` + push into the user's repo with `receive.denyCurrentBranch=updateInstead` — ref update when base isn't checked out, fast-forward of the working tree when it is checked out and clean, **refusal with a reason when dirty** (`orkka-api\.claude\CLAUDE.md:197`).
- Startup hardening (`Program.cs`): deletes (not blanks) every `CLAUDECODE` / `CLAUDE_CODE_*` / `CLAUDE_AGENT_SDK_*` marker plus everything `AgentEnvironment.IsSensitive`; sets `GIT_TERMINAL_PROMPT=0`, `GCM_INTERACTIVE=never`, `GIT_LFS_SKIP_SMUDGE=1` (LFS files reach agents as pointers); `GeminiDotenvGuard.Refresh(repoBasePath)` writes a controlled `.env` at the cache root so the gemini CLI's dotenv walk-up cannot reach `stack.env` (it previously leaked the runner token and Postgres password into agent processes).
- Agent guard on local: `BashGuardEnabled = false`, `BypassCliPermissions = true`, `MaxToolCalls = 500` — all still config-overridable via `AgentGuard__*` / `AgentLimits__*`.
- Interactive approvals: `RunnerApprovalGate.cs` + `RunnerHostLink.cs` (the agent's Bash hook runs on the runner; the human decides in the browser via approval cards).
- Other runner subsystems worth their own cases: `Commands\` (`CommandTrust`, `CommandApprovalStore`), `Terminal\` PTY sessions (app-run only; `TerminalSessionManager` deleted), `Apps\` (`AppRunManager`, `AppRunPreparer`, `DockerProbe`, `PortInspector`), `EditorResolver.cs`, `GhCliCredentials.cs`, `BundledGit.cs`, `RepoAccessProbing.cs`, `HostProjectToolServer.cs`, `RunnerRunRegistry.cs`, `ProviderAuthOverrides.cs`.

### A8. Provider detection & `doctor`
- Providers (`RunnerProtocol.cs:148-171`): `claude`, `codex`, `gemini`, `copilot`, `ollama`, `grok`, `opencode`. `All = [claude, codex, gemini, copilot, ollama, grok, opencode]`; `DiscoversModels = [ollama, opencode]` — these report model lists from the machine instead of the static catalog. Ollama "ready" = server reachable **AND** codex CLI installed; OpenCode "ready" = CLI installed **AND** ≥1 provider authenticated; grok accepts a plain `XAI_API_KEY` so a saved Settings key counts as auth.
- `ProviderDetection.cs` (879 lines) — hint constants `CopilotLoginHint`, `GrokUnverifiedHint`, `GeminiApiKeyHint`, `ClaudeSetupTokenHint`; `DetectClaudeShimTrap()`; `CopilotTokenEnvVars`; opencode `auth.json` probe. Run-time evidence is layered on top via `ProviderAuthOverrides` / `IProviderAuthFeedback` (invokers report auth-shaped failures and clean completions), and the runner re-Hellos on change.
- `doctor` stages (`Cli\DoctorCommand.cs`): OS / Machine / PID → chosen provider (validates the arg, errors with the full list) → readiness detail for model-discovering providers → PATH resolution of the spawn binary (`<cli>.cmd`, `.exe`, `.ps1`, bare on Windows; note **ollama spawns `codex`**) → Claude shim-trap warning → auth mode (`~/.claude` credentials vs `CLAUDE_CODE_OAUTH_TOKEN`) → **names only** of env vars blanked for the child → one real minimal run through the same invoker chat/tasks use, with a `DenyingAgentApprovalGate`.
- Auth workaround worth a case: `claude setup-token` → `CLAUDE_CODE_OAUTH_TOKEN=<token>` in `~/.orkka/stack.env`; read **from the file per spawn**, so it takes effect without a restart. Root cause: parallel `claude` children race the single-use refresh token in the shared credentials file and log the user out.

## B. Admin UI — `C:\Projects\orkka\orkka-admin-ui`

- **Stack:** Next.js 16.1.6, React 19.2.3, `@clerk/nextjs` ^7, `next-intl` ^4.8, Tailwind 4, recharts ^3.8, framer-motion. Dev/start port **3100**. Scripts: `dev｜build｜start｜lint` — **no test script, no tests, no workflows**.
- **Prod URL:** `https://admin.orkka.ai` — Vercel project `orkka-admin-ui`, CNAME `cname.vercel-dns.com` (`orkka-infra\GO_LIVE.md:98,223`), allow-listed in `orkka-infra\infra\k8s\helm\orkka\values-prod.yaml:119,127`. API base from `NEXT_PUBLIC_API_URL` (`lib/api.ts:30`) → `https://api.orkka.ai`.
- **Auth (two layers):**
  1. `proxy.ts` — `clerkMiddleware` + `next-intl` middleware; everything except `/:locale/sign-in(.*)` and `/sign-in(.*)` gets `auth.protect()`. **There is no sign-up route.**
  2. `app/[locale]/(admin)/layout.tsx` — server-side `GET /api/admin/me`; renders the **Access denied** card unless `isPlatformAdmin`, and **fails closed** if the API is unreachable or 5xx. Admin accounts are ordinary Orkka Clerk users flagged in the API's `PlatformAdmins` config.

### Pages / routes (`app/[locale]/(admin)/`) and their controls
| Route | Nav label (short) | Components / controls | API calls |
|---|---|---|---|
| `/` | Dashboard (Home) | `MonthPicker` (`?month=`), `FinanceCharts`, `FinancialHealthPanel`, `PlatformPanels` → `PlatformPulse`, `AgentActivityPanel`, `AudiencePanel`, `ModelMixPanel`, `MoneyAtRiskPanel`; `DataCard/DataCell/DataGrid/DataList` | `GET /api/admin/finance/summary?months=12`, `GET /api/admin/platform/summary`, `GET /api/admin/finance/analysis`, `GET /api/admin/finance/orgs{?from,to}`, `GET /api/admin/finance/orgs/{orgId}/projects{?from,to}` |
| `/organizations` | Organizations (Orgs) | `OrgTable` (count header) | `GET /api/admin/orgs` |
| `/organizations/[orgId]` | — | `LiveOrgCredits`, **GrantCreditsButton**, **GiftPlanButton**, **SuspendButton** (suspend/unsuspend), `ProjectSpendTable` with per-project **markup rate** | `GET /api/admin/orgs/{id}`, `POST .../suspend`, `POST .../unsuspend`, `POST .../plan` (→ `{plan, isGifted}`), `GET .../credits`, `POST .../credits` (→ `{freeCredits}`), `POST /api/admin/projects/{projectId}/markup-rate` |
| `/costs` | Infra Costs (Costs) | `InfraCostManager` — add / edit / delete cost items, current-month total; feeds net income on the dashboard | `GET|POST /api/admin/infra-costs`, `PUT|DELETE /api/admin/infra-costs/{id}` |
| `/models` | Models | `SyncActivity` (sync event feed, **approve** / **dismiss**, "3 h ago" via server `requestNow()`), `ModelManager` (list, create, edit, **enable/disable** toggle, delete, **reset rate**, **tier defaults**, **Sync now**, "new" badges) | `GET|POST /api/admin/models`, `PUT /api/admin/models/{id}`, `PUT .../{id}/enabled`, `DELETE .../{id}`, `PUT /api/admin/models/tier-defaults`, `POST .../{id}/reset-rate`, `POST /api/admin/models/sync`, `GET /api/admin/models/sync/events?take=`, `POST .../events/{id}/approve`, `POST .../events/{id}/dismiss` |
| `/git` | Git Reliability (Git) | `GitReliability` — window selector **1｜7｜30 days** (`?days=`, default 7); every git operation by kind and by where it ran; failures show the classified code + the failing command with credentials redacted | `GET /api/admin/git-operations/summary?days=` |
| `/bug-reports` | Bug Reports (Bugs) | `BugReportTable`; header shows total + open counts | `GET /api/admin/bug-reports` |
| `/bug-reports/[id]` | — | `BugReportBadges`, `BugReportImageGallery`, **BugReportStatusActions** | `GET /api/admin/bug-reports/{id}`, `PUT .../{id}/status` |
| `/settings` | Settings | `RateSettings` (default markup rate), `ManagedFeeSettings` | `GET /api/admin/settings/rates`, `PUT /api/admin/settings/rates`, `PUT /api/admin/settings/managed-fee` |
| `/sign-in/[[...sign-in]]` | — | Clerk `<SignIn>`, themed by `lib/clerk-theme.ts` | — |

Shell: `components/layout/AdminShell.tsx` — desktop sidebar + **fixed-column mobile bottom nav** (6 full labels don't fit 360px, hence `short`). Shared UI kit: `components/ui/` (Avatar, Badge, Button, DataList, Dropdown, EmptyState, GlassCard, Input, Modal, Select, Skeleton, Textarea, Toggle, Tooltip). Orca animation set: `components/animations/` (Empty, Error, Loading, Searching, Success, Waiting, Working). Locale routing: `i18n/`, `messages/`.

---

## C. Mobile app — `C:\Projects\orkka\orkka-mobile` (Expo)

- **Stack:** Expo ~57.0.20, React Native 0.86.3, React 19.2.3, expo-router ~57, `@clerk/clerk-expo`, TanStack Query v5 (+ AsyncStorage persister), `@microsoft/signalr` ^10, `react-native-mmkv`, Reanimated 4.5, `@sentry/react-native`, `@shopify/flash-list`, `react-native-webview`, `react-native-web` ~0.21, `use-intl`, `zustand`.
- **Platform targets:** iOS bundle `ai.orkka.mobile`, Android package `ai.orkka.mobile`, URL scheme `orkka`, EAS owner `orkka-ai`, projectId `41683af4-5a05-4ff7-be44-bd0df2fd28d8`, 4 https deep-link intent filters. `app.json` has a `web` block. `app.config.ts` only adds `google-services.json` (FCM V1 on Android; absent ⇒ PushProvider degrades to a no-op).
  EAS profiles (`eas.json`): `development` (dev client, internal, APK, no iOS simulator), `preview` (internal, APK), `production` (autoIncrement, OTA channel). All three point at `EXPO_PUBLIC_API_URL=https://api.orkka.ai`, `EXPO_PUBLIC_CLERK_JWT_TEMPLATE=orkka-mobile`, node 24.18.0. Submit: iOS `ascAppId` is still the placeholder `REPLACE_WITH_APP_STORE_CONNECT_APP_ID`; Android track `internal`, releaseStatus `draft`.
- **⚠️ No RevenueCat, no IAP.** No `react-native-purchases`, no StoreKit, no Google Play Billing, no IAP config plugin anywhere in `package.json`, `app.json` or `src/`. The only billing surface is read-only: `src/components/billing/CreditsPill.tsx` (live credits via `useCredits`, taps through to `AccountSettingsScreen`). Purchases live in the web app (`orkka-ui/apps/remote` carries `@stripe/react-stripe-js`). Treat "IAP via RevenueCat" as unbuilt — confirm with the product owner before writing cases.
- **Routes** (`src/app/`, expo-router):
  - Auth: `(auth)/sign-in`, `sign-up`, `forgot-password`, `sso-callback`
  - Root: `index`, `+not-found`, `+native-intent`, `invite/[token]`
  - App: `(app)/index`, `dashboard`, `inbox`, `notifications`, `onboarding`, `settings/index`
  - **Home grid tabs:** `(app)/(home)/index`, `(home)/alerts`, `(home)/settings`
  - Project scope `(app)/p/[projectId]/`: tabs `(tabs)/index｜board｜alerts｜settings`; `brief`, `design`, `setup`, `members`; `documents/index｜[documentId]`; `repos/index｜[repoId]`; `roadmap/index｜[milestoneId]`; `team/index｜[agentId]`; `tasks/index｜new｜[taskId]/index｜review｜merge`
- **Screens** (`src/screens/`, 22): AccountSettings, **AgentChat**, Alerts, **Board**, Brief, Design, Document, Documents, Invite, Members, Merge, Milestone, Onboarding, PrReview, ProjectSettings, Repos, Roadmap, Setup, TaskCreate, TaskDetail, TaskList, Team.
- **Component groups** (`src/components/`): auth, billing, board, chat, nav, pr, project, providers (incl. `PushProvider`), push, task, ui. Plus `src/hooks`, `src/lib` (`push.ts`, `routes.ts`), `src/i18n/mobile`, `src/theme`.
- **Testable without a device? Yes, two ways:**
  1. **jest** — `npm test` (`jest-expo` ~57 + `@testing-library/react-native` ^14); 12 suites in `__tests__/`: `board-state`, `core-integration`, `deployment-card`, `i18n-parity`, `push`, `questions-card`, `routes`, `sign-out`, `signalr-provider`, `start-session`, `task-create`, `task-detail`. Config: `jest.config.js`, `jest.setup.js`, `jest.resolver.js`.
  2. **Expo web** — `npm run web` (`expo start --web`); `react-native-web` and the `web` block are present, so it is browser-drivable with Playwright. **Not exercised in CI**, so expect rough edges (webview, push, secure-store, MMKV).
- **CI** `.github/workflows/ci.yml`: checkout with recursive submodules (`vendor/orkka-ui` is a private repo needing `SUBMODULE_TOKEN`, a fine-grained PAT with Contents:read) → node 24 → `npm ci` → `npm run typecheck` → `npm run lint` → `npm test -- --ci` → `npx expo export --platform android` (proves the Metro `@/*` resolver still bundles the shared core). **No browser/e2e step.**
- **Scanning note:** `orkka-mobile/vendor/orkka-ui` is a submodule copy of `orkka-ui` — exclude it or you will double-count every finding.

---

## D. Existing E2E / browser tooling

**Verdict: none exists.** A full-workspace scan (including `package.json`, `.ts/.tsx`, `.cs`, `.yml/.yaml`, `.json`, `.md`) returned **zero** `playwright.config.*`, zero `@playwright/test`, zero Cypress, zero puppeteer, and zero `e2e`/`smoke` npm scripts. Every Playwright hit is either the **QA agent's runtime browser** or model-catalog/docs prose.

| Repo | Test tooling | Scripts |
|---|---|---|
| `orkka-ui/apps/remote` (`orkka-web`) | **vitest** — ~40+ suites in `apps/remote/__tests__/` (incl. `pages/org-runners.test.tsx`, `components/code-view-*`) | `test` (`vitest run`), `test:watch` |
| `orkka-ui` (root workspace) | — | `dev｜build｜start｜lint｜test` (delegate to `orkka-web`), `dev:local｜build:local` (`@orkka/local-ui`) |
| `orkka-admin-ui` | **none** | `dev｜build｜start｜lint` |
| `orkka-mobile` | jest + RNTL (12 suites) | `test` |
| `orkka-api` | xUnit — `tests/Orkka.Tests`, `tests/Orkka.IntegrationTests` | `dotnet test` |

**GitHub workflows (all repos):**
- `orkka-api/.github/workflows/ci.yml` — jobs: *Build & Unit Tests* (`dotnet test tests/Orkka.Tests`, trx artifact) · *Integration Tests* (`tests/Orkka.IntegrationTests`, trx artifact) · *Security Scan* (`dotnet list package --vulnerable --include-transitive`, fails on hit) · *Dockerfile Lint* (hadolint on `Dockerfile.api`, `.worker`, `.migrator`). **No browser step.**
- `orkka-api`: also `cd.yml`, `worker-base.yml`, `release-local.yml`, `release-local-auto.yml`
- `orkka-ui`: `local-release-trigger.yml`, `release-local.yml`
- `orkka-mobile`: `ci.yml` (see §C)
- `orkka-infra`: `terraform.yml`, `monitoring.yml`, `acr-cleanup.yml`
- `orkka-admin-ui`, `orkka-local-cli`: **no workflows at all**

### Playwright MCP (QA agent) — the reusable precedent
`C:\Projects\orkka\orkka-api\src\Orkka.AgentRuntime\Agents\ClaudeAgentInvoker.cs`
- `:339-340` — server registered under the name **`playwright`**
- `:492-493` — Windows: `cmd /c npx @playwright/mcp@latest --headless --isolated`; POSIX: `npx @playwright/mcp@latest --headless --isolated`
- `:495` — `builder.AddMcpServer(PlaywrightServerName, new McpStdioServerConfig { … })`
- `:110-115` — QA allowed tools: `["Read", "Glob", "Grep", "Bash", "mcp__playwright", "TodoWrite", "BashOutput", "KillShell"]`. The **server-wide `mcp__playwright` form** is deliberate — it avoids chasing upstream tool renames.
- `:117` — API-mode QA (`AgentRequest.ApiTesting`) is identical **minus playwright** (a JSON API has nothing to drive)
- `:659` — MCP init-status assertion includes the server

Supporting pieces:
- `C:\Projects\orkka\orkka-api\infra\docker\Dockerfile.worker` — `:1` header "Playwright QA"; `:50-60` `runtime-base` installs git + Node 22 (+ pnpm/yarn via corepack); `:72-74` `npx playwright install --with-deps chromium` and `npm install -g @playwright/mcp`; `:267` notes no Android emulator (no KVM in AKS pods)
- `C:\Projects\orkka\orkka-api\src\Orkka.AgentRuntime\Agents\AgentSandboxOptions.cs:30` — local port binding allowed for QA/Playwright dev servers
- `C:\Projects\orkka\orkka-api\src\Modules\Orkka.Modules.Execution\Orchestration\TaskOrchestrator.cs` — `:1774` Testing phase; `:93` & `:1715` QA phase **skipped** for local models that cannot drive Playwright MCP; `:4282` PM/QA/Code-Reviewer keep their own models; `:6361` the instruction to drive the localhost URL with `browser_navigate` / `browser_click` / `browser_type` / `browser_snapshot`, preferring accessibility snapshots over screenshots
- `C:\Projects\orkka\orkka-api\src\Modules\Orkka.Modules.Execution\Orchestration\QaTargetMode.cs` — web vs API target selection
- `C:\Projects\orkka\orkka-api\src\Modules\Orkka.Modules.Agents\Domain\DefaultPrompts.cs:677-701` — the QA agent prompt
- `C:\Projects\orkka\orkka-infra\infra\k8s\helm\orkka\values.yaml:170` — knob signalling a local Ollama model that cannot drive Playwright MCP
- Expectation sources to mine: `C:\Projects\orkka\orkka-api\tests\Orkka.Tests\Worker\QaPromptTests.cs`, `ClaudeMcpInitStatusTests.cs`

### `C:\Projects\orkka\.claude\settings.local.json`
```json
{ "permissions": { "allow": ["Bash(python -c ' *)"] } }
```
**No MCP servers and no `additionalDirectories` at the workspace root.** A Playwright MCP server entry plus an allowlist would have to be added here (or in a new `orkka-e2e-tests/.claude/`).

Other Claude configs found:
- `C:\Projects\orkka\.claude\launch.json` — named dev launchers: `orkka-local-ui` (`orkka-ui/apps/local`, `npm run dev -- -p 6750`, port **6750**) and `remote-marketing` (`orkka-ui/apps/remote`, `npx next dev -p 3979`, port **3979**)
- `C:\Projects\orkka\orkka-api\.claude\settings.json` — stale `c:\Code\orkka\**` read allows + `additionalDirectories`, `WebSearch`, `Bash(where claude:*)`
- `C:\Projects\orkka\orkka-ui\.claude\settings.json` — allows `Bash(npx vitest:*)`, `Bash(node -e ":*)`; registers the `vercel` marketplace and enables `vercel-plugin@vercel`
- `C:\Projects\orkka\orkka-mobile\.claude\settings.json` — enables `expo@claude-plugins-official`
- Per-repo agent docs worth reading before authoring cases: `orkka-api\.claude\CLAUDE.md` + `.claude\docs\{AGENTS,BACKEND,BILLING,DATABASE,FRONTEND,INFRA,PROJECTS,SECURITY}.md`; same doc set mirrored under `orkka-ui\.claude\docs\` and `orkka-infra\.claude\docs\`

---

## Gaps and risks to plan around
1. **No e2e harness exists anywhere** — `orkka-e2e-tests` is empty; there is nothing to extend. The QA agent's `@playwright/mcp@latest --headless --isolated` stdio registration in `ClaudeAgentInvoker.cs` is the only precedent in the codebase and is directly reusable as a config template.
2. **No MCP server is configured for this workspace.** `C:\Projects\orkka\.claude\settings.local.json` contains exactly one Bash permission and nothing else.
3. **`orkka-admin-ui` has zero tests and zero CI** — highest-value greenfield target, and its two-layer auth (Clerk middleware + `isPlatformAdmin` fail-closed) is itself a priority test case.
4. **Mobile has no RevenueCat / IAP** despite the brief — confirm whether that is planned rather than shipped before writing purchase cases. Expo web exists and is browser-drivable but is unexercised by CI.
5. **Orkka Local very likely inherits the Free-plan cap** (1 project / 1 seat / 2 repos per project) with no `Plans` override in `Orkka.Local.Host\appsettings.json` — verify against a running local stack before filing it as a bug, but it contradicts the README's own onboarding flow.
6. **`orkka-infra\.claude\docs\INFRA.md` and `orkka-api\.claude\docs\INFRA.md` are stale** (pre-rename `crewdeck*` names, retired `AZURE_CREDENTIALS`) — always cross-check infra assertions against the four root-level runbooks.
7. **`orkka-mobile/vendor/orkka-ui`** is a submodule duplicate of `orkka-ui`; exclude it from every scan to avoid phantom findings.