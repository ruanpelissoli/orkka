---
area: DOC
title: Documents — project document browser, editor, revisions and live docs
modes-default: [all]
---

Page `/p/{slug}/documents` (`DocumentsBrowser` 837 lines + `DocumentEditor` split-pane markdown + `DocumentDiffView`). Header: **Rename folder** (only when folders exist), **Upload**, **New document**. Left pane `FileTree` built from `path/title.md` (file documents show `.pdf`). Viewer header: title, source badge (Agent/Brief/Manual/Task), `path · vN · time`, **Edit**, **Export** dropdown (Markdown/PDF/Word) or **Download** for file documents, **History**, **Delete**. Revision view: "Viewing version N", **Back to current**, **Diff**, **Restore**. `?d={id}` deep-links a document. `AgentChatDrawer` for the PM on the page. Writes need `DocumentsManage`; reads open. Server rules: title ≤ 200, path ≤ 500, content ≤ 200,000 chars, PDF ≤ 20 MB, duplicate `path+title` → 409.

## DOC-001 Render the Documents page with its landmarks
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/documents
  - orkka-api:GET /api/projects/{projectId}/documents
  - i18n:documents
```
**Preconditions**: Ready project.
**Steps**:
1. Open `/p/{slug}/documents` on a project with no documents.
2. Open it on a project with documents (DOC-004).
**Expected**:
- Sidebar "Documents" highlighted; document title "Documents"; heading "Documents"; buttons **Upload** and **New document**.
- Empty project: tree pane says "No documents yet"; content pane empty state "No documents yet" + "Create one manually or ask an agent to document a decision in chat."; no **Rename folder**.
- Populated project: tree lists `title.md` entries grouped by folder; content pane "Select a document from the tree"; **Rename folder** present when any document has a path.
- Floating "Chat with Luna" (PM) drawer button present.

## DOC-002 Open a document from the tree
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/documents/{id}
  - i18n:documents
```
**Preconditions**: document `decisions/backend` / "E2E Decision" (DOC-004).
**Steps**:
1. Expand `decisions` → `backend`; click `E2E Decision.md`.
2. Open `/p/{slug}/documents?d={id}` directly.
**Expected**:
- `GET …/documents/{id}` → 200; viewer shows the title, badge "Manual", line `decisions/backend · v1 · {relative time}`, rendered markdown body; buttons **Edit**, **Export**, **History**, **Delete**.
- The `?d=` deep link opens the same document on first render.

## DOC-003 Rename a document folder
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/documents/folders/rename
  - orkka-api:GET /api/projects/{projectId}/documents
  - i18n:folders
```
**Preconditions**: documents under `decisions/backend` and one under `archive`; a document titled "E2E Decision" also exists directly under `archive`.
**Steps**:
1. Click **Rename folder**; in the dialog pick `decisions/backend` in the select, type `decisions/api` in "New folder…", click **Rename folder**.
2. `GET /api/projects/{p}/documents`.
3. `POST …/documents/folders/rename {fromPath:"decisions/api", toPath:"archive"}`.
**Expected**:
- `POST …/folders/rename {fromPath:"decisions/backend", toPath:"decisions/api"}` → 200; tree shows `decisions/api`; every moved document has `path: "decisions/api"` and a new revision.
- Colliding rename → 409 `"archive/E2E Decision" already exists — rename or move it first.`
**Notes**: the dialog's button is disabled while the new name is empty or equals the old one.

## DOC-004 Create a markdown document
```yaml
priority: P0
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/documents
  - orkka-api:GET /api/projects/{projectId}/documents
  - i18n:documents
```
**Preconditions**: Owner session; Ready project.
**Steps**:
1. Click **New document**.
2. Observe **Save** is disabled; type title `E2E Decision` in "Document title"; **Save** still disabled; type `# Decision\n\nUse Postgres.` in "Write markdown here..."; type `decisions/backend` in "Folder, e.g. decisions/backend".
3. Click **Save**.
**Expected**:
- `POST /api/projects/{p}/documents {title:"E2E Decision", path:"decisions/backend", content:"# Decision…"}` → 201 with `source: "Manual"`, `currentVersion: 1`.
- Viewer switches to the new document (selected in the tree under `decisions/backend`); live preview pane on the right rendered the heading while typing.
**Notes**: cleanup deletes the document (DOC-015) or the project.

## DOC-005 Reject a duplicate title in the same folder
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/documents
  - i18n:documents
```
**Preconditions**: DOC-004 document exists.
**Steps**:
1. **New document** → same title and folder as DOC-004, any content → **Save**.
2. `POST …/documents` with the same title/path via API; then with `title: ""`.
**Expected**:
- UI: inline error "A document with this title already exists in this folder."; editor stays open.
- API: 409 `{error:"A document with this title already exists in this folder."}`; empty title → 400.

## DOC-006 Edit a document and save a new revision
```yaml
priority: P0
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PATCH /api/projects/{projectId}/documents/{id}
  - orkka-api:GET /api/projects/{projectId}/documents/{id}
  - i18n:documents
```
**Preconditions**: DOC-004.
**Steps**:
1. Open the document; click **Edit**.
2. Click **Preview** to hide the right pane, click again to show it.
3. Append `\n\nAmended.` to the content; click **Save**.
4. `GET …/documents/{id}`.
**Expected**:
- Editor shows title/folder inputs prefilled and the textarea with the current markdown; Preview toggles the split pane.
- `PATCH …/documents/{id} {title, path, content}` → 200 with `currentVersion: 2`.
- Viewer shows `… · v2 · just now` and the amended body; GET agrees.
**Notes**: **Cancel** returns to the viewer without a request.

## DOC-007 List revisions and view an older one
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/documents/{id}/revisions
  - orkka-api:GET /api/projects/{projectId}/documents/{id}/revisions/{version}
  - i18n:documents
```
**Preconditions**: DOC-006 (two revisions).
**Steps**:
1. Click **History**.
2. Click `v1`.
3. Click **Back to current**.
**Expected**:
- `GET …/revisions` → panel "History" lists `v2` ("Current", your name, relative time) and `v1`.
- Clicking `v1` calls `GET …/revisions/1`; header reads "Viewing version 1", body shows the v1 markdown; buttons **Back to current**, **Restore**; **Diff** is absent on v1 (nothing older).
- Back to current restores the v2 view.
- A document with a single revision shows "No history yet" only when the list is empty (new documents have v1).

## DOC-008 Diff a revision against its predecessor
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/documents/{id}/revisions/{version}
  - orkka-ui:apps/local/app/[locale]/(dashboard)/p/[projectId]/documents/DocumentDiffView.tsx
  - i18n:documents
```
**Preconditions**: DOC-006, plus a third revision that only changes the title (PATCH `{title:"E2E Decision v3"}`).
**Steps**:
1. **History** → click `v2` → click **Diff**.
2. Click **Diff** again.
3. Click `v3` → **Diff**.
**Expected**:
- `GET …/revisions/1` is fetched; a unified diff of `document.md` shows the added line `Amended.` in a hunk (PR diff components, read-only).
- Second click hides the diff and shows the plain v2 body.
- v3 vs v2 (content identical) shows "No changes between these versions."

## DOC-009 Restore an earlier revision
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/documents/{id}/revisions/{version}/restore
  - orkka-api:GET /api/projects/{projectId}/documents/{id}
  - i18n:documents
```
**Preconditions**: DOC-006.
**Steps**:
1. **History** → `v1` → **Restore**.
2. `GET …/documents/{id}`; `GET …/revisions`.
**Expected**:
- `POST …/revisions/1/restore` → 200 with `currentVersion: 3` and v1's content; button read "Restoring..." meanwhile.
- Viewer returns to the current view showing v1's body at `v3`; revisions list has `v3` as "Current".
- **Restore** is not shown when the viewed revision equals `currentVersion`.

## DOC-010 Upload text files as markdown documents
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/documents
  - i18n:documents
```
**Preconditions**: fixtures `notes.md`, `readme.txt`, `image.png` (renamed to `.md` is not needed), `huge.md` (> 200,000 chars).
**Steps**:
1. Click **Upload**; pick `notes.md` and `readme.txt` together.
2. Click **Upload**; pick `image.png`.
3. Click **Upload**; pick `huge.md`.
**Expected**:
- Each text file becomes `POST …/documents {title:"notes"|"readme", path:"", content:<file text>}` → 201; tree shows `notes.md` and `readme.md` at root; the last one is opened.
- `image.png` → toast "image.png isn’t a supported file type — upload .md, .markdown, .txt, or .pdf files." and no request.
- `huge.md` → toast "huge.md is too large — documents are capped at 200,000 characters." and no request; API `POST` with 200,001 chars → 400.
- Uploading a text file whose title already exists → toast "{name}: a document with this title already exists in this folder."

## DOC-011 Upload a PDF as a file document
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/documents/upload
  - orkka-api:GET /api/projects/{projectId}/documents/{id}
  - i18n:documents
```
**Preconditions**: fixtures `spec.pdf` (small), `big.pdf` (> 20 MB), `fake.pdf` (text renamed).
**Steps**:
1. Click **Upload**; pick `spec.pdf`.
2. Click **Upload**; pick `big.pdf`.
3. `POST …/documents/upload` (multipart `file`) with `fake.pdf`; then with `notes.md`.
**Expected**:
- `POST …/documents/upload` → 201 with `fileName: "spec.pdf"`, `fileContentType: "application/pdf"`, `fileSizeBytes`, `content: ""`; tree shows `spec.pdf`; viewer renders the PDF viewer ("Loading preview…" then the document) with **Download** instead of **Edit**/**Export**.
- `big.pdf` → toast "big.pdf is too large — PDF documents are capped at 20 MB." and no request.
- API: `fake.pdf` → 400 "That file is not a PDF."; `notes.md` → 400 "Only PDF files can be uploaded as documents."; duplicate title → 409.

## DOC-012 Export a document as Markdown, PDF and Word
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/documents/{id}/pdf
  - orkka-api:GET /api/projects/{projectId}/documents/{id}/docx
  - i18n:documents
```
**Preconditions**: DOC-004 document.
**Steps**:
1. Open the document; click **Export** → "Markdown (.md)".
2. **Export** → "PDF (.pdf)".
3. **Export** → "Word (.docx)".
4. `GET …/documents/{id}/pdf` and `…/docx` via API.
**Expected**:
- Markdown download `E2E Decision.md` is built client-side from `content` (no request).
- PDF: `GET …/pdf` → 200 `application/pdf`, download `E2E Decision.pdf`; Word: `GET …/docx` → 200 `application/vnd.openxmlformats-officedocument.wordprocessingml.document`, download `E2E Decision.docx`.
- Button reads "Downloading..." while in flight; a failed export shows "Failed to download the PDF."

## DOC-013 Download an uploaded file document
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/documents/{id}/file
  - i18n:documents
```
**Preconditions**: DOC-011 `spec.pdf`.
**Steps**:
1. Open `spec.pdf`; click **Download**.
2. `GET …/documents/{id}/file` via API; compare bytes with the fixture.
**Expected**:
- Browser download named `spec.pdf`; API → 200 `application/pdf` with identical bytes (`Content-Length` = `fileSizeBytes`).
- File documents show no **Edit** and no **Export** dropdown.

## DOC-014 List documents produced by a task
```yaml
priority: P2
surface: [api, ui]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/documents/by-task/{taskId}
  - i18n:taskDocumentation
```
**Preconditions**: a task with no documentation pass; (for the populated assertion) the task from DOC-017.
**Steps**:
1. `GET /api/projects/{p}/documents/by-task/{taskId}` for a fresh task.
2. Same call for the DOC-017 task; open `/p/{slug}/tasks/{n}`.
**Expected**:
- Fresh task → 200 `[]`; the task page shows no "Documentation" section.
- DOC-017 task → 200 list of `{documentId, title, path, version, kind:"Created"|"Updated"}`; the task page "Documentation" section lists them with "Created"/"Updated" badges and links to `/documents?d={id}`.

## DOC-015 Delete a document after confirming
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:DELETE /api/projects/{projectId}/documents/{id}
  - orkka-api:GET /api/projects/{projectId}/documents/{id}
  - i18n:documents
```
**Preconditions**: DOC-010 `readme` document.
**Steps**:
1. Open `readme.md`; click **Delete**; dialog titled "readme" says "Delete this document? This cannot be undone."; click **Cancel**.
2. Repeat; click **Delete**.
3. `GET …/documents/{id}`.
**Expected**:
- Cancel: nothing changes.
- Delete: `DELETE …/documents/{id}` → 204; tree drops the entry; content pane returns to "Select a document from the tree"; GET → 404.

## DOC-016 Ask the PM to write a document from the side drawer
```yaml
priority: P1
modes: [hybrid-repo]
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/documents
  - orkka-api:POST /api/projects/{projectId}/agents/{agentId}/conversations
  - orkka-api:POST /api/projects/{projectId}/agents/{agentId}/conversations/{conversationId}/messages
  - orkka-api:GET /api/projects/{projectId}/documents
  - i18n:agentChat
```
**Preconditions**: fixture-web project; runner online with `chat-tools`; PM agent (Luna).
**Steps**:
1. Open `/p/{slug}/documents`; click the floating "Chat with Luna" button.
2. Send "Write a one-paragraph document titled 'E2E Overview' in folder 'e2e' describing this project."; wait for the stream.
3. Click **View in Documents** in the "Document saved" block.
**Expected**:
- Streaming shows "Saving document..." then "Document saved" with the link.
- The link selects the new document (`?d={id}`) — title "E2E Overview", path `e2e`, badge "Agent"; the tree refreshed without reload.
- History for it shows `v1` by the agent with "Asked by {you}" and "View request in chat".

## DOC-017 Keep documents up to date after a task lands (live docs)
```yaml
priority: P2
modes: [hybrid-repo]
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/settings
  - orkka-api:PATCH /api/projects/{id}
  - orkka-api:GET /api/projects/{projectId}/documents/{id}/revisions
  - orkka-api:GET /api/projects/{projectId}/documents/by-task/{taskId}
  - orkka-api:src/Modules/Orkka.Modules.Execution/Orchestration/DocumentationSyncService.cs
  - i18n:documents
```
**Preconditions**: fixture-web project with the "E2E Overview" document (DOC-016); runner online; PM agent present; `autoApprovePlan` and `autoApproveCodeReview` on to keep the run unattended.
**Steps**:
1. In `/p/{slug}/settings` turn on **Live docs** (`settings.documentationSyncEnabled: true`, verify via `GET /api/projects/{id}`).
2. Create and submit the standard task ("add a list item '<token>' to the features list"); wait until it is Done (merged).
3. Poll `GET …/documents/by-task/{taskId}` until non-empty (allow several minutes).
4. Open the affected document → **History**.
**Expected**:
- At least one document is created or revised with `source`/badge "Task"; the revision row shows "From task: {task title}" linking to `/tasks/{n}` and the PM as author.
- The task page shows the "Documentation" section (DOC-014).
- With the setting off the pass is skipped (`by-task` stays `[]`).
**Notes**: runs the PM turn on the runner ($0).

## DOC-018 Hide document writes and return 403 without DocumentsManage
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/documents
  - orkka-api:PATCH /api/projects/{projectId}/documents/{id}
  - orkka-api:DELETE /api/projects/{projectId}/documents/{id}
  - orkka-api:POST /api/projects/{projectId}/documents/upload
  - orkka-api:POST /api/projects/{projectId}/documents/folders/rename
  - orkka-api:POST /api/projects/{projectId}/documents/{id}/revisions/{version}/restore
  - orkka-api:GET /api/projects/{projectId}/documents
```
**Preconditions**: API key minted without `DocumentsManage`; a member session without it; DOC-006 document.
**Steps**:
1. Call each write in refs with the key (valid bodies); `GET …/documents`.
2. As the member open `/p/{slug}/documents`, open the document, open **History**, click `v1`.
**Expected**:
- All writes → 403; GET → 200.
- UI: no **New document**, **Upload**, **Rename folder**, **Edit**, **Delete**, or **Restore**; **Export**, **History**, **Back to current**, **Diff** still work.

## DOC-019 Return 404 for documents of another project
```yaml
priority: P3
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/documents/{id}
  - orkka-api:PATCH /api/projects/{projectId}/documents/{id}
  - orkka-api:GET /api/projects/{projectId}/documents/{id}/revisions/{version}
```
**Preconditions**: projects A (document `x`) and B.
**Steps**:
1. `GET /api/projects/{B}/documents/{x}`; `PATCH /api/projects/{B}/documents/{x} {title:"y"}`.
2. `GET /api/projects/{A}/documents/{x}/revisions/99`.
**Expected**:
- All → 404; the document in A is unchanged.
