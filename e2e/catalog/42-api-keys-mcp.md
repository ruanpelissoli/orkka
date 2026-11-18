---
area: KEY
title: API keys and MCP
modes-default: [all]
---

Personal org API keys (`orkka_key_…`) minted on `/settings/api-keys`, accepted by every REST endpoint as the default bearer scheme, and the stateless MCP endpoint `POST /mcp`. A key acts as its creator with `Role = Member` and an intersected permission set; `ListTools` on MCP is the authorization. Known gap (P2): project binding is enforced on MCP only.

## KEY-001 Render the API keys panel
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:GET /api/me/api-keys
  - orkka-ui:/settings/api-keys
  - orkka-ui:packages/ui/src/components/settings/ApiKeysPanel.tsx
  - i18n:apiKeysPage
```
**Preconditions**: owner signed in.
**Steps**:
1. Open `/settings/api-keys`.
**Expected**:
- Tab "API keys" highlighted; container `data-testid="api-keys-panel"`.
- Card "API keys" with intro "Personal keys for the Orkka REST API and the MCP endpoint…", button "New key", and either "No API keys yet." or one `api-key-row` per key.
- Card "Connect Claude Code / Cursor" with two copyable commands: `claude mcp add --transport http orkka http://127.0.0.1:<port>/mcp --header "Authorization: Bearer <YOUR_KEY>"` and the JSON `mcpServers` snippet.
**Notes**: none.

## KEY-002 Fill the New key modal and observe its validation
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:/settings/api-keys
  - i18n:apiKeysPage
  - i18n:uiMisc
```
**Preconditions**: at least one project exists.
**Steps**:
1. Click "New key".
2. Observe the empty form; uncheck every permission; type a name.
3. Re-check one permission.
4. Open the "Project" select.
**Expected**:
- Modal title "New key"; "Name" input (placeholder "Claude Code on my laptop", `maxLength=100`, autofocus); "Project" select defaulting to "Any project I can reach" plus one option per project; "Permissions" checkboxes (only permissions the caller holds — all 18 for an Owner) with hint "Only what you hold yourself can be granted. Read tools are always available."; 6 preselected: Create tasks, Execute tasks, Review plans, Review code, Merge pull requests, Chat with agents.
- "Create key" disabled while name is blank or no permission is checked; enabled after step 3.
- "Cancel" closes.
**Notes**: none.

## KEY-003 Mint a key and see the secret exactly once
```yaml
priority: P0
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/me/api-keys
  - orkka-api:GET /api/me/api-keys
  - orkka-ui:/settings/api-keys
  - i18n:apiKeysPage
```
**Preconditions**: fewer than 10 active keys.
**Steps**:
1. "New key" → Name `e2e-key-{stamp}`, keep defaults, click "Create key".
2. Read the minted modal; click "Done".
3. `GET /api/me/api-keys`.
4. Call `GET /api/org/me` with `Authorization: Bearer <rawKey>`.
**Expected**:
- `POST /api/me/api-keys {name, permissions:[6], projectId:null}` → 200 `{key:{id,name,prefix,permissions,projectId:null,revoked:false,lastUsedAt:null}, rawKey}`; `rawKey` starts with `orkka_key_`.
- Modal "Your new API key": amber "Copy it now — it will not be shown again.", the raw key, "Add it to Claude Code in one command:" with the `claude mcp add …` line embedding the key, "Done".
- Row: name, `<16-char prefix>…`, badge "Active", permission labels joined by " · ", "never used".
- Step 3 never returns the raw key; step 4 → 200 (key works).
**Notes**: none.

## KEY-004 Show last-used time and bound project on a key row
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/me/api-keys
  - orkka-api:GET /api/me/api-keys
  - orkka-ui:/settings/api-keys
  - i18n:apiKeysPage
```
**Preconditions**: one project `P`.
**Steps**:
1. `POST /api/me/api-keys {name:"e2e-bound", permissions:["TasksCreate"], projectId:P.id}` (slug also accepted).
2. `GET /api/projects` with the new key; wait ~2 s; reload `/settings/api-keys`.
**Expected**:
- Row text includes "Project: {P.name}" and "last used {relative time}" (LastUsedAt stamped fire-and-forget).
- Response `projectId` equals `P.id`.
**Notes**: none.

## KEY-005 Revoke a key and see it stop working
```yaml
priority: P0
surface: [ui, api]
automation: planned
refs:
  - orkka-api:DELETE /api/me/api-keys/{keyId}
  - orkka-ui:/settings/api-keys
  - i18n:apiKeysPage
```
**Preconditions**: key from KEY-003 with its raw value.
**Steps**:
1. Click "Revoke" on the row; dialog "Revoke key?" / "\"{name}\" stops working immediately. This cannot be undone." → click "Revoke".
2. `GET /api/org/me` with the revoked raw key.
3. `GET /api/me/api-keys`.
**Expected**:
- `DELETE /api/me/api-keys/{id}` → 204; badge "Revoked", "Revoke" button gone.
- Step 2 → 401.
- Step 3 lists the key with `revoked:true` (kept for history); revoking it again → 404 or 204 (idempotent), never 500.
**Notes**: none.

## KEY-006 Reject a key name that is empty or longer than 100 characters
```yaml
priority: P2
surface: [api, ui]
automation: planned
refs:
  - orkka-api:POST /api/me/api-keys
```
**Preconditions**: owner session token.
**Steps**:
1. `POST /api/me/api-keys {name:"", permissions:["TasksCreate"]}`.
2. `POST /api/me/api-keys {name:"a"*101, permissions:["TasksCreate"]}`.
**Expected**:
- Both → 400 (name length 1–100). UI input has `maxLength=100` so the second case is API-only.
**Notes**: none.

## KEY-007 Refuse an 11th active key
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/me/api-keys
  - orkka-api:DELETE /api/me/api-keys/{keyId}
```
**Preconditions**: owner session; start with N active keys.
**Steps**:
1. Mint keys until 10 are active.
2. Mint one more.
3. Revoke one, mint again.
4. Cleanup: revoke all `e2e-*` keys.
**Expected**:
- Step 2 → 409 "You already have 10 active keys (max 10). Revoke one first.".
- Step 3 → 200 (revoked keys do not count).
**Notes**: none.

## KEY-008 Refuse minting a key while authenticated with a key
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/me/api-keys
```
**Preconditions**: any active key.
**Steps**:
1. `POST /api/me/api-keys {name:"nested", permissions:["TasksCreate"]}` with `Authorization: Bearer orkka_key_…`.
**Expected**:
- 403 "API keys can't create API keys — sign in to manage them.".
**Notes**: none.

## KEY-009 Reject unknown permissions, an empty set and an unknown project
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/me/api-keys
```
**Preconditions**: owner session.
**Steps**:
1. `POST /api/me/api-keys {name:"x", permissions:["TasksMake"]}`.
2. `POST /api/me/api-keys {name:"x", permissions:[]}`.
3. `POST /api/me/api-keys {name:"x", permissions:["TasksCreate"], projectId:"01ARZ3NDEKTSV4RRFFQ69G5FAV"}`.
**Expected**:
- Each → 400 with a ProblemDetails body naming the field.
**Notes**: none.

## KEY-010 Refuse granting a permission the minting member does not hold
```yaml
priority: P2
surface: [api, ui]
automation: manual
refs:
  - orkka-api:POST /api/me/api-keys
  - orkka-ui:/settings/api-keys
```
**Preconditions**: secondary account is a Member of the test org with `[TasksCreate, AgentsChat]` only.
**Steps**:
1. As that Member, open `/settings/api-keys` → "New key" and count the permission checkboxes.
2. As that Member: `POST /api/me/api-keys {name:"esc", permissions:["BillingManage"]}`.
**Expected**:
- Step 1: only "Create tasks" and "Chat with agents" are offered.
- Step 2 → 403 "You can only grant permissions you hold yourself.".
**Notes**: needs a non-Owner Clerk session (keys cannot mint keys), hence manual.

## KEY-011 Treat a key as a Member on Owner-only endpoints
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:PUT /api/org
  - orkka-api:GET /api/org/me
```
**Preconditions**: Owner-minted key with ALL permissions.
**Steps**:
1. `PUT /api/org {name:"x"}`.
2. `GET /api/org/me` with the key.
**Expected**:
- Step 1 → 403 (`IsOwner` is false by construction for keys).
- Step 2 → `role:"Member"` with the key's permissions.
**Notes**: org settings and member-role endpoints went with the cloud product (2026-11-17).

## KEY-014 Document that a project-bound key is NOT enforced on REST (known gap)
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/me/api-keys
  - orkka-api:GET /api/projects/{id}
  - orkka-api:src/Modules/Orkka.Modules.Organizations/PublicApi/OrgApiKeyValidator.cs
```
**Preconditions**: two projects `A`, `B`; key bound to `A` (KEY-004).
**Steps**:
1. With the bound key: `GET /api/projects/{B.id}` and `GET /api/projects`.
**Expected**:
- CURRENT behaviour: 200 for both — `api_key_project` is read only by MCP (`McpCaller.BoundProjectId`), nothing on the REST path consults it.
- DESIRED behaviour (tracked gap): 404 for `B`, list filtered to `A`.
**Notes**: assert the current behaviour and flag it in the run report; flip the expectation when the API closes the gap.

## KEY-015 List only the MCP tools the key's permissions allow
```yaml
priority: P0
surface: [api]
automation: planned
refs:
  - orkka-api:POST /mcp
  - orkka-api:src/Modules/Orkka.Modules.Agents/Features/Mcp/OrkkaMcpToolServer.cs
```
**Preconditions**: key `K-read` with `[DesignReview]` only; key `K-full` with all 18.
**Steps**:
1. `POST /mcp` (JSON-RPC `initialize`, then `tools/list`, headers `Authorization: Bearer <key>`, `Accept: application/json, text/event-stream`) with `K-read`.
2. Same with `K-full`.
3. `tools/call create_tasks` with `K-read`.
**Expected**:
- `K-read` lists the 14 read tools (`list_projects, get_project, list_tasks, get_task, get_task_history, list_documents, get_document, list_milestones, list_repos, list_agents, list_skills, list_schedules, get_inbox, get_autopilot_status`) and NO write tool.
- `K-full` additionally lists `create_tasks, submit_task, answer_task, approve_plan, reject_plan, review_pull_request, merge_pull_request, start_autopilot, stop_autopilot, chat_with_agent`.
- Step 3 → JSON-RPC error (unknown/unauthorized tool), never a created task.
**Notes**: none.

## KEY-016 Create and read a task through MCP with a stateless HTTP client
```yaml
priority: P0
surface: [api]
automation: planned
refs:
  - orkka-api:POST /mcp
  - orkka-api:GET /api/projects/{projectId}/tasks
```
**Preconditions**: `K-full`; project `P`.
**Steps**:
1. `tools/call create_tasks {project:P.slug, tasks:[{title:"e2e mcp {stamp}", description:"…"}]}`.
2. `tools/call get_task` / `list_tasks {project:P.slug}`.
3. `GET /api/projects/{P.id}/tasks`.
**Expected**:
- Step 1 result names the created task (number/code); step 2 returns it in Drafting; step 3 lists it.
- No credits spent (task not submitted).
**Notes**: each call is an independent HTTP POST — the server is stateless (no session id required).

## KEY-017 Submit a task and chat with an agent through MCP
```yaml
priority: P1
modes: [hybrid-repo]
surface: [api]
automation: planned
costUsd: 0
refs:
  - orkka-api:POST /mcp
  - orkka-api:GET /api/tasks/{id}
```
**Preconditions**: task from KEY-016; `K-full`; cheapest Sonnet/Haiku model pinned.
**Steps**:
1. `tools/call submit_task {task}`.
2. `tools/call chat_with_agent {project, agent:"Luna", message:"Reply with the single word pong."}`.
3. `GET /api/tasks/{id}`.
**Expected**:
- Step 1: task leaves Drafting (`Ready`/`InQueue`); step 3 confirms `currentLane != "Drafting"`.
- Step 2 returns an assistant reply containing "pong".
- An `McpToolCall` audit row per call (see AUD-013).
**Notes**: none.

## KEY-018 Enforce project binding on MCP
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:POST /mcp
```
**Preconditions**: projects `A`, `B`; key `K-A` bound to `A` with `[TasksCreate]`.
**Steps**:
1. `tools/call list_projects` with `K-A`.
2. `tools/call create_tasks {project:B.slug, tasks:[{title:"leak {stamp}"}]}` with `K-A`.
3. `GET /api/projects/{A.id}/tasks` and `GET /api/projects/{B.id}/tasks`.
**Expected**:
- Step 1 lists only `A`.
- Step 2 creates the task in `A` (the `project` argument is ignored) — `B` gains nothing.
**Notes**: none.

## KEY-020 Reject an API key on SignalR hubs
```yaml
priority: P1
surface: [hub, api]
automation: planned
refs:
  - hub:TaskHub.NotificationCreated
  - orkka-api:src/Orkka.Local/OrkkaHost.cs
```
**Preconditions**: `K-full`.
**Steps**:
1. `POST http://127.0.0.1:<port>/hubs/tasks/negotiate?access_token=<orkka_key_…>`.
2. Same with the owner's Clerk session token.
**Expected**:
- Step 1 → 401 (HubAccess policy lists only JwtBearer + WorkerApiKey).
- Step 2 → 200 negotiate payload.
**Notes**: none.

## KEY-021 Require authentication on /mcp
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:POST /mcp
```
**Preconditions**: none.
**Steps**:
1. `POST /mcp` `tools/list` without Authorization.
2. With `Authorization: Bearer orkka_key_definitelyinvalid`.
**Expected**:
- Both → 401; no tool list leaks.
**Notes**: none.
