---
area: WSP
title: Workspaces — one Orkka org per job, switcher, X-Orkka-Workspace isolation
modes-default: [all]
---

## WSP-001 List my workspaces with exactly one active
```yaml
priority: P0
surface: [api]
automation: automated
refs:
  - orkka-api:GET /api/me/workspaces
since: 2026-10-26
```
**Preconditions**: Signed in (Clerk session); the account belongs to ≥ 1 workspace.
**Steps**:
1. `GET /api/me/workspaces`.
**Expected**:
- 200 array of `{orgId, name, slug, role, isActive, suspended}`; `role` is `Owner` or `Member`.
- Exactly one element has `isActive:true`.
- The answer is the same whichever workspace the request was made in (`X-Orkka-Workspace` of any member org).

## WSP-002 Create a workspace: owned by me and made active
```yaml
priority: P0
surface: [api]
automation: automated
refs:
  - orkka-api:POST /api/workspaces
  - orkka-api:GET /api/me/workspaces
since: 2026-10-26
```
**Preconditions**: Clerk session. Cleanup: the harness cannot delete an org — name it `e2e-ws-<stamp>` and reuse it on later runs (see Notes).
**Steps**:
1. `POST /api/workspaces {name:"e2e-ws-<stamp>"}`.
2. `GET /api/me/workspaces`.
3. `POST /api/workspaces {name:""}`.
**Expected**:
- Step 1: 200 `{orgId, name:"e2e-ws-<stamp>", role:"Owner", isActive:true, suspended:false}`.
- Step 2: the new workspace is listed and is the only active one; the previously active one is still listed with `isActive:false`.
- Step 3: 400 "Name the workspace — usually after the job or client."
**Notes**: There is no delete-workspace endpoint; the spec reuses a single `e2e-ws-*` workspace when one already exists and switches back to the original active workspace at the end.

## WSP-003 Switch the active workspace; unknown or foreign org → 404
```yaml
priority: P0
surface: [api]
automation: automated
refs:
  - orkka-api:POST /api/me/active-workspace
  - orkka-api:GET /api/me/workspaces
since: 2026-10-26
```
**Preconditions**: Clerk session; two workspaces A (active) and B.
**Steps**:
1. `POST /api/me/active-workspace {orgId:B}`; `GET /api/me/workspaces`.
2. `POST /api/me/active-workspace {orgId:<random ULID>}`.
3. `POST /api/me/active-workspace {orgId:A}` (restore).
**Expected**:
- Step 1: 204; B is now `isActive:true`, A `false`; `GET /api/org/me` without a header answers for B.
- Step 2: 404; the active workspace is unchanged.
- Step 3: 204.

## WSP-004 X-Orkka-Workspace that is not a ULID → 400
```yaml
priority: P1
surface: [api]
automation: automated
refs:
  - orkka-api:GET /api/projects
  - orkka-api:src/Orkka.Local/Auth/CurrentUserMiddleware.cs
since: 2026-10-26
```
**Preconditions**: Clerk session.
**Steps**:
1. `GET /api/projects` with header `X-Orkka-Workspace: not-a-ulid`.
**Expected**:
- 400 `{message:"Invalid workspace."}`; nothing else runs.

## WSP-005 X-Orkka-Workspace naming a workspace I am not in → 403
```yaml
priority: P0
surface: [api]
automation: automated
refs:
  - orkka-api:GET /api/projects
  - orkka-api:GET /api/org/me
since: 2026-10-26
```
**Preconditions**: Clerk session; a syntactically valid ULID of an org the caller does not belong to (a random ULID works).
**Steps**:
1. `GET /api/projects` with `X-Orkka-Workspace: <that ULID>`.
**Expected**:
- 403 `{message:"You are not a member of that workspace."}` — never data from another org, never a silent fallback to the active workspace.

## WSP-006 Header isolation: the same call in two workspaces returns disjoint data
```yaml
priority: P0
surface: [api]
automation: automated
refs:
  - orkka-api:GET /api/projects
  - orkka-api:POST /api/projects
  - orkka-api:GET /api/org/me
since: 2026-10-26
```
**Preconditions**: Clerk session; workspaces A and B; an `e2e-*` project created in A (with `X-Orkka-Workspace: A`).
**Steps**:
1. `GET /api/projects` with `X-Orkka-Workspace: A`; again with `B`.
2. `GET /api/org/me` with each header.
3. `GET /api/projects/{projectInA}` with `X-Orkka-Workspace: B`.
**Expected**:
- Step 1: the project ids of the two answers do not intersect; the A answer contains the new project.
- Step 2: `orgId` equals the header's id each time.
- Step 3: 404 (not 403 — the id is not confirmed to exist).

## WSP-007 An API key ignores X-Orkka-Workspace and stays bound to its own org
```yaml
priority: P1
surface: [api]
automation: automated
refs:
  - orkka-api:GET /api/org/me
  - orkka-api:GET /api/projects
since: 2026-10-26
```
**Preconditions**: An `orkka_key_…` minted in workspace A; the caller also belongs to B.
**Steps**:
1. `GET /api/org/me` with the key and `X-Orkka-Workspace: B`.
**Expected**:
- 200 with `orgId` = A — keys are org-bound; the header does not move them.

## WSP-008 Switch workspaces from the top-bar switcher
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:/dashboard
  - orkka-ui:apps/local/components/workspace/WorkspaceSwitcher.tsx
  - orkka-api:POST /api/me/active-workspace
  - i18n:workspaces
since: 2026-10-26
```
**Preconditions**: Signed in with two workspaces A (current) and B, each with one project.
**Steps**:
1. Click the "Workspace" button in the top bar.
2. Read the list; click B.
**Expected**:
- The menu lists both with their role ("Owner"/"Member") and a runner dot ("Runner online"/"Runner offline") per workspace.
- Clicking B shows "Switching…", sets the `orkka_ws` cookie to B, calls `POST /api/me/active-workspace`, and hard-navigates to `/dashboard`, which lands in B's project; every later request carries `X-Orkka-Workspace: B`.

## WSP-009 Create a workspace from the switcher and land on its first-run page
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/dashboard
  - orkka-api:POST /api/workspaces
  - orkka-api:POST /api/me/active-workspace
  - i18n:workspaces
  - i18n:firstRun
since: 2026-10-26
```
**Preconditions**: Signed in.
**Steps**:
1. Open the switcher; click "New workspace".
2. Read the hint; type `e2e-ws-<stamp>`; click "Create and set up".
**Expected**:
- Hint "One workspace per job or client — its own projects, runner token and tracker connections."; the button is disabled while the name is blank and reads "Creating…" while pending.
- `POST /api/workspaces` → 200; the `orkka_ws` cookie switches to it, `POST /api/me/active-workspace` is sent and the browser reloads `/dashboard`, which (no projects) shows "No projects yet" with "Create a project".
- Within a minute the running `./orkka start` writes the new workspace's runner profile (`./orkka workspace list` lists it).

## WSP-011 A stale workspace cookie is replaced and the page reloads
```yaml
priority: P2
surface: [ui]
automation: planned
refs:
  - orkka-ui:/dashboard
  - orkka-ui:apps/local/components/workspace/WorkspaceProvider.tsx
since: 2026-10-26
```
**Preconditions**: Signed in; set cookie `orkka_ws=<ULID of an org the caller is not in>`.
**Steps**:
1. Open `/dashboard`.
**Expected**:
- The layout re-points the cookie to the caller's active workspace and hard-reloads `/dashboard`; no 403 page is shown to the user.

## WSP-012 Runner tokens, projects and tracker connections are per workspace
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/me/runner-tokens
  - orkka-api:GET /api/trackers/connections
  - orkka-api:GET /api/me/runner
since: 2026-10-26
```
**Preconditions**: Workspaces A (runner token + tracker connection) and B (none).
**Steps**:
1. `GET /api/me/runner-tokens`, `GET /api/trackers/connections`, `GET /api/me/runner` with `X-Orkka-Workspace: B`.
**Expected**:
- B has no tokens, no connections, and `hasActiveTokens:false` — nothing from A leaks.

## WSP-013 List the workspaces on this machine with `./orkka workspace list`
```yaml
priority: P2
modes: [offline]
surface: [cli]
automation: planned
refs:
  - cli:workspace
since: 2026-10-26
```
**Preconditions**: workspaces `acme` and `side` exist in Orkka and a running `./orkka start` gave each a profile on this machine (or, by hand, `./orkka connect --workspace acme --token orkr_…` and `--workspace side --token orkr_…`).
**Steps**:
1. `./orkka workspace list`.
2. `./orkka workspace set-identity acme --name "Dana Silva" --email dana@acme.com`; list again.
3. `./orkka workspace remove side`; list again.
**Expected**:
- Table `WORKSPACE  HOST  GIT AUTHOR  TRACKERS` with one row per workspace; `(git config)` until an identity is set.
- After set-identity: `Dana Silva <dana@acme.com>` on acme's row.
- After remove: `side` is gone, its `~/.orkka/workspaces/side/` folder and its tracker tokens are deleted.
**Notes**: With no workspaces it prints "No workspaces on this machine yet. Run `./orkka start`: every workspace you create in" / "Orkka gets a profile here within a minute. (By hand, advanced:" / "  ./orkka connect --workspace <name> --token orkr_...)". Remove prints "Removed the workspace 'side'. A running Orkka stops serving it after a restart (`./orkka stop`, then `./orkka start`)."

## WSP-014 One runner process connects every workspace on the machine
```yaml
priority: P1
surface: [cli, api]
automation: manual
refs:
  - cli:connect
  - orkka-api:GET /api/me/runner
since: 2026-10-26
```
**Preconditions**: Two workspace profiles (two Orkka orgs) on this machine.
**Steps**:
1. `./orkka start` (its in-process runner serves every profile; `./orkka connect` with no arguments does the same for a runner alone).
2. `GET /api/me/runner` with `X-Orkka-Workspace` of each org.
**Expected**:
- Both answer `online:true` with the same `machineName`; a task in either workspace runs on this machine with that workspace's git identity.
