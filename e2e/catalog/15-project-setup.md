---
area: PSU
title: Project setup — the guided onboarding of a new project, the dry run and the Start gate
modes-default: [hybrid-folder]
---

## PSU-001 Creating a project from a folder opens its guided setup
```yaml
priority: P0
surface: [ui]
automation: planned
refs:
  - orkka-ui:/projects/new
  - orkka-ui:/p/[projectId]/setup
  - i18n:projectSetup
since: 2026-11-19
```
**Preconditions**: runner online; a folder with one git repository.
**Steps**:
1. Open `/projects/new`, choose the folder, click "Create project".
**Expected**:
- The browser lands on `/p/{slug}/setup`; the stepper shows Folder & repos · Machine check · Ticket source · Team & model · Review & Start, the first step current.
- The subtitle says nothing is pulled from the tracker before Start.

## PSU-002 A new project starts with its setup open
```yaml
priority: P0
surface: [api]
automation: automated
refs:
  - orkka-api:POST /api/projects
  - orkka-api:GET /api/projects/{projectId}
since: 2026-11-19
```
**Preconditions**: the sandboxed Orkka.
**Steps**:
1. `POST /api/projects {name:"e2e-psu-…"}`; `GET /api/projects/{id}`.
**Expected**:
- `setupCompletedAt: null`, `setupSteps: []`. (Projects that existed before the setup flow were migrated as started — `ProjectSetupMigrationTests`.)

## PSU-003 Confirm and take back setup steps
```yaml
priority: P1
surface: [api]
automation: automated
refs:
  - orkka-api:PUT /api/projects/{projectId}/setup/steps
since: 2026-11-19
```
**Preconditions**: a project created by the spec.
**Steps**:
1. `PUT …/setup/steps {step:"source"}`, then `{step:"folder"}` (by slug), then `{step:"source", done:false}`.
2. `PUT …/setup/steps {step:"review"}` and `{step:"deploy"}`.
**Expected**:
- `setupSteps` is `["folder","source"]`, then `["folder"]` — always in setup order.
- `review` (finished by Start) and unknown steps → 400.

## PSU-004 The setup resumes at the first step not confirmed
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/setup
  - orkka-ui:/p/[projectId]/board
since: 2026-11-19
```
**Preconditions**: a project with only Folder & repos confirmed.
**Steps**:
1. On the board, click "Finish setup" in the amber banner.
2. Click "Folder & repos" in the stepper, then "Continue".
**Expected**:
- The setup opens at Machine check (`/p/{slug}/setup`, no `?step=`); Folder & repos shows a check mark.
- The URL carries `?step=folder`, then `?step=machine`; reloading keeps the step.

## PSU-005 Folder & repos: branch, landing mode, forge, CLI account and commands
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/setup
  - orkka-api:PATCH /api/projects/{projectId}/repos/{repoId}
  - orkka-api:POST /api/projects/{projectId}/rescan
since: 2026-11-19
```
**Preconditions**: a folder of repositories: one with an origin on github.com, one without.
**Steps**:
1. Open the setup's Folder & repos step.
2. On the remote repository change the default branch, pick "Azure Repos" as forge, type a CLI account, set Setup `npm ci` and Check `npm test`; click "Save".
**Expected**:
- The remote card says "Pull request" (published with your forge CLI), the folder card "Local folder" (squash-merged into it); only the remote card offers forge and CLI account.
- `GET …/repos` returns the new branch, `provider: "azuredevops"`, the account and both commands at once (the repo list is not served stale).
- Each command shows whether this machine would run it (PSU-006).

## PSU-006 The command check: declared, approved or waiting — with the exact approve command
```yaml
priority: P0
surface: [api, cli]
automation: automated
refs:
  - orkka-api:POST /api/projects/{projectId}/setup/commands
  - orkka-api:POST /internal/runner/repo-commands/vet
  - cli:commands
since: 2026-11-19
```
**Preconditions**: runner online; a repository whose orkka.json declares `setup: "npm ci"`.
**Steps**:
1. `POST …/setup/commands` on a repository without commands.
2. Set Setup `npm ci` and Check `make bootstrap` on the repository; `POST …/setup/commands` again.
3. `./orkka commands approve <approvalId> --yes` in the sandbox; check again.
**Expected**:
- Step 1: `supported: true`, the repository with `commands: []` (orkka.json needs no approval) — automated.
- Step 2: setup `allowed: true, source: "declared"`; check `allowed: false` with an `approvalId`; `./orkka commands` lists it as waiting. Nothing ran.
- Step 3: check `allowed: true, source: "approved"`. There is no API that approves.

## PSU-007 The machine check, with Re-check
```yaml
priority: P0
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/setup
  - orkka-api:GET /api/me/runner
  - orkka-api:GET /api/projects/{projectId}/repos/access-check
since: 2026-11-19
```
**Preconditions**: runner online; a project with a GitHub repository.
**Steps**:
1. Open Machine check with the runner online; then stop Orkka's runner (or sign the AI CLI out) and click "Re-check".
**Expected**:
- Green: runner online on the machine, AI CLIs signed in, the commit identity (`GET /api/me/runner` → `commitIdentity {name, email, source: workspace|git-config}`), each repository reachable, `gh` signed in for GitHub repositories.
- After the change: red items with the exact fix (`./orkka start`, `claude`, `gh auth login`, `./orkka workspace set-identity …`).

## PSU-008 Ticket source: connect a tracker inline, or choose None
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/setup
  - orkka-api:POST /api/trackers/connections
  - orkka-api:PUT /api/projects/{projectId}/tracker
  - orkka-api:DELETE /api/projects/{projectId}/tracker
modes: [tracker]
since: 2026-11-19
```
**Preconditions**: the fake tracker; no Jira connection on the sandbox.
**Steps**:
1. Ticket source → Jira: fill site, email and token in the inline form; "Add".
2. Pick the project, keep the defaults, "Save".
3. Switch to "None" and click "Remove the ticket source"; "Continue".
**Expected**:
- Step 1: the connection appears (token only in the sandbox's secret store); the ticket source card shows with that connection.
- Step 2: saved; nothing is polled (PSU-010).
- Step 3: `GET …/tracker` → 204; the checklist reads "No ticket source: you write the tasks yourself" (green).

## PSU-009 The dry run lists exactly what the first poll would pull — and creates nothing
```yaml
priority: P0
surface: [api, ui]
automation: automated
refs:
  - orkka-api:POST /api/projects/{projectId}/tracker/preview
  - orkka-api:POST /internal/runner/tracker-query
  - orkka-ui:/p/[projectId]/setup
modes: [tracker]
since: 2026-11-19
```
**Preconditions**: the fake tracker; a saved ticket source on a project not started.
**Steps**:
1. `POST /api/projects/{id}/tracker/preview` (UI: Ticket source → "Preview").
**Expected**:
- `supported: true, ok: true`; `preview.tasks` lists the tickets in intake assigned to me with their status, assignee and repository (or "Picked at import" when only the repo router could place it); a Story lists its member sub-tasks in tracker order with its branch; `skipped` says why the rest are left out.
- `GET …/tasks` is still empty and `lastPolledAt` stays null. Automated in workflow step 7 (ADO work item 501). The same intake rules as the poll are pinned by `TrackerSetupGateAndPreviewTests`.

## PSU-010 Nothing is pulled before Start; Start opens intake
```yaml
priority: P0
surface: [api]
automation: automated
refs:
  - orkka-api:POST /api/projects/{projectId}/setup/start
  - orkka-api:GET /api/projects/{projectId}/tracker
modes: [tracker]
since: 2026-11-19
```
**Preconditions**: PSU-009's project and source.
**Steps**:
1. Wait two runner poll cycles (70 s).
2. `POST /api/projects/{id}/setup/start`.
**Expected**:
- Step 1: `lastPolledAt` still null, no task — the runner is not handed the source.
- Step 2: `setupCompletedAt` set; the work item becomes a task within a poll. Automated in workflow step 7.

## PSU-011 Start is idempotent and records what "Start anyway" overrode
```yaml
priority: P1
surface: [api]
automation: automated
refs:
  - orkka-api:POST /api/projects/{projectId}/setup/start
since: 2026-11-19
```
**Preconditions**: a project created by the spec, not started.
**Steps**:
1. `POST …/setup/start {warnings:["Claude is not signed in"]}`; again with no warnings.
**Expected**:
- Both answer the same `setupCompletedAt`; `setupSteps` is all four steps.
- The audit trail records one ProjectSettingsChanged "started anyway with 1 warning(s)" (`ProjectSetupEndpointTests`).

## PSU-012 Review: Start disabled while anything is red; "Start anyway" lists the warnings
```yaml
priority: P0
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/setup
since: 2026-11-19
```
**Preconditions**: a project not started; the runner stopped.
**Steps**:
1. Open Review & Start.
2. Click "Start anyway…", then "Start anyway".
**Expected**:
- The checklist puts "Your runner is offline." (red, with `./orkka start`) first, each item linking to its step; "Start" is disabled.
- Step 2 lists every red and amber item, starts the project and opens the board, where the "Finish setup" banner is gone.

## PSU-013 Re-run setup checks from project settings
```yaml
priority: P2
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/settings
  - orkka-ui:/p/[projectId]/setup
since: 2026-11-19
```
**Preconditions**: a started project, and one not started.
**Steps**:
1. On a started project open Settings; click "Re-run setup checks".
**Expected**:
- `/p/{slug}/setup?step=review` shows the checklist, "Started — your runner pulls this project's tickets" and no Start button; every setting stays editable.
- On a project not started the card says "Finish setup" instead.

## PSU-014 The dry run needs a saved source, and an offline runner is a state
```yaml
priority: P2
surface: [api]
automation: automated
refs:
  - orkka-api:POST /api/projects/{projectId}/tracker/preview
since: 2026-11-19
```
**Preconditions**: a project with no ticket source.
**Steps**:
1. `POST …/tracker/preview` on a project without a ticket source.
**Expected**:
- 404. (With a source and the runner offline: `supported: false`, never a 5xx — `ProjectSetupEndpointTests`.)
