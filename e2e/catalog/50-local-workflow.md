---
area: LWF
title: Local-only workflow — the stack, base folders, tracker tickets, Stories (2026-11)
modes-default: [tracker]
---

## LWF-001 The sandboxed Orkka starts, and a launch code signs a browser in
```yaml
priority: P0
modes: [all]
surface: [cli, api, ui]
automation: automated
refs:
  - cli:start
  - cli:stop
  - cli:status
  - cli:open
  - orkka-api:POST /local/launch-codes
  - orkka-api:GET /launch
  - orkka-api:POST /api/me/api-keys
  - orkka-ui:/dashboard
since: 2026-11-18
```
**Preconditions**: the clone built (`./orkka start` once, or `(cd ui && npm ci && npm run build) && dotnet build src/Orkka -c Release`); the port of `E2E_BASE_URL` free (not 6750).
**Steps**:
1. `npm run stack:start -- --fresh` (runs `dotnet src/Orkka/bin/Release/net10.0/orkka.dll start --no-browser --port <port>` in the background with `ORKKA_HOME=<sandbox>/home`, `ORKKA_SECRET_STORE=file`).
2. Read `<sandbox>/home/workspaces/_local/secrets/install-secret`; `POST /api/me/api-keys` with it as bearer.
3. `./orkka open --print` (sandbox home) — or `POST /local/launch-codes` (bearer) — and open the printed `/launch?code=…`.
4. `./orkka status`; then `./orkka stop`; `./orkka status` again.
**Expected**:
- `/health` answers `"service":"orkka"` from the one process; the first page loads without an error (the static UI is served by the host itself).
- An `orkka_key_` key with every permission; the browser lands on `/en/dashboard` with the `orkka_s` session cookie; a reused code gets 403 "This link has expired".
- Step 4: "Orkka is running at http://127.0.0.1:<port>" with pid, health, data and logs lines (exit 0); `stop` prints "Stopping Orkka (pid N)..." then "Orkka stopped."; the second `status` prints "Orkka is not running. Start it with `./orkka start`." (exit 3) and nothing listens on the port.

## LWF-002 A base folder becomes a project: a remote and a local folder
```yaml
priority: P0
surface: [api, ui]
automation: automated
refs:
  - orkka-api:POST /api/runners/fs/discover
  - orkka-api:POST /api/projects/from-folder
  - orkka-ui:/projects/new
  - orkka-ui:/p/[projectId]/repos
  - i18n:fromFolder
  - i18n:newProjectPage
since: 2026-11-18
```
**Preconditions**: `shop/api` is a checkout whose origin is `https://github.com/e2e-local/<name>.git` (a local bare remote behind the stack's git `insteadOf`); `shop/web` has commits and no origin.
**Steps**:
1. New project → Choose folder… → `shop` → Use shop: the preview lists both repositories.
2. `POST /api/projects/from-folder {path, name}`.
**Expected**:
- Preview: `api` "Remote · github.com", `web` "Local folder".
- Two repos: `api` location `remote` / provider `github` with the CONFIGURED origin (not its insteadOf rewrite — orkka-api d012406), `web` location `local`; no repo in `error` (the mirror clone honours the environment's `GIT_CONFIG_*` — orkka-api 15f5ee0).

## LWF-003 Add a tracker connection from the browser; the token stays in the secret store
```yaml
priority: P0
surface: [api, ui]
automation: automated
refs:
  - orkka-api:POST /api/trackers/connections
  - orkka-ui:/settings/trackers
since: 2026-11-18
```
**Preconditions**: the stack runs; the fake Jira listens on loopback (the runner allows it with `ORKKA_TRACKER_ALLOW_INSECURE_SITE=1`).
**Steps**:
1. `POST /api/trackers/connections {kind:"jira", site, email, token}` (Settings → Trackers → Add Jira Cloud).
2. Scan the sandbox Orkka home for the token; `pg_dump` the database and search it.
**Expected**:
- `{supported:true, ok:true, connection}`; the response never echoes the token.
- The token is in exactly one file: `workspaces/<ws>/secrets/<connection key>` — not in the SQLite database (`orkka.db` + WAL), not in a log, not in `trackers.json`, and not in the OS keychain when `ORKKA_SECRET_STORE=file` (orkka-api a4c76d5).

## LWF-004 Ticket source: To Do intake and the six-step status map
```yaml
priority: P0
surface: [api, ui]
automation: automated
refs:
  - orkka-api:PUT /api/projects/{projectId}/tracker
  - orkka-api:POST /api/trackers/connections/{connectionId}/query
  - orkka-ui:/p/[projectId]/settings
since: 2026-11-18
```
**Preconditions**: LWF-002 and LWF-003 done.
**Steps**: ask the connection for statuses; `PUT …/tracker {intakeStatuses:["To Do"], statusMap:{planning:"In Progress", inReview:null, prOpened:"Code Review", merged:"Done"}}`.
**Expected**: the stored source echoes the map; Project settings → Ticket source → Ticket status sync shows the six rows with those values.

## LWF-005 A standalone ticket: Discuss, plan, build, review, publish; Jira follows
```yaml
priority: P0
surface: [api]
automation: automated
refs:
  - orkka-api:POST /api/tasks/{taskId}/chat
  - orkka-api:GET /api/tasks/{taskId}/chat
  - orkka-api:POST /api/tasks/{taskId}/plans/{planId}/review
  - orkka-api:POST /api/tasks/{taskId}/pull-request/merge
  - i18n:taskDiscuss
since: 2026-11-18
```
**Preconditions**: `E2E_WORKFLOW_AGENTS=1` (a real agent run on the developer's AI CLI, Haiku).
**Steps**: a ticket assigned to me in To Do → wait for its task; answer a PM question if any; one Discuss question; a Jira comment; approve the plan; approve the review and publish.
**Expected**:
- The repo router places it on `api`; Discuss answers as SSE (`data: {"text":…}`).
- The comment reaches the thread as `ExternalComment`; no "was updated in Jira" note after Orkka's own status move (orkka-api cf0218a).
- `gh pr create` on the stub; the task's `publishedPrUrl` is that PR; Jira: To Do → In Progress → Code Review — never straight to Done (orkka-api 4cbb34a).

## LWF-006 A ticket naming no repository: the question routes it; a move keeps it
```yaml
priority: P1
surface: [api]
automation: automated
refs:
  - orkka-api:POST /api/tasks/{taskId}/chat/apply
since: 2026-11-18
```
**Preconditions**: `E2E_WORKFLOW_AGENTS=1`; LWF-004 done.
**Steps**: a ticket that could be either repo; move it to In Progress in Jira; answer the repository question with Discuss → Apply "api".
**Expected**: PendingQuestions with options `api`, `web`; Apply answers it (`action:"answered"`) and the task is routed to `api`; it stays live after two polls.

## LWF-007 A Story with two sub-tasks: one branch, a draft PR, ready at the end
```yaml
priority: P0
surface: [api, ui]
automation: automated
refs:
  - orkka-api:GET /api/tasks/{taskId}/code-changes/{snapshotId}
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-ui:/p/[projectId]/board
since: 2026-11-18
```
**Preconditions**: `E2E_WORKFLOW_AGENTS=1`; LWF-004 done; the project approves plans and reviews automatically.
**Steps**: a Story with two sub-tasks (all assigned to me, To Do), hands-free approvals.
**Expected**:
- One Story group, members in order, one shared branch; the first member starts at once (orkka-api 613d1c9).
- After the first lands: `gh pr create --draft`; later `gh pr edit` (checklist) and `gh pr ready`.
- The second member's clone continues the shared branch; its Code changes diff shows only its own change (orkka-api 8df8c45).
- Story + both sub-tasks → Code Review together; when the story PR merges → Done.
- Board card chip `SHOP-20 · 1/2`; the task page's Story panel lists both sub-tasks as LANDED.

## LWF-008 Azure DevOps: a PAT connection, intake, write-back and a comment
```yaml
priority: P1
surface: [api]
automation: automated
refs:
  - orkka-api:POST /api/trackers/connections
  - orkka-api:PUT /api/projects/{projectId}/tracker
since: 2026-11-18
```
**Preconditions**: the fake tracker also serves Azure DevOps (`/e2eorg`); no agent runs (auto-submit off).
**Steps**: add an ADO connection; a project on the same base folder with intake `New`, Picked up → `Active`, auto-submit off; a work item assigned to me; the dry run (PSU-009); two poll cycles before Start (PSU-010); Start; a comment on the work item.
**Expected**: a Draft task routed to `api`; the work item moves New → Active; the comment reaches the thread; the PAT is not in the SQLite database.

## LWF-009 The key screens render without errors
```yaml
priority: P1
surface: [ui]
automation: automated
refs:
  - orkka-ui:/settings/machine
  - orkka-ui:/settings/trackers
  - orkka-ui:/p/[projectId]/settings
since: 2026-11-18
```
**Preconditions**: the workflow left its project (`E2E_SCREENS_PROJECT=<slug>`).
**Steps**: `npm run test:screens` after the workflow (`E2E_KEEP_PROJECTS=1`).
**Expected**: dashboard, create-from-folder preview, board + Story chip, task detail with the Story panel and the Discuss tab, Settings → Trackers, the ticket source card with status sync, the Machine page — no 5xx, no console error, no raw i18n key; switches line up (orkka-ui b391de3).

## LWF-010 Rescan a base-folder project picks up added and removed repositories
```yaml
priority: P2
surface: [api, ui]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/rescan
  - i18n:baseFolder
since: 2026-11-18
```
**Preconditions**: a base-folder project (LWF-002).
**Steps**: add a new git repo under the base folder; `POST /api/projects/{id}/rescan`; remove it again and rescan.
**Expected**: `added` names the new repo, then `missing` names it; the Repos page's base-folder panel shows the same.

## LWF-011 Remove a tracker connection deletes its token on the machine
```yaml
priority: P1
surface: [api, ui]
automation: planned
refs:
  - orkka-api:DELETE /api/trackers/connections/{connectionId}
since: 2026-11-18
```
**Preconditions**: a connection added through LWF-003 and no project using it.
**Steps**: Settings → Trackers → Remove (`DELETE /api/trackers/connections/{id}`).
**Expected**: the connection is gone from the list; its secret file and `trackers.json` entry are gone from the sandbox.

## LWF-012 The usage page sums the runs of the last 30 days
```yaml
priority: P2
surface: [api, ui]
automation: planned
refs:
  - orkka-api:GET /api/usage/summary
  - orkka-api:GET /api/usage/daily
  - orkka-api:GET /api/usage/breakdown
  - orkka-ui:/settings/usage
  - i18n:usagePage
since: 2026-11-18
```
**Preconditions**: at least one finished agent run.
**Steps**: open Settings → Usage.
**Expected**: totals, the daily chart and the per-project breakdown agree with the three endpoints; estimated list-price spend only (nothing billed).

## LWF-013 Workspace settings: rename the workspace
```yaml
priority: P2
surface: [ui]
automation: planned
refs:
  - orkka-ui:/settings/workspace
  - i18n:workspaceSettings
  - i18n:permissions
since: 2026-11-18
```
**Preconditions**: the stack runs.
**Steps**: Settings → Workspace → change the name → Save.
**Expected**: the switcher shows the new name; the runner profile keeps serving the workspace.

## LWF-014 First run: an empty install guides to the first project
```yaml
priority: P2
surface: [ui]
automation: planned
refs:
  - i18n:firstRun
since: 2026-11-18
```
**Preconditions**: a fresh sandbox (`npm run stack:start -- --fresh`).
**Steps**: open the launch URL.
**Expected**: the first-run view explains base folders and offers "Create a project".

## LWF-015 `./orkka open` prints or opens a fresh one-time sign-in link
```yaml
priority: P2
modes: [all]
surface: [cli, api]
automation: planned
refs:
  - cli:open
  - orkka-api:POST /local/launch-codes
  - orkka-api:GET /launch
since: 2026-11-18
```
**Preconditions**: the sandboxed Orkka running (LWF-001).
**Steps**:
1. `./orkka open --print` twice.
2. Open the first link twice; open the second once.
3. `./orkka stop`; `./orkka open --print`.
**Expected**:
- Step 1: each prints only a URL `http://127.0.0.1:<port>/launch?code=…` (exit 0); the two codes differ. Without `--print` the default browser opens the link instead.
- Step 2: the first visit sets the session and redirects to `/`; the reused code gets 403 "This link has expired" ("Launch links work once, for 60 seconds. Run `./orkka open` in your Orkka folder to get a new one."); the second link still works.
- Step 3: exit 1 with "Orkka is not running. Start it with `./orkka start`." on stderr.
**Notes**: The opt-in PATH entry (`path add|remove|status`) was removed with the installed binary (R4); `./orkka doctor` only reports what the old installer left (RUN-043).

## LWF-016 The launcher's /local endpoints answer only to the install secret
```yaml
priority: P1
modes: [all]
surface: [api, cli]
automation: planned
refs:
  - orkka-api:POST /local/launch-codes
  - orkka-api:POST /local/bootstrap
  - orkka-api:POST /local/shutdown
  - cli:start
since: 2026-11-18
```
**Preconditions**: the sandboxed Orkka running (LWF-001); the sandbox's install secret; the harness `orkka_key_` key.
**Steps**:
1. `POST /local/launch-codes`, `POST /local/bootstrap`, `POST /local/shutdown` with no `Authorization`, then each with the `orkka_key_` key as bearer.
2. `POST /local/bootstrap {machineName:"e2e-<stamp>"}` with the install secret as bearer; repeat it.
3. `POST /local/bootstrap {machineName:"<101 characters>"}` (install secret).
4. `GET /api/me/runner-tokens` (key); revoke the `e2e-<stamp>` tokens (cleanup).
**Expected**:
- Step 1: every call → 401; nothing is minted and Orkka keeps running — a browser session or an API key is never enough.
- Step 2: 200 `[{workspaceId, name, token}]`, one entry per workspace, `Cache-Control: no-store`; the first call mints an `orkr_…` token for each workspace with no live token named `e2e-<stamp>`; the repeat returns `token:null` for all of them (nothing new minted).
- Step 3: 400 "Machine name is longer than 100 characters."
- Step 4: the minted tokens are listed by name with a 90-day `expiresAt`; the audit log has "Runner token "e2e-<stamp>" created by the launcher".
**Notes**: `./orkka start` calls bootstrap to write the runner's workspace profiles (and re-polls it, so a workspace created in the UI gets a profile within a minute); `./orkka stop` calls shutdown (202, then the process exits — LWF-001); `./orkka open` calls launch-codes (LWF-015). Shutdown is not called here with the secret: it would stop the sandbox.

## LWF-017 Blob URLs: a signed link or the session, nothing else
```yaml
priority: P1
modes: [all]
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/tasks/{taskId}/attachments/upload-url
  - orkka-api:PUT /blobs/{container}/{**blob}
  - orkka-api:GET /blobs/{container}/{**blob}
  - orkka-api:HEAD /blobs/{container}/{**blob}
  - orkka-api:POST /api/tasks/{taskId}/attachments
since: 2026-11-18
```
**Preconditions**: the sandboxed Orkka; an `e2e-*` project with one task.
**Steps**:
1. `POST /api/tasks/{id}/attachments/upload-url {fileName:"note.svg", contentType:"image/svg+xml", …}` → `{uploadUrl, blobName}`.
2. `PUT <uploadUrl>` with the raw bytes (no cookie, no bearer); `PUT` the same path with `sig` changed by one character; `PUT` it after `exp` has passed.
3. `POST /api/tasks/{id}/attachments {blobName, …}`; take the returned `fileUrl`.
4. `GET`/`HEAD /blobs/task-attachments/<blobName>` with no credentials; again with the session cookie; again with `?filename=note.svg`.
**Expected**:
- Step 2: 204 for the signed URL; 403 for the tampered or expired one; a blob name escaping the container (`../`) → 400/404, never a file outside `{DataDir}/blobs`.
- Step 4: unauthenticated and unsigned → 403; with the session → 200 with `Content-Type: image/svg+xml` and `X-Content-Type-Options: nosniff` (HEAD: headers only); `?filename=` adds `Content-Disposition: attachment; filename=note.svg`; a missing blob → 404.
**Notes**: a signed-blob request skips the cookie CSRF check (`LocalRequestGuard.IsSignedBlobRequest`), so a browser upload needs no `X-Orkka-Csrf`.
