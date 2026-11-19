---
area: NEG
title: Cross-cutting negatives — permissions, plan limits, rate limits, auth, malformed input
modes-default: [all]
---

Negative behaviour that spans areas: the 18-permission matrix over org API keys (a key acts as a Member with an intersected set — the cheapest way to impersonate "member without X"), plan-limit 402 shapes, 429 rate limits, suspension, revoked keys, route constraints, lifecycle guards, CORS, health and OpenAPI exposure. Keys used below are Owner-minted `orkka_key_…` values named by their permission set (e.g. `K[TasksCreate]`).

## NEG-001 Return 403 on task writes without the matching Tasks permission
```yaml
priority: P0
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/tasks
  - orkka-api:PATCH /api/tasks/{id}
  - orkka-api:DELETE /api/tasks/{id}
  - orkka-api:POST /api/tasks/{id}/submit
  - orkka-api:PATCH /api/tasks/{id}/lane
  - orkka-api:POST /api/tasks/{taskId}/messages
```
**Preconditions**: project `P`; a Drafting task `T`; key `K[DesignReview]` (holds none of the Tasks permissions).
**Steps**:
1. `POST /api/projects/{P}/tasks {title:"x"}`.
2. `PATCH /api/tasks/{T} {title:"y"}`; `POST /api/tasks/{T}/archive`.
3. `DELETE /api/tasks/{T}`.
4. `POST /api/tasks/{T}/submit`; `PATCH /api/tasks/{T}/lane {targetLane:"Ready"}`; `POST /api/tasks/{T}/messages {content:"hi"}`.
**Expected**:
- Step 1 → 403 (TasksCreate); step 2 → 403 (TasksEdit); step 3 → 403 (TasksDelete); step 4 → 403 ×3 (TasksExecute).
- `GET /api/tasks/{T}` with the same key → 200.
**Notes**: none.

## NEG-002 Return 403 on plan review without PlansReview
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/tasks/{taskId}/plans/{planId}/review
```
**Preconditions**: task with a plan (id `PL`) — or any ULIDs (guard runs first); `K[TasksCreate]`.
**Steps**:
1. `POST /api/tasks/{T}/plans/{PL}/review {decision:"approve"}`.
**Expected**:
- 403 before any lookup (the guard is the first line of the handler); `GET /api/tasks/{T}/plans` → 200.
**Notes**: none.

## NEG-003 Return 403 on code review and merge without the CodeReview permissions
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/tasks/{taskId}/pull-request/reviews
  - orkka-api:POST /api/tasks/{taskId}/pull-request/comments
  - orkka-api:POST /api/tasks/{taskId}/pull-request/merge
  - orkka-api:POST /api/tasks/{taskId}/resolve-merge
```
**Preconditions**: `K[CodeReviewReview]` and `K[TasksCreate]`.
**Steps**:
1. With `K[TasksCreate]`: `POST …/pull-request/reviews {decision:"approve"}`, `POST …/pull-request/comments {…}`.
2. With `K[CodeReviewReview]`: `POST …/pull-request/merge`, `POST …/resolve-merge {…}`.
**Expected**:
- Step 1 → 403 ×2 (CodeReviewReview); step 2 → 403 ×2 (CodeReviewMerge — reviewing does not imply merging).
**Notes**: none.

## NEG-004 Return 403 on document writes without DocumentsManage
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/documents
  - orkka-api:PATCH /api/projects/{projectId}/documents/{id}
  - orkka-api:DELETE /api/projects/{projectId}/documents/{id}
```
**Preconditions**: `K[TasksCreate]`; a document `D` in `P`.
**Steps**:
1. `POST /api/projects/{P}/documents {title:"x", content:"y"}`; `PATCH …/documents/{D} {title:"z"}`; `DELETE …/documents/{D}`.
**Expected**:
- 403 ×3; `GET …/documents` → 200.
**Notes**: none.

## NEG-005 Return 403 on agent chat and configuration without AgentsChat / AgentsConfigure
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/agents/{agentId}/conversations
  - orkka-api:PUT /api/projects/{projectId}/agents/{agentId}
  - orkka-api:POST /api/projects/{projectId}/skills
  - orkka-api:POST /api/projects/{projectId}/memories
```
**Preconditions**: `K[AgentsChat]` and `K[AgentsConfigure]`; agent `A`.
**Steps**:
1. With `K[AgentsConfigure]`: `POST …/agents/{A}/conversations {}`.
2. With `K[AgentsChat]`: `PUT …/agents/{A} {name:"x"}`, `POST …/skills {…}`, `POST …/memories {…}`.
**Expected**:
- Step 1 → 403 (AgentsChat); step 2 → 403 ×3 (AgentsConfigure).
**Notes**: none.

## NEG-006 Return 403 on design actions without DesignReview
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/design-assets/upload
  - orkka-api:POST /api/projects/{projectId}/design-assets/{assetId}/approve
  - orkka-api:PUT /api/projects/{projectId}/brand
```
**Preconditions**: `K[TasksCreate]`.
**Steps**:
1. `POST …/design-assets/upload {…}`; `POST …/design-assets/{anyUlid}/approve`; `PUT …/brand {…}`.
**Expected**:
- 403 ×3; `GET …/design-assets` and `GET …/brand` → 200.
**Notes**: none.

## NEG-007 Return 403 on project create / manage / delete without the Projects permissions
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/projects
  - orkka-api:PATCH /api/projects/{id}
  - orkka-api:DELETE /api/projects/{id}
  - orkka-api:PUT /api/projects/{projectId}/tracker
since: 2026-09-15
```
**Preconditions**: API key with all permissions except `ProjectsCreate`, `ProjectsManage`, `ProjectsDelete`; an `e2e-*` project.
**Steps**:
1. `POST /api/projects`; `PATCH /api/projects/{id}`; `PUT /api/projects/{id}/tracker {…}`; `DELETE /api/projects/{id}`.
**Expected**:
- Each → 403 naming the missing permission; nothing changes.

## NEG-008 Return 403 on repo writes without ReposManage
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/repos
  - orkka-api:POST /api/projects/{projectId}/repos/local
  - orkka-api:PUT /api/projects/{id}/run-config
since: 2026-09-15
```
**Preconditions**: API key without `ReposManage`.
**Steps**:
1. `POST …/repos`; `POST …/repos/local`; `PUT /api/projects/{id}/run-config`.
**Expected**:
- Each → 403.

## NEG-010 Return 403 on the audit log without AuditView
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/audit
```
**Preconditions**: `K[TasksCreate]`.
**Steps**:
1. `GET /api/audit` with `K`.
2. `GET /api/audit` with a key holding `AuditView`.
**Expected**:
- Step 1 → 403; step 2 → 200.
**Notes**: the members/invites half of this case went with members (2026-11-17).

## NEG-011 Return 403 on autopilot and schedules without TasksExecute and its sub-gates
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/autopilot
  - orkka-api:DELETE /api/projects/{projectId}/autopilot
  - orkka-api:POST /api/projects/{projectId}/schedules
```
**Preconditions**: `K[TasksCreate]`; `K[TasksExecute]` (no BillingManage/ProjectsManage).
**Steps**:
1. With `K[TasksCreate]`: `POST …/autopilot {capUsd:1}`; `DELETE …/autopilot`; `POST …/schedules {…}`.
2. With `K[TasksExecute]`: `POST …/autopilot {uncapped:true}`; `POST …/autopilot {capUsd:1, answerEveryQuestion:true}`.
**Expected**:
- Step 1 → 403 ×3.
- Step 2 → 403 ×2 (`uncapped` ⇒ BillingManage; `answerEveryQuestion` ⇒ ProjectsManage); a plain `{capUsd:1}` from the same key would be accepted (stop it immediately, no spend expected as no task is queued).
**Notes**: none.

## NEG-012 Allow every read with a minimal key
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/org/me
  - orkka-api:GET /api/projects
  - orkka-api:GET /api/projects/{projectId}/board
  - orkka-api:GET /api/projects/{projectId}/agents
  - orkka-api:GET /api/usage/summary
  - orkka-api:GET /api/models
  - orkka-api:GET /api/inbox
```
**Preconditions**: `K[DesignReview]` (a key must hold ≥ 1 permission).
**Steps**:
1. Call each listed GET plus `GET /api/projects/{P}/tasks`, `/repos`, `/documents`, `/milestones`, `/executions`, `/api/notifications`.
**Expected**:
- 200 for every one; `GET /api/org/me` reports `role:"Member"` and `permissions:["DesignReview"]`.
**Notes**: none.

## NEG-013 Answer 404, not 403, for an unreachable or foreign project
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{id}
  - orkka-api:GET /api/projects/{projectId}/tasks
  - orkka-api:PATCH /api/projects/{id}
```
**Preconditions**: owner key; project-scoped membership ON and a secondary Member NOT assigned to project `P` (manual half).
**Steps**:
1. `GET /api/projects/01ARZ3NDEKTSV4RRFFQ69G5FAV`, `…/tasks`, `PATCH …` (random well-formed ULID — belongs to no one / another org).
2. (Manual) as the unassigned Member: `GET /api/projects/{P}`, `GET /api/projects/{P}/tasks`, open `/p/{P.slug}`.
**Expected**:
- Step 1 → 404 ×3 (existence is never confirmed across orgs).
- Step 2 → 404 (not 403) and `GET /api/projects` omits `P`; the UI shows the 404 page.
**Notes**: none.

## NEG-019 Reject a revoked or malformed API key
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:DELETE /api/me/api-keys/{keyId}
  - orkka-api:GET /api/org/me
```
**Preconditions**: a key minted then revoked (KEY-005).
**Steps**:
1. `GET /api/org/me` with the revoked key; with `Bearer orkka_key_` (empty); with `Bearer orkka_key_AAAA`.
2. With no Authorization header.
**Expected**:
- 401 for each; body carries no hint whether the key ever existed.
**Notes**: none.

## NEG-020 Answer 404 for a malformed ULID route parameter
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/tasks/{id}
  - orkka-api:POST /api/notifications/{id}/read
  - orkka-api:DELETE /api/me/api-keys/{keyId}
  - orkka-api:src/Orkka.Local/Infrastructure/UlidRouteConstraint.cs
```
**Preconditions**: owner key.
**Steps**:
1. `GET /api/tasks/not-a-ulid`; `POST /api/notifications/123/read`; `DELETE /api/me/api-keys/xyz`.
2. `GET /api/projects/not-a-real-slug`.
**Expected**:
- Step 1 → 404 (the `:ulid` route constraint does not match, so no endpoint runs); never 500.
- Step 2 → 404 (slug lookup miss).
**Notes**: none.

## NEG-022 Reject illegal lane moves through the API
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:PATCH /api/tasks/{id}/lane
  - orkka-api:src/Modules/Orkka.Modules.Tasks/Features/Tasks/MoveTaskLaneEndpoint.cs
```
**Preconditions**: Drafting task `T`; `K[TasksExecute]`.
**Steps**:
1. `PATCH /api/tasks/{T}/lane {targetLane:"Developing"}`.
2. `PATCH …/lane {targetLane:"Drafting"}`.
3. `PATCH …/lane {targetLane:"Nowhere"}`.
4. `PATCH …/lane {targetLane:"Ready"}` then `{targetLane:"Ready"}` again.
**Expected**:
- Step 1 → 400 "Tasks in Drafting can only be submitted to Ready."; step 2 → 400 "Cannot move a task back to Drafting."; step 3 → 400 "'Nowhere' is not a valid pipeline lane.".
- Step 4 → 200 then 400 "Task is already in this lane."; a forward move with unmet dependencies → 400 "This task can't move forward yet. Waiting on: …"; moving a task whose PR merged backward → 400 "This task's branch was already merged…".
- UI: forward drags snap back (board area); backward-only is enforced client-side.
**Notes**: none.

## NEG-023 Refuse deleting a task that left Drafting
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:DELETE /api/tasks/{id}
  - orkka-api:POST /api/tasks/{id}/archive
```
**Preconditions**: a task in `Ready` (submitted, then blocked or still queued); owner key.
**Steps**:
1. `DELETE /api/tasks/{T}`.
2. `POST /api/tasks/{T}/archive`.
**Expected**:
- Step 1 → 400 "Only tasks in Drafting can be deleted. Use archive instead."; task still present.
- Step 2 → 200 (archive is the sanctioned path); UI shows "Delete" only on Drafting tasks.
**Notes**: none.

## NEG-024 Refuse cross-origin browser calls from a foreign origin
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/org/me
  - orkka-api:PUT /api/org/me/preferences
  - orkka-api:src/Orkka.Local/Auth/LocalRequestGuard.cs
```
**Preconditions**: the sandboxed Orkka; a browser session cookie; the API key.
**Steps**:
1. `OPTIONS /api/org/me` with `Origin: https://evil.example`, `Access-Control-Request-Method: GET`; then the same with `Origin: http://127.0.0.1:<port>`.
2. With the session cookie: `PUT /api/org/me/preferences {…}` with `Origin: https://evil.example` and `X-Orkka-Csrf: 1`; then with the own origin and no `X-Orkka-Csrf`; then with both right.
**Expected**:
- Step 1: no `Access-Control-Allow-Origin` header for any origin — the host has no CORS policy; the UI is served from the same origin.
- Step 2: 403 for the foreign origin and for the missing CSRF header; 200 only with the own origin AND `X-Orkka-Csrf: 1`. A bearer (API key) request needs neither.
**Notes**: the Host allowlist (421 for any other Host header) is in the smoke suite.

## NEG-025 Serve the health endpoint anonymously
```yaml
priority: P0
surface: [api]
automation: planned
refs:
  - orkka-api:GET /health
  - orkka-api:src/Orkka.Local/Infrastructure/HealthResponse.cs
```
**Preconditions**: the sandboxed Orkka.
**Steps**:
1. `GET http://127.0.0.1:<port>/health` without Authorization or cookie.
**Expected**:
- 200 `{"service":"orkka","status":"Healthy"|"Degraded","checks":[{name,status,description}]}` (a dependency check reports Degraded, never Unhealthy, so it never fails the endpoint); no auth challenge; response < 2 s.
**Notes**: `./orkka start` and `./orkka status` read `service` to tell Orkka from another program on the port.

## NEG-027 Answer unknown routes with 404 and no HTML
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:GET /health
```
**Preconditions**: owner key.
**Steps**:
1. `GET /api/does-not-exist`, `POST /api/projects/{P}/nope` on the sandboxed Orkka.
**Expected**:
- 404 each, empty or JSON body (never an HTML error page, never 500); `Content-Type` absent or `application/json`/`application/problem+json`.
**Notes**: none.

## NEG-028 Require authentication on API and hubs
```yaml
priority: P0
surface: [api, hub]
automation: planned
refs:
  - orkka-api:GET /api/org/me
  - orkka-api:GET /api/projects
  - hub:TaskHub.TaskCreated
```
**Preconditions**: none.
**Steps**:
1. `GET /api/org/me`, `GET /api/projects`, `POST /api/projects {}` without Authorization; with `Bearer not.a.jwt`.
2. `POST /hubs/tasks/negotiate` and `POST /hubs/projects/negotiate` without `access_token`.
**Expected**:
- 401 for all (no 403, no 500); `WWW-Authenticate` present on bearer failures.
**Notes**: none.

## NEG-029 Return ProblemDetails on malformed JSON and missing fields
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/projects
  - orkka-api:POST /api/me/api-keys
```
**Preconditions**: owner session token.
**Steps**:
1. `POST /api/projects` with body `{"name":` (truncated JSON).
2. `POST /api/projects {}` (missing name/key).
3. `POST /api/me/api-keys {"permissions":"TasksCreate"}` (wrong type).
**Expected**:
- 400 each with a FastEndpoints ProblemDetails/errors body the UI's `extractErrorMessage` can render (field names present); never 500.
**Notes**: none.

## NEG-030 A project whose slug is exactly 26 characters still resolves by slug
```yaml
priority: P1
surface: [api, ui]
automation: automated
refs:
  - orkka-api:GET /api/projects/{id}
  - orkka-api:GET /api/projects/{projectId}/board
  - orkka-api:src/Modules/Orkka.Modules.Projects/PublicApi/ProjectResolver.cs
  - orkka-ui:/p/[projectId]/board
  - orkka-ui:/p/[projectId]/settings
since: 2026-09-15
```
**Preconditions**: Owner API key; a project name whose generated slug has exactly 26 characters (e.g. `e2e-neg-20260915-1105abcde`).
**Steps**:
1. `POST /api/projects {name, key:"E2E"}` → note `slug` (length 26).
2. `GET /api/projects/{slug}`; `GET /api/projects/{slug}/board`.
3. In the browser open `/p/{slug}/board` and `/p/{slug}/settings`.
**Expected**:
- Both GETs answer 200 for the project just created; the board shows its lanes and the settings page renders.
**Notes**: Fixed 2026-11-19. Before that, `ProjectResolver.ResolveAsync` ran `Ulid.TryParse` first and the Ulid library accepts any 26-character string, so a 26-character slug was read as a missing id and every slug-addressed endpoint answered 404. The resolver now takes a value as an id only in Orkka's own uppercase Crockford spelling; anything else is looked up as a slug first, then read as an id (a lowercased id, or digits only). The integration test `ProjectEndpointTests.GetProject_BySlugOfIdLength_Resolves` covers 25/26/27 characters.

## NEG-031 Removed cloud-execution routes answer 404; org settings have no hybrid switch
```yaml
priority: P1
surface: [api]
automation: automated
refs:
  - orkka-api:GET /api/org/settings
  - orkka-api:POST /api/projects
since: 2026-11-17
```
**Preconditions**: Harness API key; an `e2e-neg-…` project.
**Steps**:
1. `GET /api/me/execution-preference`; `PUT /api/me/execution-preference {target:"MyRunner"}`.
2. `POST /api/projects/{id}/tasks/execution-target {}`.
3. `GET /api/org/settings`.
**Expected**:
- Steps 1–2: 404 (or 405) — the routes were removed with cloud execution (orkka-api e3a3a06), as was `POST /api/tasks/{id}/execution-target`.
- Step 3: 404 — org settings went with the cloud product (2026-11-17), so there is no `allowHybridExecution` anywhere.

## NEG-032 The /internal relays refuse everyone but this process
```yaml
priority: P1
modes: [all]
surface: [api]
automation: planned
refs:
  - orkka-api:POST /internal/runner/utility
  - orkka-api:POST /internal/runner/tracker-query
  - orkka-api:POST /internal/runner/repo/read
  - orkka-api:src/Orkka.Local/Auth/LocalRequestGuard.cs
since: 2026-11-18
```
**Preconditions**: the sandboxed Orkka; the install secret, the `orkka_key_` key and a browser session.
**Steps**:
1. `POST /internal/runner/utility`, `/internal/runner/tracker-query`, `/internal/runner/repo/read` with each of: no credentials, the install secret, the API key, the session cookie (+ `Origin` and `X-Orkka-Csrf: 1`), and an `X-Worker-Key` header holding a guess.
**Expected**:
- Every call → 403 "Forbidden." before any endpoint runs; the runner receives nothing.
**Notes**: the relays carry the host's own calls to the runner over loopback; they need the per-process `WorkerAuth:ApiKey` (random, memory only) as `X-Worker-Key` AND a loopback remote address. The rest of `/internal/runner/*` is listed in `sync-ignore.json` under this case.
