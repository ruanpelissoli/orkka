---
area: RDK
title: Run dock — run the project's apps on your machine
modes-default: [hybrid-repo, hybrid-folder]
---

## RDK-001 Hide the run dock while the runner is offline and show it once online
```yaml
priority: P0
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-ui:apps/local/components/run/RunDock.tsx
  - orkka-api:GET /api/me/runner
  - i18n:run
since: 2026-09-15
```
**Preconditions**: Project with the fixture web repo linked.
**Steps**:
1. With the runner stopped open `/p/{slug}/board`.
2. Start the runner (`./orkka connect` against this Orkka, or restart with `./orkka start`); wait for `GET /api/me/runner` `online:true`; reload.
**Expected**:
- Step 1: no floating button with `aria-label="Run this project"` anywhere in the shell.
- Step 2: the FAB "Run this project" appears bottom-right in the floating dock (order after the terminal slot).

## RDK-002 Hide the run dock when the runner lacks the app-run capability
```yaml
priority: P2
surface: [ui, api]
automation: manual
refs:
  - orkka-ui:apps/local/components/run/RunDock.tsx
  - orkka-api:GET /api/me/runner
since: 2026-09-15
```
**Preconditions**: A runner build whose `RunnerHello.Capabilities` omits `app-run` (`GET /api/me/runner` lists `capabilities` without it).
**Steps**:
1. Open any project page.
**Expected**:
- No FAB rendered. With `capabilities` absent entirely (older daemon) the dock renders.
**Notes**: Needs an old binary — manual.

## RDK-003 Open the panel on a never-detected project and read the empty state
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-api:GET /api/projects/{projectId}/run-config
  - orkka-api:GET /api/projects/{projectId}/app-run
  - i18n:run
since: 2026-09-15
```
**Preconditions**: Runner online; project never detected.
**Steps**:
1. Click "Run this project".
**Expected**:
- `GET …/run-config` → 200 `{detected:false, targets:[], source:…, stale:false}` (not 404); `GET …/app-run` → empty snapshot.
- Panel title "Run locally"; body "Orkka has not worked out how to run this project yet. It will read your repositories and figure out the commands for each app.", button "Work out how to run it", hint "Orkka reads your repositories once and remembers the commands — running again costs nothing."
- Header button "Hide run panel" collapses the panel back to the FAB.

## RDK-004 Detect run targets from the fixture's orkka.json
```yaml
priority: P0
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-api:POST /api/projects/{projectId}/run-config/detect
  - orkka-api:GET /api/projects/{projectId}/run-config
  - i18n:run
since: 2026-09-15
```
**Preconditions**: RDK-003 state; fixture repo `orkka.json` declares a `web` target on port 3000.
**Steps**:
1. Click "Work out how to run it".
**Expected**:
- `POST …/run-config/detect` → 200 with `source:"manifest"`, `detected:true`, one target `{key, label, kind:"web", command, port:3000, repo}`.
- Body switches to "Pick what to start, then press Run." with the note "Commands come from the repository's orkka.json"; no "Work out how to run it" button remains; the target chip shows the label, the kind pill "web" and a checkbox.

## RDK-005 Return 422 from detect when the project has no repository
```yaml
priority: P2
surface: [api, ui]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/run-config/detect
  - i18n:run
since: 2026-09-15
```
**Preconditions**: A project with zero repos.
**Steps**:
1. `POST …/run-config/detect`.
**Expected**:
- 422 with an actionable message (link a repository); the panel shows the message in the error strip, falling back to "Could not work out how to run this project." when the body carries none.

## RDK-006 Flag the stored commands as stale after the repositories change
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/run-config
  - orkka-api:POST /api/projects/{projectId}/run-config/detect
  - i18n:run
since: 2026-09-15
```
**Preconditions**: RDK-004 detected config.
**Steps**:
1. Push a commit to the fixture repo changing `package.json` scripts (or link a second repo).
2. Reopen the panel.
3. Click "Re-detect" in the strip.
**Expected**:
- `GET …/run-config` → `stale:true`; strip "Your repositories changed since these commands were worked out." with a "Re-detect" button (title "Read the repositories again and replace the stored commands").
- Step 3: `POST …/run-config/detect` again → `stale:false`, `detectedAt` updated; the strip disappears.

## RDK-007 Run the web target and watch the Live app chip go from starting to ready
```yaml
priority: P0
surface: [ui, api, hub]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-api:POST /api/projects/{projectId}/app-run/start
  - orkka-api:GET /api/projects/{projectId}/app-run
  - hub:TaskHub.AppRunState
  - hub:TaskHub.AppRunOutput
  - i18n:run
since: 2026-09-15
```
**Preconditions**: RDK-004 config; port 3000 free; dependencies installed in the runner's working copy.
**Steps**:
1. Check the `web` chip; click "Run".
**Expected**:
- `POST …/app-run/start {targetKeys:["web"], forceFreePorts:false}` → 200 with a run id; header shows "1 running"; a log tab appears with the target label and `:3000` link; the FAB shows a running count.
- `AppRunOutput` events stream into the xterm view; `AppRunState` moves the status dot from starting to ready.
- The "Live app" chip first reads "Starting…" (amber) then the host (`localhost:3000`) with a green dot; its link `aria-label="Open http://localhost:3000"` opens the fixture page (`<h1>Orkka E2E Fixture</h1>`).

## RDK-008 Stop a single target from its log tab
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/app-run/stop
  - orkka-api:GET /api/projects/{projectId}/app-run
  - i18n:run
since: 2026-09-15
```
**Preconditions**: RDK-007 running.
**Steps**:
1. Click the tab's button `aria-label="Stop"`.
**Expected**:
- `POST …/app-run/stop {runId}` → 200; the process on port 3000 exits; the running count disappears; `GET …/app-run` shows the run stopped; the Live app chip disappears.

## RDK-009 Stop every running target with Stop all
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/app-run/stop
  - i18n:run
since: 2026-09-15
```
**Preconditions**: Two targets running (fixture web + fixture api on port 4000 in a project linking both repos).
**Steps**:
1. Click "Stop all".
**Expected**:
- `POST …/app-run/stop {}` (no runId) → 200; "2 running" disappears; both ports are free within 30 s.

## RDK-010 Persist "Keep running" as the project's autoRunOnOpen setting
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PATCH /api/projects/{id}
  - orkka-api:GET /api/projects/{id}
  - i18n:run
since: 2026-09-15
```
**Preconditions**: Panel open; caller has `ProjectsManage`.
**Steps**:
1. Flip the "Keep running" Toggle (title "Start the app whenever you open this project, and keep it up so every merge shows in the tab you left open.").
2. Reload.
**Expected**:
- `PATCH /api/projects/{id} {settings:{…, autoRunOnOpen:true}}` → 200; `GET /api/projects/{id}` → `settings.autoRunOnOpen:true`; the Toggle is on after reload.
- Save failure (route 500) shows "Could not save." and reverts the Toggle.

## RDK-011 Auto-start the app once per project when Keep running is on
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/app-run/start
  - orkka-api:GET /api/projects/{projectId}/app-run
  - i18n:run
since: 2026-09-15
```
**Preconditions**: `autoRunOnOpen:true`; detected config; nothing running; runner online.
**Steps**:
1. Open `/p/{slug}` in a fresh tab.
2. Navigate to `/p/{slug}/board`, then `/p/{slug}/tasks`.
**Expected**:
- Step 1: exactly one `POST …/app-run/start` fires without a click; the Live app chip appears.
- Step 2: no further start requests in the same tab (auto-start is remembered per project per tab); the app keeps running.

## RDK-012 Toggle target chips and read their kind pills
```yaml
priority: P2
surface: [ui]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/run-config
  - i18n:run
since: 2026-09-15
```
**Preconditions**: Project linking fixture web (kind `web`) and fixture api (kind `api`); detected.
**Steps**:
1. Uncheck the `api` chip; click "Run".
**Expected**:
- Chips show pills "web" and "api"; only the checked target key is sent in `targetKeys`; only one log tab opens.
- A target with `restartOnMerge:true` shows an icon with `aria-label="Restarts on merge"`.

## RDK-013 Edit a target's command, folder and port and save a manual config
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PUT /api/projects/{projectId}/run-config
  - orkka-api:GET /api/projects/{projectId}/run-config
  - i18n:run
since: 2026-09-15
```
**Preconditions**: Detected config; caller has `ReposManage`.
**Steps**:
1. Click the pencil `aria-label="Edit command"` on the `web` chip.
2. Set "Command" to `npm run dev -- -p 3100`, "Folder (inside the repo)" to `.`, "Port" to `3100`; click "Save".
**Expected**:
- Editor shows "Install dependencies with `npm ci` if this fails on a fresh checkout." (when `installCommand` is known) and "Never put secrets here. The app reads the .env file already sitting in your working copy (Orkka creates it from .env.example when it is missing)."
- `PUT …/run-config {targets:[…], notes}` → 200; `GET …/run-config` → `source:"manual"`, target port 3100; "Cancel" discards edits without a request.

## RDK-014 Offer "Free the port and run" when the target's port is busy
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/app-run/start
  - i18n:run
since: 2026-09-15
```
**Preconditions**: A stray `node -e "require('http').createServer().listen(3000)"` holds port 3000.
**Steps**:
1. Click "Run".
2. Click "Free the port and run".
**Expected**:
- Step 1: snapshot has `portConflict`; strip "A port is already in use by another process." with the button; the FAB shows a red failure dot.
- Step 2: `POST …/app-run/start {targetKeys, forceFreePorts:true}` → the stray process is killed, the web target reaches ready on 3000.

## RDK-015 Mark a target failed with its log tail when the command exits early
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PUT /api/projects/{projectId}/run-config
  - orkka-api:POST /api/projects/{projectId}/app-run/start
  - i18n:run
since: 2026-09-15
```
**Preconditions**: Manual target with command `node -e "console.error('boom'); process.exit(1)"` and port 3000.
**Steps**:
1. Click "Run".
**Expected**:
- Within 15 s the tab's status dot turns to failed; an error strip shows the failure reason and the log body ends with `boom`; the FAB red dot appears; "Stop" is no longer offered for that run.

## RDK-016 Prepare the workspace: copy .env from .env.example and install dependencies
```yaml
priority: P1
surface: [ui, cli, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/app-run/start
  - i18n:run
since: 2026-09-15
```
**Preconditions**: Runner with `app-run-prepare`; in the runner's working copy of the fixture delete `.env` and `node_modules`; the fixture ships `.env.example`.
**Steps**:
1. Click "Run"; read the log.
**Expected**:
- Log announces the `.env` created from `.env.example` and "Dependencies look missing — running: npm ci" before the app command; `.env` exists afterwards; the target reaches ready.

## RDK-017 Refuse a Docker-backed target plainly when the Docker daemon is down
```yaml
priority: P2
surface: [ui, api]
automation: manual
refs:
  - orkka-api:PUT /api/projects/{projectId}/run-config
  - orkka-api:POST /api/projects/{projectId}/app-run/start
  - i18n:run
since: 2026-09-15
```
**Preconditions**: Docker Desktop stopped; a manual target `{label:"db", kind:"database", command:"docker compose up postgres"}`.
**Steps**:
1. Run the `db` target.
**Expected**:
- Fails immediately with "'db' needs Docker, but the Docker daemon is not running — start Docker Desktop and run again." in the failed-target strip; no compose pipe error.
**Notes**: Stopping Docker on the CI machine is disruptive — manual.

## RDK-018 Restart a restart-on-merge target after a task merges and post to the thread
```yaml
priority: P1
surface: [ui, api, hub]
automation: planned
costUsd: 0
refs:
  - orkka-api:GET /api/projects/{projectId}/app-run
  - orkka-api:GET /api/tasks/{taskId}/messages
  - hub:TaskHub.AppRunState
  - i18n:run
since: 2026-09-15
```
**Preconditions**: Fixture `web` target with `restartOnMerge:true` running (run id A); a runner task on the same repo at `WaitingCodeReview`.
**Steps**:
1. Approve and let the PR merge.
**Expected**:
- `GET …/app-run` shows a new run id B for the target and A stopped; the Live app chip cycles "Starting…" → ready; reloading `localhost:3000` shows the merged change.
- The task thread contains a message saying the app was restarted after the merge.

## RDK-019 Switch between log tabs and type into the running process
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/app-run/input
  - orkka-api:POST /api/projects/{projectId}/app-run/resize
  - hub:TaskHub.AppRunOutput
  - i18n:run
since: 2026-09-15
```
**Preconditions**: Two targets running; one of them a manual target `node -e "process.stdin.on('data',d=>process.stdout.write('echo:'+d))"`.
**Steps**:
1. Click the echo target's tab; type `hello` + Enter into the xterm.
2. Resize the browser window.
**Expected**:
- Tab switch swaps the RunLogView contents; typing issues `POST …/app-run/input {runId, dataBase64}` and `echo:hello` streams back via `AppRunOutput`.
- Resizing issues `POST …/app-run/resize {runId, cols, rows}`.

## RDK-020 Refuse run-config writes from an API key without ReposManage
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:PUT /api/projects/{projectId}/run-config
  - orkka-api:POST /api/projects/{projectId}/run-config/detect
  - orkka-api:GET /api/projects/{projectId}/run-config
since: 2026-09-15
```
**Preconditions**: API key with `["TasksCreate"]` only.
**Steps**:
1. `PUT …/run-config {targets:[]}`; `POST …/run-config/detect`.
2. `GET …/run-config`.
**Expected**:
- Both writes 403; the read 200 (reads are open to members).

## RDK-021 Reattach to running apps after a page reload
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/app-run
  - i18n:run
since: 2026-09-15
```
**Preconditions**: RDK-007 running.
**Steps**:
1. Reload the page; open the panel.
**Expected**:
- `GET …/app-run` returns the live snapshot; header shows "1 running", the same log tab is present (with the buffered log tail) and the Live app chip is ready — no new start request.
