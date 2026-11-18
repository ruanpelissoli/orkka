---
area: MEM
title: Memory — project memory (decisions, conventions, learnings)
modes-default: [all]
---

Page `/p/{slug}/memory` → `MemoryPage` (`data-testid="memory-page"`). Header card: intro text, **What would an agent see?** (`memory-preview-open`), **Add memory** (`memory-add`, AgentsConfigure only); filters Search (300 ms debounce), Type (`All types` / `Rules (decisions, conventions, preferences)` / one of Decision, Convention, Preference, Learning, Mistake, Pattern), Scope (`Team + all agents` / `Shared with the team` / `Private to {agent}`), View (Active / Archived). Rows `memory-row`: type badge, "Pinned" badge, title, content, `shared with the team | private to {name} · by a person | by an agent | by Orkka · {time} · used N times`, buttons **Pin/Unpin** (`memory-pin`), **Edit**, **Archive/Restore**, **Delete**. **Load more (shown of total)** on `nextCursor`. Editor modal (Title ≤ 200, Content ≤ 4000, Type, Scope, "Pin — always show this to agents", Cancel / **Create** | **Save** `memory-save`). Preview modal (query textarea, "As agent" select, **Retrieve** `memory-preview-run`, results `memory-preview-results`). Server: `MemoryRules` TitleMax 200, ContentMax 4000, MaxPerProject 2000 (409). Writes need `AgentsConfigure`; reads open. Nav key `memory` hidden in the simplified experience.

## MEM-001 Render the Memory page with filters and empty state
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/memory
  - orkka-api:GET /api/projects/{projectId}/memories
  - i18n:memoryPage
```
**Preconditions**: fresh Ready project (no memories); Owner session.
**Steps**:
1. Open `/p/{slug}/memory`.
**Expected**:
- Sidebar "Memory" highlighted; heading "Memory" with subtitle "What the team knows about this project…"; container `memory-page`.
- Buttons "What would an agent see?" (`memory-preview-open`) and "Add memory" (`memory-add`); inputs labelled "Search" (placeholder "oauth, migrations, tailwind…"), "Type", "Scope", "View" with the option sets listed above; the Scope select lists one "Private to {name}" option per project agent.
- `GET /api/projects/{p}/memories?archived=false&limit=50` → 200 `{items:[], nextCursor:null, total:0}`; after the skeleton the list shows "Nothing here yet — agents will add learnings as they work, and you can add decisions now."

## MEM-002 Add a team memory from the editor
```yaml
priority: P0
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/memories
  - orkka-api:GET /api/projects/{projectId}/memories
  - i18n:memoryPage
```
**Preconditions**: MEM-001.
**Steps**:
1. Click **Add memory** → modal "New memory".
2. Observe **Create** (`memory-save`) disabled; type Title `Use Postgres, not Mongo`; still disabled; type Content `Relational data and the team knows it.`; pick Type "Decision"; leave Scope "Shared with the team"; leave pin unchecked.
3. Click **Create**.
**Expected**:
- Title input has `maxlength=200`, Content `maxlength=4000`; Type defaults to "Learning"; hint "Decisions, conventions and preferences are shown to every agent on every run…" visible.
- `POST /api/projects/{p}/memories {title, content, type:"Decision", agentId:null, isPinned:false, isArchived:false}` → 201 with `source: "User"`, `useCount: 0`.
- Modal closes; a `memory-row` shows badge "Decision", the title, the content and "shared with the team · by a person · just now · used 0 times".
**Notes**: cleanup deletes the memory (MEM-008) or the project.

## MEM-003 Validate memory fields on the API
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/memories
  - orkka-api:PUT /api/projects/{projectId}/memories/{memoryId}
```
**Preconditions**: Owner API key; project with one memory `m`.
**Steps**:
1. `POST …/memories {title:"", content:"x"}`.
2. `POST …/memories {title:"x", content:""}`.
3. `POST …/memories {title:<201 chars>, content:"x"}`.
4. `POST …/memories {title:"x", content:<4001 chars>}`.
5. `POST …/memories {title:"x", content:"y", type:"Rumour"}`.
6. `PUT …/memories/{m} {title:"x", content:"y", agentId:"01ZZZZZZZZZZZZZZZZZZZZZZZZ"}`.
**Expected**:
- 400 "Title is required."; 400 "Content is required."; 400 "Title must be at most 200 characters."; 400 "Content must be at most 4000 characters."; 400 "Unknown memory type."; 400 "AgentId must belong to this project."
**Notes**: the 2,000-active-memories cap returns 409 "A project can hold at most 2000 active memories — archive some first." — not exercised (seeding cost); see `MemoryRules.MaxPerProject`.

## MEM-004 Add a memory private to one agent
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/memories
  - orkka-api:GET /api/projects/{projectId}/memories
  - i18n:memoryPage
```
**Preconditions**: MEM-001; project has agent `Luna`.
**Steps**:
1. **Add memory** → Title `Luna prefers bullet lists`, Content `Keep summaries as bullets.`, Type "Preference", Scope "Private to Luna" → **Create**.
2. Set the Scope filter to "Private to Luna"; then to "Shared with the team".
**Expected**:
- POST body has `agentId` = Luna's id → 201 with `agentName: "Luna"`.
- Row reads "private to Luna · by a person …".
- Filter "Private to Luna" → `GET …/memories?scope={lunaId}&…` lists only that row; "Shared with the team" → `?scope=team` excludes it.

## MEM-005 Edit a memory
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PUT /api/projects/{projectId}/memories/{memoryId}
  - i18n:memoryPage
```
**Preconditions**: MEM-002 memory.
**Steps**:
1. Click **Edit** on the row → modal "Edit memory" prefilled.
2. Change Type to "Convention", append ` (2026)` to the title; click **Save**.
**Expected**:
- `PUT …/memories/{id} {title:"Use Postgres, not Mongo (2026)", content, type:"Convention", agentId:null, isPinned:false, isArchived:false}` → 200.
- Row shows badge "Convention" and the new title; **Cancel** in the modal discards without a request.

## MEM-006 Pin and unpin a memory
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PUT /api/projects/{projectId}/memories/{memoryId}
  - orkka-api:GET /api/projects/{projectId}/memories
  - i18n:memoryPage
```
**Preconditions**: two memories, the older one created first.
**Steps**:
1. Click **Pin** (`memory-pin`) on the older row.
2. `GET …/memories?archived=false&limit=50`.
3. Click **Unpin** on the same row.
**Expected**:
- `PUT …/memories/{id}` with `isPinned:true` → 200; the row gains the "Pinned" badge, a tinted background, the button now reads "Unpin", and the row moves to the top (list orders pinned first, then newest).
- GET → `items[0].isPinned === true`; `?pinned=true` returns only pinned rows.
- Unpin → `isPinned:false`; badge gone.

## MEM-007 Archive a memory, find it under Archived, restore it
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PUT /api/projects/{projectId}/memories/{memoryId}
  - orkka-api:GET /api/projects/{projectId}/memories
  - i18n:memoryPage
```
**Preconditions**: MEM-002 memory.
**Steps**:
1. Click **Archive** on the row.
2. Set View to "Archived".
3. Click **Restore**; set View back to "Active".
**Expected**:
- `PUT …/memories/{id}` with `isArchived:true` → 200; the row leaves the Active list (`GET …?archived=false`).
- Archived view → `GET …?archived=true` lists the row with a **Restore** button.
- Restore → `isArchived:false`; the row is back in Active and gone from Archived.

## MEM-008 Delete a memory after confirming
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:DELETE /api/projects/{projectId}/memories/{memoryId}
  - orkka-api:GET /api/projects/{projectId}/memories
  - i18n:memoryPage
```
**Preconditions**: a disposable memory titled `Temp`.
**Steps**:
1. Click **Delete** on the row → dialog "Delete memory?" with message `"Temp" is removed permanently. Archive it instead if you might want it back.`; click **Cancel**.
2. Repeat; click **Delete**.
3. `DELETE …/memories/{id}` again.
**Expected**:
- Cancel: row stays, no request.
- Delete: `DELETE …/memories/{id}` → 204; row gone; `total` decremented.
- Second delete → 404.

## MEM-009 Search memories by text
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/memories
  - i18n:memoryPage
```
**Preconditions**: memories about `Postgres` and about `Tailwind`.
**Steps**:
1. Type `postgres` in "Search"; wait 300 ms.
2. Clear and type `zzzzunlikely`.
**Expected**:
- One request `GET …/memories?q=postgres&archived=false&limit=50` after the debounce (not one per keystroke); only the Postgres row remains (full-text ranked, `nextCursor: null`).
- No match → "No memories match that search."

## MEM-010 Filter by type and by rules group
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/memories
  - i18n:memoryPage
```
**Preconditions**: one Decision, one Preference, one Learning memory.
**Steps**:
1. Type filter → "Learning".
2. Type filter → "Rules (decisions, conventions, preferences)".
3. `GET …/memories?type=Rumour`.
**Expected**:
- `?type=Learning` → only the Learning row.
- `?type=Decision,Convention,Preference` → the Decision and Preference rows, not the Learning one.
- API unknown type → 400 `Unknown memory type "Rumour".`

## MEM-011 Load more memories with the cursor
```yaml
priority: P3
surface: [ui, api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/memories
  - orkka-api:POST /api/projects/{projectId}/memories
  - i18n:memoryPage
```
**Preconditions**: seed 55 memories via `POST …/memories` (API key).
**Steps**:
1. Open the page.
2. Click **Load more (50 of 55)**.
3. `GET …/memories?limit=500`.
**Expected**:
- First page: 50 rows, `nextCursor` set, button label "Load more (50 of 55)".
- Click → `GET …/memories?…&cursor={nextCursor}` appends 5 rows; button disappears (`nextCursor: null`).
- `limit` is clamped to 200 (`items.length ≤ 200`).

## MEM-012 Preview what an agent would retrieve for a task
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/memories/retrieve
  - i18n:memoryPage
```
**Preconditions**: a pinned Decision about Postgres, an unpinned Learning about OAuth, a Learning private to Luna about Google login.
**Steps**:
1. Click **What would an agent see?** → modal with intro "Paste a task title or description…".
2. Type `Add OAuth login with Google to the web app` in "Task or question"; leave "As agent" = "Any agent (team memory only)"; click **Retrieve** (`memory-preview-run`).
3. Change "As agent" to Luna; click **Retrieve** again.
4. Type `quantum knitting`; **Retrieve**.
**Expected**:
- Step 2: `GET …/memories/retrieve?q=…` → numbered list in `memory-preview-results`: the pinned Decision first (rules first), then the OAuth Learning; Luna's private memory absent.
- Step 3: request includes `agentId={lunaId}`; Luna's Google-login memory now appears.
- Step 4: "Nothing would be retrieved for that — consider pinning the entries that should always apply." (pinned rules still surface if present — assert list content accordingly).

## MEM-013 Retrieve endpoint honours take and leaves use counts untouched
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/memories/retrieve
  - orkka-api:GET /api/projects/{projectId}/memories
```
**Preconditions**: ≥ 3 memories; note `useCount` of each via list.
**Steps**:
1. `GET …/memories/retrieve?q=postgres&take=1`.
2. `GET …/memories/retrieve?q=postgres&take=500`.
3. `GET …/memories` and compare `useCount`.
**Expected**:
- Step 1 returns at most 1 row; step 2 at most 50 (clamped); rows are `MemoryResponse` objects in retrieval order.
- `useCount` values are unchanged after previews (the endpoint undoes its own bump).

## MEM-014 Let an agent save a memory with the remember tool
```yaml
priority: P2
modes: [hybrid-repo]
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/agents/{agentId}/conversations/{conversationId}/messages
  - orkka-api:GET /api/projects/{projectId}/memories
  - orkka-api:src/Modules/Orkka.Modules.Agents/Features/AgentChat/Tools/OrkkaChatToolServer.cs
  - i18n:memoryPage
```
**Preconditions**: fixture-web project; runner online with `chat-tools`.
**Steps**:
1. On `/p/{slug}/team` open Luna's chat; send "Use your remember tool to save this project Decision: 'E2E token <token>: we always use pnpm' with the reason 'workspace protocol'. Reply 'saved' when done."; wait for the reply.
2. Open `/p/{slug}/memory`; search `<token>`.
**Expected**:
- `GET …/memories?q=<token>` returns one row with `source: "Agent"`, `type: "Decision"`, `agentId: null` (scope team).
- Row shows badge "Decision" and "shared with the team · by an agent".
**Notes**: the same tool exists in the task pipeline (`TaskPipelineToolServer`) — dev runs can write Learnings/Mistakes.

## MEM-015 Hide memory writes and return 403 without AgentsConfigure
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/memories
  - orkka-api:PUT /api/projects/{projectId}/memories/{memoryId}
  - orkka-api:DELETE /api/projects/{projectId}/memories/{memoryId}
  - orkka-api:GET /api/projects/{projectId}/memories
  - orkka-api:GET /api/projects/{projectId}/memories/retrieve
```
**Preconditions**: API key minted without `AgentsConfigure`; member session without it; one memory.
**Steps**:
1. With the key: `POST`, `PUT`, `DELETE` (valid bodies); `GET …/memories`; `GET …/memories/retrieve?q=x`.
2. As the member open `/p/{slug}/memory`.
**Expected**:
- Writes → 403; both GETs → 200.
- UI: no `memory-add`, `memory-pin`, **Edit**, **Archive**, **Delete**; the "What would an agent see?" button and filters remain usable.
