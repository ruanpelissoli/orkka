---
area: RUN
title: Runner — My Runner settings, tokens, presence and the ./orkka CLI
modes-default: [hybrid-repo]
---

## RUN-001 Render the Machine settings page
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/settings/machine
  - orkka-api:GET /api/me/runner
  - orkka-api:GET /api/me/runner-tokens
  - i18n:runnerPage
since: 2026-09-15
```
**Preconditions**: Signed in; runner online.
**Steps**:
1. Open `/settings/machine` (Settings → "Machine").
**Expected**:
- Heading "Machine" with "The runner on this machine: the AI CLIs and forge CLIs it found, and the git identity it pushes with."; cards "Runner status", "Pull requests" (forge CLIs, FRG-010), "Repository commands" (`data-testid="pending-commands"`, with `./orkka commands` and `./orkka commands approve <id>`) and "Runner tokens".
- The GETs each return 200. There is no "Connect your machine" card and no token generator: `./orkka start` mints the runner's tokens (LWF-016), and no "Run my tasks on my machine" card (`/api/me/execution-preference` is gone, NEG-031).

## RUN-003 Validate the token name
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/me/runner-tokens
since: 2026-09-15
```
**Preconditions**: Owner API key or session.
**Steps**:
1. `POST /api/me/runner-tokens {name:"   "}`.
2. `POST /api/me/runner-tokens {name:"<101 characters>"}`.
**Expected**:
- Both 400 "Give the token a name between 1 and 100 characters (e.g. "Work laptop")."; no token created.
**Notes**: There is no per-member cap on runner tokens (unlike the 10 API keys).

## RUN-004 List token rows with created, last-used and status badges
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/settings/machine
  - orkka-api:GET /api/me/runner-tokens
  - i18n:runnerPage
since: 2026-09-15
```
**Preconditions**: The token `./orkka start` minted for this machine; one more from `POST /api/me/runner-tokens {name:"e2e-<stamp>"}`.
**Steps**:
1. Read the "Runner tokens" card.
**Expected**:
- Each row shows the name, "Created {date}", and either "Last used {time}" (with the machine name from `lastMachineName`) or "Never used"; badge "Active"; a "Revoke" button.
- Active rows also show "Expires {date} · renews automatically while connected".
- With zero tokens the card reads "No runner tokens yet — `./orkka start` creates one."

## RUN-005 Revoke a token and disconnect the runner that used it
```yaml
priority: P0
surface: [ui, api, cli]
automation: planned
refs:
  - orkka-ui:/settings/machine
  - orkka-api:DELETE /api/me/runner-tokens/{tokenId}
  - orkka-api:GET /api/me/runner
  - orkka-api:GET /api/audit
  - i18n:runnerPage
since: 2026-09-15
```
**Preconditions**: Runner connected with token T (dedicated to this case).
**Steps**:
1. Click "Revoke" on T; read the dialog; click Cancel.
2. Click "Revoke" again; confirm.
3. Wait up to 90 s; `GET /api/me/runner`.
**Expected**:
- Dialog text "Revoke this token? Any runner using it will disconnect within a minute. This cannot be undone."; Cancel sends nothing.
- `DELETE /api/me/runner-tokens/{id}` → 204; the row badge becomes "Revoked" and its Revoke button disappears; audit lists `RunnerTokenRevoked`.
- Step 3: the daemon's connection is dropped and reconnect attempts are refused; presence turns `online:false`; the CLI logs an authentication failure.
**Notes**: Reconnect with a fresh token afterwards.

## RUN-008 Show machine, version and last-seen in the Runner status card while online
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/settings/machine
  - orkka-api:GET /api/me/runner
  - i18n:runnerPage
since: 2026-09-15
```
**Preconditions**: Runner online.
**Steps**:
1. `GET /api/me/runner`; compare with the card.
**Expected**:
- Response `{online:true, machineName, daemonVersion, lastSeenAt, providers:[…], gitIdentity, capabilities:[…]}`; `capabilities` contains `hybrid`, `app-run`, `local-folders`, `editor`, `managed-git`, `platform-git`.
- Card badge "Online"; "Machine" = `machineName` (this PC's hostname), "Version" = `daemonVersion` (the build's `OrkkaVersion`), "Last seen" is a relative time under a minute.

## RUN-009 Show the never-connected message before the runner's first connect
```yaml
priority: P2
surface: [ui, api]
automation: manual
refs:
  - orkka-ui:/settings/machine
  - orkka-api:GET /api/me/runner
  - i18n:runnerPage
since: 2026-09-15
```
**Preconditions**: A fresh workspace whose runner profile has not connected yet (created seconds ago, before `./orkka start` picks it up), or a sandbox started with the runner held back.
**Steps**:
1. Open `/settings/machine`.
**Expected**:
- `GET /api/me/runner` → `online:false` with null machine/version; card text "The runner has not connected yet — `./orkka start` launches it."
**Notes**: timing-dependent — manual.

## RUN-011 Show the git push identity detected on this machine
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/settings/machine
  - orkka-api:GET /api/me/runner
  - i18n:runnerPage
since: 2026-09-15
```
**Preconditions**: Runner online; `gh auth status` shows `<your-account>` and git credential manager holds the same account.
**Steps**:
1. Read "Git push identity".
**Expected**:
- `gitIdentity.defaultAccount:"<your-account>"`; card "Pushes as <your-account>" followed by "shared across all repos" (or "per repo" when `gitIdentity.repos` carries per-remote accounts).
- Variant with no credential (manual: sign out of gh and clear the credential store): amber "No GitHub credential detected on this machine".

## RUN-012 Render the four AI provider chip states and the no-cost summary
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/settings/machine
  - orkka-api:GET /api/me/runner
  - i18n:runnerPage
since: 2026-09-15
```
**Preconditions**: Runner online with Claude signed in, Codex CLI installed but not signed in, Gemini CLI absent.
**Steps**:
1. Read "AI providers".
**Expected**:
- Chip states derive from `providers[].cliFound/authenticated`: Claude "ready"; Codex "not signed in"; Gemini "not installed" (dimmed); a provider with `authenticated:true, cliFound:false` reads "signed in, but CLI not found on the runner's PATH"; a `detail` string is listed under the chips.
- Summary "Runs at no cost: Claude"; when nothing is ready "No provider is signed in on this runner yet — tasks will wait until you sign in to one."; Copilot ready adds "Copilot models spend premium requests on your Copilot plan."

## RUN-013 List the workspace's connected runners through the API
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/org/runners
since: 2026-09-15
```
**Preconditions**: Runner online.
**Steps**:
1. `GET /api/org/runners`.
2. Stop the runner; wait 3 minutes; repeat.
**Expected**:
- Step 1: `[{userId:"local_user", online:true, machineName, daemonVersion, lastSeenAt, providers}]` — one row per machine that connected to this workspace.
- Step 2: the row reads `online:false` with the last `lastSeenAt`.
**Notes**: the org roster page went with members (2026-11-17); the endpoint remains for MCP/API clients.

## RUN-014 Reject a token without the orkr_ prefix before any network call
```yaml
priority: P1
surface: [cli]
automation: planned
refs:
  - cli:connect
since: 2026-09-15
```
**Preconditions**: No workspace profile named `e2e-bad` on this machine.
**Steps**:
1. `./orkka connect --workspace e2e-bad --host http://127.0.0.1:<port> --token abc123`.
**Expected**:
- Exit code 1; stderr "That doesn't look like a runner token — they start with `orkr_`. `./orkka start` sets the runner up by itself; you rarely need a token by hand."; no connection to the host (no presence change); no `~/.orkka/workspaces/e2e-bad/profile.env` is written and nothing lands in the OS secret store.

## RUN-015 Connect with a token, persist the workspace profile and appear online
```yaml
priority: P0
surface: [cli, api]
automation: planned
refs:
  - cli:connect
  - orkka-api:GET /api/me/runner
since: 2026-09-15
```
**Preconditions**: A fresh token from RUN-002; no runner running.
**Steps**:
1. `./orkka connect --workspace e2e --host http://127.0.0.1:<port> --token orkr_…` (background).
2. `GET /api/me/runner` every 5 s.
**Expected**:
- `~/.orkka/workspaces/e2e/profile.env` holds the host (and git identity) but NOT the token; the token is in the OS secret store (RUN-040). A legacy `~/.orkka/hosted.env` is migrated into the `default` workspace, its token moved to the secret store with no plaintext backup.
- Within 30 s `online:true`, `machineName` = this hostname; the daemon re-Hellos about every 60 s.

## RUN-016 Reconnect every workspace with a bare connect
```yaml
priority: P1
surface: [cli]
automation: planned
refs:
  - cli:connect
since: 2026-09-15
```
**Preconditions**: RUN-015 done; runner stopped.
**Steps**:
1. `./orkka connect` with no arguments.
**Expected**:
- Every workspace profile on the machine connects (one line per workspace); each appears online in its own Orkka workspace.

## RUN-017 Print usage and exit 1 for connect without a token or a saved workspace
```yaml
priority: P2
surface: [cli]
automation: planned
refs:
  - cli:connect
since: 2026-09-15
```
**Preconditions**: No workspace profiles on the machine (`ORKKA` home pointed at an empty folder, or a fresh user).
**Steps**:
1. `./orkka connect`.
**Expected**:
- Exit 1 with the usage block: "Usage: ./orkka start" / "       ./orkka connect [--token orkr_... --workspace <name> --host http://127.0.0.1:6750]", then "`./orkka start` runs Orkka on this machine and connects the runner to every workspace by itself …".

## RUN-019 Trace one agent run through the spawn path with doctor
```yaml
priority: P1
surface: [cli]
automation: planned
refs:
  - cli:doctor
since: 2026-09-15
```
**Preconditions**: Claude signed in on this machine.
**Steps**:
1. `./orkka doctor claude`.
2. `./orkka doctor nosuch`.
**Expected**:
- Step 1: exit 0; output starts "Orkka runner doctor — tracing one agent run through the runner's spawn path." then "OS: … Machine: … PID: …"; a PATH resolution line "claude resolved on PATH:" listing hits (or "NOT FOUND on this process's PATH — that alone explains failing runs."); an "Auth:" line; "Env vars blanked for the agent child (N):" naming variables only; then one real minimal run that completes (approvals denied by the gate).
- Step 2: exit non-zero with "Unknown provider 'nosuch' — use claude, codex, gemini, copilot, ollama, grok, opencode."

## RUN-020 `./orkka update` points at git pull — Orkka runs from source
```yaml
priority: P2
modes: [offline]
surface: [cli]
automation: planned
refs:
  - cli:update
since: 2026-09-15
```
**Preconditions**: None (Orkka need not be running).
**Steps**:
1. `./orkka update`.
**Expected**:
- Exit 0; prints exactly "Orkka runs from source: `git pull`, then `./orkka start`."; no network request, no download, nothing under `~/.orkka` changes.
**Notes**: Since R4 (2026-11) there is no release binary, no newer-release check and no signed-release install — the former RUN-021/041/042 are retired.

## RUN-022 Emit verbose structured logs with --debug or ORKKA_DEBUG=1 in any position
```yaml
priority: P3
surface: [cli]
automation: planned
refs:
  - cli:doctor
since: 2026-09-15
```
**Preconditions**: None.
**Steps**:
1. `./orkka doctor --debug`; `./orkka --debug doctor`; `ORKKA_DEBUG=1 ./orkka doctor`.
**Expected**:
- All three run `doctor` (the flag is stripped from any position) and print debug-level lines absent from a plain run.
**Notes**: `--debug` also works with `start`, `status` and every other command (`CliArgs`).

## RUN-023 Exit 1 with a hint on an unknown command
```yaml
priority: P2
surface: [cli]
automation: planned
refs:
  - cli:help
since: 2026-09-15
```
**Preconditions**: None.
**Steps**:
1. `./orkka frobnicate`.
**Expected**:
- Exit code 1; stderr "Unknown command 'frobnicate' — try `./orkka help`."

## RUN-024 Print the help block from help, --help and -h
```yaml
priority: P2
surface: [cli]
automation: planned
refs:
  - cli:help
since: 2026-09-15
```
**Preconditions**: None.
**Steps**:
1. `./orkka help`; `./orkka --help`; `./orkka -h`; `./orkka` with no command.
**Expected**:
- Exit 0 each; identical output starting "Orkka — your tickets worked on this machine, with your own git and AI subscriptions." and listing `./orkka start [--port N] [--no-browser]`, `open [--print]`, `status`, `stop`, `workspace list | remove <name>`, `workspace set-identity`, `workspace set-env | unset-env | env`, `doctor [claude|codex|gemini|grok|copilot|opencode|ollama]`, `connect` ("advanced: run only the runner …"), `update` ("Orkka runs from source: `git pull`, then `./orkka start`"), the "Ticket trackers" block (`./orkka tracker add jira|ado`, `tracker list | test [id] | remove <id>`), the "Repository commands" block (`./orkka commands`, `commands approve <id>`, `commands revoke <id>`) and the one flag "--debug              verbose structured logs — works with any command (also: ORKKA_DEBUG=1)".
- No `--no-update-check`, `--detach`, `path` or `up`/`down` anywhere (gone with the installed binary, R4).

## RUN-025 Refuse a daemon that does not advertise the hybrid capability
```yaml
priority: P2
surface: [hub, cli]
automation: manual
refs:
  - cli:connect
  - orkka-api:src/Orkka.Runners.Server/Hosting/HostedRunnerHub.cs
  - orkka-api:GET /api/me/runner
since: 2026-09-15
```
**Preconditions**: A pre-hybrid daemon build (Hello without `hybrid`) and a valid token.
**Steps**:
1. Run its connect against the sandboxed Orkka's `/hubs/runner` (`--host http://127.0.0.1:<port>`).
**Expected**:
- The hub aborts the connection right after Hello; worker log "runner on {Machine} lacks the hybrid capability — update it"; `GET /api/me/runner` never reports `online:true`.
**Notes**: Requires an old binary — manual.

## RUN-026 Mark the runner offline three minutes after its last heartbeat
```yaml
priority: P1
surface: [api, ui]
automation: planned
refs:
  - orkka-api:GET /api/me/runner
  - orkka-api:GET /api/org/runners
  - orkka-ui:/settings/machine
  - i18n:runnerPage
since: 2026-09-15
```
**Preconditions**: Runner online.
**Steps**:
1. Kill the `./orkka` process (no graceful disconnect); poll `GET /api/me/runner` every 30 s for 4 min.
**Expected**:
- `online` flips to `false` no later than 3 min after `lastSeenAt` stops advancing; `lastKnownProviders` and `lastKnownMachineName` still carry the Claude snapshot; `/settings/machine` shows "Offline" with "Last seen" a relative time; `GET /api/org/runners` reports the row `online:false`.

## RUN-027 Pick up CLAUDE_CODE_OAUTH_TOKEN from runner.env per spawn without a restart
```yaml
priority: P2
surface: [cli]
automation: manual
refs:
  - cli:doctor
since: 2026-09-15
```
**Preconditions**: Runner connected; `~/.orkka/runner.env` without `CLAUDE_CODE_OAUTH_TOKEN`.
**Steps**:
1. `./orkka doctor claude`; note the "Auth:" line.
2. Run `claude setup-token`, append `CLAUDE_CODE_OAUTH_TOKEN=<token>` to `~/.orkka/runner.env`; run doctor again without restarting; submit a runner task.
**Expected**:
- Step 1: "Auth:" describes the shared `~/.claude` credentials and suggests the setup token.
- Step 2: "Auth:" names the long-lived setup token from `runner.env`; the task authenticates with it.
**Notes**: Legacy `~/.orkka/stack.env` and `~/.orkka/.env` are migrated into `runner.env` on first read.

## RUN-028 Scrub Claude session markers from the agent child environment
```yaml
priority: P1
surface: [cli, api]
automation: planned
costUsd: 0
refs:
  - cli:doctor
  - orkka-api:POST /api/projects/{projectId}/tasks
  - orkka-api:GET /api/tasks/{taskId}/messages
since: 2026-09-15
```
**Preconditions**: Start the daemon from a shell where `CLAUDECODE=1` and `CLAUDE_CODE_ENTRYPOINT=cli` are exported.
**Steps**:
1. `./orkka doctor claude`; read "Env vars blanked for the agent child".
2. Submit a runner task "Reply only with the output of printing whether the environment variables CLAUDECODE and CLAUDE_CODE_ENTRYPOINT are set; make no code changes." and read its thread.
**Expected**:
- Step 1: the list names sensitive variables (token-like names) — values are never printed.
- Step 2: the agent reports both variables unset (the daemon deletes `CLAUDECODE`, `CLAUDE_CODE_*` and `CLAUDE_AGENT_SDK_*` at startup); `GIT_TERMINAL_PROMPT=0` is in effect (no credential prompt hangs the run).

## RUN-029 Refuse a connect that uses a revoked token
```yaml
priority: P1
surface: [cli, api]
automation: planned
refs:
  - cli:connect
  - orkka-api:DELETE /api/me/runner-tokens/{tokenId}
  - orkka-api:GET /api/me/runner
since: 2026-09-15
```
**Preconditions**: Token R minted then revoked via `DELETE /api/me/runner-tokens/{id}`; daemon stopped.
**Steps**:
1. `./orkka connect --host http://127.0.0.1:<port> --token <R>`.
**Expected**:
- The hub rejects the bearer during the handshake; the CLI prints an authentication error and exits non-zero (or keeps retrying without ever going online); `GET /api/me/runner` stays `online:false`; the revoked row's "Last used" does not change.

## RUN-030 Refuse an unvouched repository command: task-thread note, listed as pending
```yaml
priority: P0
surface: [api, cli]
automation: planned
refs:
  - orkka-api:PATCH /api/projects/{projectId}/repos/{repoId}
  - orkka-api:GET /api/tasks/{taskId}/messages
  - orkka-api:src/Orkka.LocalRunner/Commands/CommandTrust.cs
  - cli:commands
since: 2026-11-17
```
**Preconditions**: The harness's sandboxed runner (approvals live in its own Orkka home); a bare remote seeded from the api fixture, whose orkka.json on `main` declares `check: "npm test"`; hands-free project settings.
**Steps**:
1. `PATCH …/repos/{r} {checkCommand:"npm run lint"}` — a command neither orkka.json nor an approval vouches for.
2. Run the standard task to Done.
3. `GET /api/tasks/{id}/messages`; `./orkka commands` (sandbox).
**Expected**:
- The task still reaches Done — the phase carries on as if the check could not run.
- The thread has a system note "<machine> did not run this check command because it is not in the repository's orkka.json on its default branch and has not been approved on that machine: `npm run lint`. To allow it, run `./orkka commands approve <id>` there — or commit it to orkka.json. The checks did not run for this pass."
- `./orkka commands` lists `<id>` under "Waiting for your approval:" with `$ npm run lint`, the repository and the workspace.
**Notes**: same for setup (HYB-041), build (merge build) and run-target start/install/stop commands; a refused run target also keeps dependent targets from starting. Covered at unit level by `tests/Orkka.Tests/Services/CommandTrustTests.cs` (AnUndeclaredCommand_IsRefused_AndWaitsForTheDeveloper, OrkkaJsonOnATaskBranch_DoesNotVouch); the `./orkka commands` listing by RUN-034.

## RUN-031 Approve a refused command on the machine; the next run of that repo runs it
```yaml
priority: P0
surface: [api, cli]
automation: planned
refs:
  - orkka-api:GET /api/tasks/{taskId}/messages
  - cli:commands
since: 2026-11-17
```
**Preconditions**: RUN-030's pending `<id>`.
**Steps**:
1. `./orkka commands approve --yes <id>`.
2. `./orkka commands`.
3. Run a second standard task on the same repo to Done.
**Expected**:
- Step 1: exit 0, "Approved <id>. Orkka will run it the next time it is needed."
- Step 2: `<id>` is under "Approved on this machine:" and no longer pending.
- Step 3: the check runs; the task's thread has no command-approval note.
**Notes**: an approval covers exactly one (repository, command) pair on this machine — changing the command by one character asks again. Approval happens only at the machine's terminal; no API can approve. Cleanup revokes the approval. Covered at unit level by `tests/Orkka.Tests/Services/CommandTrustTests.cs` (ACommandApprovedAtTheTerminal_RunsFromThenOn, AnApprovalIsForOneRepository, Revoking_RequiresApprovalAgain).

## RUN-032 A command declared in orkka.json on the default branch runs without approval
```yaml
priority: P0
surface: [api, cli]
automation: planned
refs:
  - orkka-api:GET /api/tasks/{taskId}/messages
  - orkka-api:src/Orkka.LocalRunner/Commands/CommandTrust.cs
  - cli:commands
since: 2026-11-17
```
**Preconditions**: A bare remote seeded from the web fixture: its orkka.json on `main` declares `"setup": "npm install"` and `"check": "npm run lint && npm test"`; the repo's own setup/check settings left empty (the orchestrator then reads them from orkka.json through the runner).
**Steps**:
1. Run the standard task hands-free to Done (HYB-046).
2. `GET /api/tasks/{id}/messages`.
**Expected**:
- The setup and check ran (the worktree has `node_modules`; check output appears only on failure); the thread has no "./orkka commands approve" note; nothing new is pending in `./orkka commands`.
**Notes**: comparison is per line with outer whitespace trimmed and CRLF folded, so the same command typed in the repo settings also matches. Covered at unit level by `tests/Orkka.Tests/Services/CommandTrustTests.cs` (ACommandDeclaredOnTheDefaultBranch_Runs).

## RUN-033 orkka.json on a task branch never vouches for a command
```yaml
priority: P1
surface: [api, cli]
automation: planned
refs:
  - orkka-api:GET /api/tasks/{taskId}/messages
  - orkka-api:src/Orkka.LocalRunner/Commands/CommandTrust.cs
since: 2026-11-17
```
**Preconditions**: A fixture remote whose orkka.json on `main` declares no `check`; a task whose description asks the agent to add `"check": "npm run lint"` to orkka.json; the repo's check setting set to the same command.
**Steps**:
1. Run the task to Code Review.
2. Read the thread; merge the PR on the remote's `main`; run a second task.
**Expected**:
- Step 1: the check is refused (RUN-030 note) although the task branch's orkka.json declares it — only `origin/HEAD` (the default branch) counts.
- Step 2: once the change is on `main`, the second task's check runs without approval.

## RUN-034 `./orkka commands` lists, approves (asks or --yes) and revokes
```yaml
priority: P1
modes: [offline]
surface: [cli]
automation: automated
refs:
  - cli:commands
since: 2026-11-17
```
**Preconditions**: A throwaway `ORKKA_HOME`; a pending entry (the offline spec seeds one in the runner's `command-approvals.json` format).
**Steps**:
1. `./orkka commands` with nothing recorded.
2. `./orkka commands approve deadbeef --yes`.
3. With one pending entry: `./orkka commands`; `./orkka commands approve <id>` answering `n`; then `--yes`; then again.
4. `./orkka commands revoke <id>`; `./orkka commands frobnicate`.
**Expected**:
- Step 1: exit 0 "No repository commands have been asked for or approved on this machine." / "Commands in a repository's orkka.json on its default branch run without approval."
- Step 2: exit 1 "No pending command with id deadbeef. Run `./orkka commands` to see them."
- Step 3: the list shows "Waiting for your approval:" with the id, kind, repo, workspace and `$ <command>`; the question shows the command in full and `n` → exit 1 "Not approved."; `--yes` → "Approved <id>."; a second approve → exit 1 "<id> is already approved."
- Step 4: "Forgot <id>. Orkka will ask again before running it."; an unknown sub-command prints the usage and exits 1.
**Notes**: `./orkka command` is an alias.

## RUN-035 Runner advertises command-trust; no repo command goes to a runner without it
```yaml
priority: P1
surface: [api, hub]
automation: planned
refs:
  - orkka-api:GET /api/me/runner
  - orkka-api:src/Orkka.LocalRunner.Contracts/RunnerProtocol.cs
since: 2026-11-17
```
**Preconditions**: Runner online (current build).
**Steps**:
1. `GET /api/me/runner`.
2. (manual) Connect a pre-2026-11-17 build and submit a task on a repo with a setup command.
**Expected**:
- Step 1: `capabilities` contains `command-trust` and neither `setup-command` nor `worktree-command` (retired).
- Step 2: no setup/check/build command is sent to the old runner at all; the run proceeds without them.
**Notes**: step 2 needs an old binary — manual.

## RUN-036 Runner tokens expire 90 days after creation (create and list carry expiresAt)
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/me/runner-tokens
  - orkka-api:GET /api/me/runner-tokens
  - orkka-api:DELETE /api/me/runner-tokens/{tokenId}
since: 2026-11-17
```
**Preconditions**: Harness API key.
**Steps**:
1. `POST /api/me/runner-tokens {name:"e2e-token-…"}`.
2. `GET /api/me/runner-tokens`.
3. `DELETE /api/me/runner-tokens/{id}` (cleanup).
**Expected**:
- Step 1: 200 `{id, name, token:"orkr_…", expiresAt}` with `expiresAt` ≈ now + 90 days.
- Step 2: the row has the same `expiresAt`, `revoked:false` and never the raw token.
**Notes**: expiry and renewal are covered at unit level by `tests/Orkka.Tests/Features/Runners/RunnerTokenLifetimeTests.cs`; the API shape has no spec yet.

## RUN-037 Show each token's expiry and an Expired badge on the Machine page
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/settings/machine
  - orkka-api:GET /api/me/runner-tokens
  - i18n:runnerPage
since: 2026-11-17
```
**Preconditions**: One active token; one token whose `ExpiresAt` is in the past (set in the sandbox's SQLite database).
**Steps**:
1. Open `/settings/machine`; read the "Runner tokens" card.
**Expected**:
- The active row reads "Expires {date} · renews automatically while connected" with badge "Active".
- The expired row shows badge "Expired".

## RUN-038 The hub refuses an expired token at connect and on Hello
```yaml
priority: P1
surface: [hub, cli, api]
automation: manual
refs:
  - cli:connect
  - orkka-api:GET /api/me/runner
  - orkka-api:src/Orkka.Runners.Server/Hosting/HostedRunnerHub.cs
since: 2026-11-17
```
**Preconditions**: A sandboxed Orkka; a token T whose `ExpiresAt` is set in the past in its SQLite database.
**Steps**:
1. `./orkka connect --token <T>`.
2. With a runner connected on token U, set U's `ExpiresAt` in the past; wait for the next Hello (≤ 60 s).
**Expected**:
- Step 1: the handshake is refused like a revoked token (RUN-029); presence never goes online.
- Step 2: the live connection is dropped and the runner cannot reconnect with U.
**Notes**: needs direct database access — manual.

## RUN-039 A connected runner renews its own token before it expires
```yaml
priority: P1
surface: [hub, cli, api]
automation: manual
refs:
  - cli:connect
  - orkka-api:GET /api/me/runner-tokens
  - orkka-api:src/Orkka.Runners.Server/Hosting/HostedRunnerHub.cs
since: 2026-11-17
```
**Preconditions**: A sandboxed Orkka; runner connected on token T; T's `ExpiresAt` moved to within 30 days of now in its SQLite database.
**Steps**:
1. Wait for the runner's renewal check (on connect, then every 6 h — restart the runner to trigger it).
2. `GET /api/me/runner-tokens`; inspect the OS secret store entry `orkka/<workspace>/runner-token`.
3. After 24 h (or with T's grace moved into the past), try `./orkka connect --token <T>`.
**Expected**:
- Step 1: the hub's `RotateToken` hands the runner a fresh token; the runner stores it in the OS secret store and redials with it — presence stays online.
- Step 2: a new token row with `expiresAt` ≈ now + 90 days; T's `expiresAt` is cut to ≈ now + 24 h (grace).
- Step 3: T is refused.
**Notes**: needs database time travel — manual.

## RUN-040 The runner token lives in the OS secret store, never in profile.env
```yaml
priority: P0
surface: [cli]
automation: planned
refs:
  - cli:connect
  - orkka-api:src/Orkka.LocalRunner/Cli/WorkspaceProfiles.cs
since: 2026-11-17
```
**Preconditions**: A runner connected with `--token`.
**Steps**:
1. Read `~/.orkka/workspaces/<ws>/profile.env`.
2. Look for the token in the OS store: Windows Credential Manager generic credential `orkka/<ws>/runner-token` ("Orkka runner token"), macOS Keychain, or libsecret.
3. Upgrade from a build that kept `ORKKA_RUNNER_TOKEN` in profile.env, then connect.
**Expected**:
- Step 1: host and identity only — no `orkr_` value.
- Step 2: the token is there.
- Step 3: the legacy plaintext token is moved to the secret store and removed from profile.env; no backup copy is left.
**Notes**: the harness's sandbox (a non-default `ORKKA_HOME`, plus `ORKKA_SECRET_STORE=file`) always uses the file store `<ORKKA_HOME>/workspaces/<ws>/secrets/runner-token` — a spec can read that and profile.env (none does yet); steps 2–3 on a real home are manual. Covered at unit level by `tests/Orkka.Tests/Services/RunnerWorkspaceTests.cs` (Migration_MovesTheSingleConnection_IntoTheDefaultWorkspace_Once: the token is not in profile.env and no plaintext backup is left; Profiles_RoundTrip_ListDefaultFirst_AndNeverPrintTheirToken).

## RUN-043 doctor reports what the old installer left behind, and changes nothing
```yaml
priority: P2
modes: [offline]
surface: [cli]
automation: planned
refs:
  - cli:doctor
  - orkka-api:src/Orkka.LocalRunner/Cli/LegacyInstall.cs
since: 2026-11-18
```
**Preconditions**: A throwaway `ORKKA_HOME` containing `bin/`, `app/` and `runtime/` folders; `<ORKKA_HOME>/bin` on the PATH doctor runs with; on Linux/macOS a throwaway `HOME` whose `.bashrc` has `export PATH="$HOME/.orkka/bin:$PATH"` under a "# Added by Orkka (orkka-local path add)" marker.
**Steps**:
1. `./orkka doctor`.
2. Hash the PATH value, the rc file and the three folders before and after.
**Expected**:
- One "Old install: …" line per finding: "<ORKKA_HOME>/bin is on your PATH (added by the old Orkka installer). Orkka no longer needs it: …" (Windows: the Environment Variables steps; Unix: remove the line from your shell's startup file), "<rc> still has the old installer's PATH lines (line N, M). Delete them in an editor; Orkka never edits this file.", and per folder "<dir> is left from the old installer (the orkka-local binary, its bundle or the Node.js it downloaded). Orkka runs from your clone now; you can delete this folder."
- Nothing changed — doctor only reports. With none of these present, no "Old install:" line is printed.
