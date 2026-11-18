---
area: ARC
title: Architect — architecture board and Excalidraw diagram editor
modes-default: [all]
---

Page `/p/{slug}/architect` ("Architecture Board"). `DiagramGallery` (folder pane + `DiagramCard` grid, `MoveToFolderDialog`) → clicking a card sets `?d={diagramId}` and swaps in the full-page `DiagramEditor` (Back, editable title, **History**, **Save**, Excalidraw canvas, `DiagramHistoryPanel` with side-by-side read-only preview). `AgentChatDrawer` for the Solution Architect (Archie) sits on the page. Diagrams are created only by agents (`create_diagram` chat tool); humans edit, move, restore and delete. Writes need `AgentsChat`; reads are open. Nav key `architect` is hidden in the simplified experience.

## ARC-001 Render the Architecture Board with its landmarks
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/architect
  - orkka-api:GET /api/projects/{projectId}/diagrams
  - i18n:architect
  - i18n:folders
```
**Preconditions**: Ready project (technical details on if Managed).
**Steps**:
1. Open `/p/{slug}/architect`.
**Expected**:
- Sidebar item highlighted (`data-tour="nav-architect"`); document title "Architecture Board"; heading "Architecture Board".
- Folder pane titled "Folders" with "All items" and a **New folder** control.
- With no diagrams: empty state "No architecture diagrams yet" + "Chat with Archie on the Team page to generate architecture diagrams for your project." + **Go to Team** (→ `/p/{slug}/team`).
- The floating "Chat with Archie" drawer button is present bottom-right.

## ARC-002 Show diagram cards with type, version and freshness
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/architect
  - orkka-api:GET /api/projects/{projectId}/diagrams
  - i18n:architect
```
**Preconditions**: project with ≥ 1 diagram (ARC-011 creates one; the suite keeps that project).
**Steps**:
1. `GET /api/projects/{p}/diagrams`.
2. Open `/p/{slug}/architect`.
**Expected**:
- One card per diagram: title, type badge (e.g. "System Architecture"), `v{n}` badge when `version > 1`, description (clamped), footer "Updated {time}" (e.g. "Updated 3m ago"), **Move** text button and a trash icon (`aria-label` "Delete").
- Card order and count match the API response; folder counts in the pane match `folderPath` groups.

## ARC-003 Open a diagram in the full-page editor
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/architect
  - orkka-api:GET /api/projects/{projectId}/diagrams/{diagramId}
  - i18n:architect
```
**Preconditions**: ARC-002.
**Steps**:
1. Click a diagram card.
2. Click the back arrow (`aria-label` "Back").
**Expected**:
- URL gains `?d={diagramId}` via pushState (no full navigation); gallery is replaced by the editor: back button, title input prefilled with the diagram title, **History**, **Save**, and an Excalidraw canvas (`.excalidraw` container) rendering the agent's elements on a dark theme.
- Back removes `?d=` and shows the gallery again.
- Loading `/p/{slug}/architect?d={id}` directly opens the editor (deep link used by chat).

## ARC-004 Edit the title and canvas, then save a new version
```yaml
priority: P0
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PUT /api/projects/{projectId}/diagrams/{diagramId}
  - orkka-api:GET /api/projects/{projectId}/diagrams/{diagramId}
  - orkka-api:GET /api/projects/{projectId}/diagrams/{diagramId}/versions
  - i18n:architect
```
**Preconditions**: diagram at `version: 1`.
**Steps**:
1. Open the editor; change the title input to `E2E renamed`; draw a rectangle on the canvas.
2. Click **Save**.
3. `GET /api/projects/{p}/diagrams/{id}`; `GET …/diagrams/{id}/versions`.
**Expected**:
- `PUT …/diagrams/{id} {title:"E2E renamed", diagramData:{type:"excalidraw", version:2, source:"orkka-archie", elements:[…], appState:{…}}}` → 200.
- Button shows "Saving..." then "Saved" for ~2 s, then "Save".
- GET → `title: "E2E renamed"`, `version: 2`, `diagramData.elements` includes the new rectangle; versions list has `v1` and `v2`, `v2` with `changedByUserName` = you.

## ARC-005 Save with no changes without creating a version
```yaml
priority: P3
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PUT /api/projects/{projectId}/diagrams/{diagramId}
  - i18n:architect
```
**Preconditions**: ARC-004.
**Steps**:
1. Open the editor; click **Save** without touching anything.
2. `PUT …/diagrams/{id} {title: <current title>}` via API.
**Expected**:
- UI: "Saved" flashes and no network request is made.
- API: 200 and `version` unchanged (the endpoint short-circuits identical title/data/description).

## ARC-006 Browse versions in the history panel with side-by-side preview
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/diagrams/{diagramId}/versions
  - orkka-api:GET /api/projects/{projectId}/diagrams/{diagramId}/versions/{version}
  - i18n:architect
```
**Preconditions**: diagram with ≥ 2 versions (ARC-004).
**Steps**:
1. Open the editor; click **History**.
2. Click the `v1` row; then click it again.
**Expected**:
- Panel "History" lists versions newest first: `v{n}`, "Current" on the latest, author name or "Agent"/"User", relative time, "Asked by {name}" and "View request in chat" on agent-authored versions.
- Selecting `v1` calls `GET …/versions/1` and splits the canvas: left labelled "Current · v2" (editable), right labelled "v1 · {title}" read-only (view mode); **Restore this version** appears under the selected row.
- Clicking the selected row again closes the preview.
- With no versions the panel says "No versions yet".

## ARC-007 Restore an earlier version of a diagram
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/diagrams/{diagramId}/versions/{version}/restore
  - orkka-api:GET /api/projects/{projectId}/diagrams/{diagramId}
  - i18n:architect
```
**Preconditions**: ARC-006.
**Steps**:
1. Select `v1` in the history panel; click **Restore this version**.
2. `GET /api/projects/{p}/diagrams/{id}`.
**Expected**:
- `POST …/diagrams/{id}/versions/1/restore` → 200 with `version: 3` and `v1`'s title/data; button reads "Restoring..." meanwhile.
- Canvas reloads with the restored content, title input shows the restored title, history now lists `v3` as "Current".
- GET → `version: 3`, `diagramData` equal to `v1`'s.

## ARC-008 Move a diagram to a folder
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PATCH /api/projects/{projectId}/diagrams/{diagramId}/folder
  - orkka-api:GET /api/projects/{projectId}/diagrams/{diagramId}
  - i18n:folders
```
**Preconditions**: diagram at root.
**Steps**:
1. Click **Move** on the card; in "Move to folder" type `infra` in "New folder…"; click **Move**.
2. `GET /api/projects/{p}/diagrams/{id}`.
3. Click "infra" in the folder pane.
**Expected**:
- `PATCH …/diagrams/{id}/folder {folderPath:"infra"}` → 200.
- GET → `folderPath: "infra"`; the pane shows "infra (1)" and selecting it shows only that card.

## ARC-009 Rename a diagram folder
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/diagrams/folders/rename
  - orkka-api:GET /api/projects/{projectId}/diagrams
  - i18n:folders
```
**Preconditions**: ARC-008.
**Steps**:
1. Hover "infra"; click **Rename folder**; type `platform`; press Enter.
2. `GET /api/projects/{p}/diagrams`.
3. `POST …/diagrams/folders/rename {fromPath:"", toPath:"x"}`; then `{fromPath:"platform", toPath:"platform/inner"}`.
**Expected**:
- `POST …/folders/rename {fromPath:"infra", toPath:"platform"}` → 200; diagram `folderPath: "platform"`.
- 400 "The folder to rename must not be the root."; 400 "A folder cannot be moved into itself."
**Notes**: the architect folder pane has no delete-folder action (folders vanish when empty).

## ARC-010 Delete a diagram after confirming
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:DELETE /api/projects/{projectId}/diagrams/{diagramId}
  - orkka-api:GET /api/projects/{projectId}/diagrams/{diagramId}
  - i18n:architect
```
**Preconditions**: a disposable diagram.
**Steps**:
1. Click the trash icon on the card; dialog titled with the diagram title asks "Are you sure you want to delete this diagram?"; click **Cancel**.
2. Repeat; click **Delete**.
3. `GET /api/projects/{p}/diagrams/{id}`.
**Expected**:
- Cancel: card stays, no request, and the click does not open the editor.
- Delete: `DELETE …/diagrams/{id}` → 204; card disappears; GET → 404.

## ARC-011 Ask the Solution Architect for a diagram from the side drawer
```yaml
priority: P1
modes: [hybrid-repo]
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/architect
  - orkka-api:POST /api/projects/{projectId}/agents/{agentId}/conversations
  - orkka-api:POST /api/projects/{projectId}/agents/{agentId}/conversations/{conversationId}/messages
  - orkka-api:GET /api/projects/{projectId}/diagrams
  - i18n:agentChat
  - i18n:architect
```
**Preconditions**: fixture-web project linked; runner online with `chat-tools`; Solution Architect agent present.
**Steps**:
1. Open `/p/{slug}/architect`; click the floating "Chat with Archie" button.
2. Type "Draw a simple system architecture diagram of this project titled 'E2E Arch'" and press **Send**; wait for the stream to finish.
3. Click **View on the Architecture Board** in the result block.
4. `GET /api/projects/{p}/diagrams?conversationId={c}`.
**Expected**:
- While streaming a "Drawing diagram..." block appears; on completion the block reads "Diagram ready" with the link.
- The link opens `/p/{slug}/architect?d={diagramId}` — the new diagram in the editor, title "E2E Arch".
- API lists exactly the new diagram with `conversationId` = the drawer's conversation, `version: 1`, `agentId` = Archie.
**Notes**: runs on the user's runner ($0).

## ARC-013 Refuse diagram writes without AgentsChat
```yaml
priority: P2
surface: [api, ui]
automation: planned
refs:
  - orkka-api:PUT /api/projects/{projectId}/diagrams/{diagramId}
  - orkka-api:DELETE /api/projects/{projectId}/diagrams/{diagramId}
  - orkka-api:PATCH /api/projects/{projectId}/diagrams/{diagramId}/folder
  - orkka-api:POST /api/projects/{projectId}/diagrams/folders/rename
  - orkka-api:POST /api/projects/{projectId}/diagrams/{diagramId}/versions/{version}/restore
  - orkka-api:GET /api/projects/{projectId}/diagrams/{diagramId}/versions
```
**Preconditions**: API key minted without `AgentsChat`; diagram with ≥ 2 versions.
**Steps**:
1. Call each write in refs with the key (valid bodies).
2. `GET …/diagrams/{id}/versions` with the key.
3. As a member without AgentsChat open the editor, select an old version in History.
**Expected**:
- All writes → 403; GET → 200.
- UI: **Restore this version** is hidden; the "Chat with Archie" drawer button is absent (chat requires AgentsChat).

## ARC-014 Return 404 for unknown diagrams and versions
```yaml
priority: P3
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/diagrams/{diagramId}
  - orkka-api:GET /api/projects/{projectId}/diagrams/{diagramId}/versions/{version}
```
**Preconditions**: projects A (with diagram `x`, 2 versions) and B.
**Steps**:
1. `GET /api/projects/{B}/diagrams/{x}`.
2. `GET /api/projects/{A}/diagrams/{x}/versions/99`.
**Expected**:
- Both → 404.
