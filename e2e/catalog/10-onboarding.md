---
area: ONB
title: Onboarding — dashboard router and project-creation wizard
modes-default: [all]
---

## ONB-002 Route /dashboard to the project named in the orkka-last-project cookie
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/dashboard
  - orkka-api:GET /api/projects
since: 2026-09-15
```
**Preconditions**: Two Ready Unmanaged projects `e2e-onb-a-<stamp>` and `e2e-onb-b-<stamp>` created via `POST /api/projects`.
**Steps**:
1. Set cookie `orkka-last-project=<slug of project b>` on the sandboxed Orkka's origin.
2. Navigate to `/dashboard`.
**Expected**:
- Browser lands on `/p/<slug of project b>` (overview), not on project a.
**Notes**: cleanup deletes both projects.

## ONB-003 Resolve a legacy ULID in the orkka-last-project cookie to the project slug
```yaml
priority: P2
surface: [ui]
automation: planned
refs:
  - orkka-ui:/dashboard
  - orkka-api:GET /api/projects
since: 2026-09-15
```
**Preconditions**: One Ready project; its `id` (ULID) and `slug` known.
**Steps**:
1. Set cookie `orkka-last-project=<project ULID>`.
2. Navigate to `/dashboard`.
**Expected**:
- Browser lands on `/p/<slug>` — the ULID is matched against `p.id` and redirected to the slug route.

## ONB-024 Choose the project folder in the folder picker on New project
```yaml
priority: P1
modes: [hybrid-repo, hybrid-folder]
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/projects/new
  - orkka-ui:apps/local/components/projects/CreateProjectFromFolder.tsx
  - orkka-api:POST /api/runners/fs/browse
  - orkka-api:POST /api/runners/fs/discover
  - i18n:fromFolder
  - i18n:folderPicker
since: 2026-09-15
```
**Preconditions**: Runner online with `local-folders`. A git repo at `C:\e2e\folder-<stamp>` on this machine.
**Steps**:
1. Open `/projects/new`; under "Project folder" ("No folder chosen yet") click "Choose folder…".
2. In the modal "Choose the project folder", navigate from "This machine" (drive list) into `C:\` then `e2e`.
3. Select `folder-<stamp>`; click "Use folder-<stamp>".
**Expected**:
- Each navigation issues `POST /api/runners/fs/browse {path}` returning entries with `name`, `fullPath`, `isGitRepo`.
- The modal closes; the path replaces "No folder chosen yet", the button reads "Change folder…", "Reading the folder…" shows while `POST /api/runners/fs/discover` runs, then "Found" with "One repository (monorepo)" and the repo row ("Remote · <host>" or "Local folder", "on <branch>").
- "Project name" defaults to the folder name ("Defaults to the folder name.").
**Notes**: creating the project from here is LWF-002. Before 2026-11 this picker lived in the onboarding wizard.

## ONB-025 Show the runner-offline state in the folder picker on New project
```yaml
priority: P2
modes: [hybrid-repo, hybrid-folder]
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/projects/new
  - orkka-api:POST /api/runners/fs/browse
  - orkka-api:POST /api/runners/fs/discover
  - i18n:folderPicker
  - i18n:fromFolder
since: 2026-09-15
```
**Preconditions**: Runner offline (or `POST /api/runners/fs/browse` and `/fs/discover` intercepted to return `{supported:false}`).
**Steps**:
1. Open `/projects/new`; click "Choose folder…".
2. (With a folder already chosen) let the discover call answer `{supported:false}`.
**Expected**:
- Step 1: the modal shows "Start Orkka on your machine (`./orkka start`) to browse folders there."; no folder rows; nothing can be used.
- Step 2: "Your runner is not running. Start Orkka with `./orkka start`, then choose the folder again."; "Create project" stays disabled.

