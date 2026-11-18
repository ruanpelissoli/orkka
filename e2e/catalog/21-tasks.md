---
area: TSK
title: Tasks — list, create form, detail, actions, sidebar cards
modes-default: [all]
---

Staging note: see `20-board.md` for how tasks are staged in a lane without an agent claiming them (PATCH lane + immediate `POST /block`). "Restricted key" = an Owner-minted API key whose permission set omits the named permission.

## TSK-001 Render the tasks list page with header, filter bar and list
```yaml
priority: P0
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks
  - orkka-api:GET /api/projects/{projectId}/tasks
  - i18n:tasksPage
  - i18n:taskList
```
**Preconditions**: project with two tasks (one Drafting, one staged in Developing).
**Steps**:
1. Open `/p/{slug}/tasks`.
2. Click "New Task".
**Expected**:
- Heading "Tasks"; view switch shows "List" as `aria-current`; sidebar Tasks highlighted.
- Filter bar: search input "Search tasks...", select "Filter by repository" ("All repos"), select "Filter by status" ("All statuses"), checkbox "Archived".
- List columns Title / Priority / Lane / Status / Created with both tasks; empty project shows "No tasks found".
- "New Task" navigates to `/p/{slug}/tasks/new?from=tasks`.
**Notes**: the list is server-rendered from `GET …/tasks?pageSize=50`.

## TSK-002 Filter the list by search text and reset the page parameter
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks
  - orkka-ui:packages/ui/src/components/task/TaskFilterBar.tsx
  - orkka-api:GET /api/projects/{projectId}/tasks
  - i18n:taskFilterBar
```
**Preconditions**: tasks "alpha", "beta".
**Steps**:
1. Open `/p/{slug}/tasks?page=2`.
2. Type `alp` in "Search tasks..." and wait 300 ms.
3. Clear the input.
**Expected**:
- URL becomes `/p/{slug}/tasks?search=alp` (the `page` param is dropped); only "alpha" is listed.
- Clearing removes `search` from the URL and both tasks return.
- Typing the same value twice does not push a second navigation.
**Notes**: the API receives `search=alp`.

## TSK-003 Filter the list by repository, status and the Archived checkbox
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks
  - orkka-api:GET /api/projects/{projectId}/tasks
  - orkka-api:POST /api/tasks/{id}/archive
  - i18n:taskFilterBar
```
**Preconditions**: two repos linked; task A on repo 1 (Drafting), task B on repo 2 staged in Developing then archived.
**Steps**:
1. Choose repo 1 in "Filter by repository".
2. Reset to "All repos"; choose "Draft" in "Filter by status".
3. Reset; tick "Archived".
**Expected**:
- Step 1 → URL `?repoId={repo1}`, only A.
- Step 2 → `?status=Draft`, only A.
- Step 3 → `?isArchived=true`, only B.
**Notes**: status options are the full TaskStatus set rendered through `labels.taskStatus`.

## TSK-004 Paginate the list with Previous/Next
```yaml
priority: P3
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks
  - orkka-api:GET /api/projects/{projectId}/tasks
  - i18n:tasksPage
```
**Preconditions**: 51 tasks created via API.
**Steps**:
1. Open `/p/{slug}/tasks`; click "Next"; click "Previous".
**Expected**:
- Page 1 shows 50 rows, footer "Page 1" with "Next" only.
- Next → `?page=2` with 1 row, "Page 2" and "Previous" only.
- Previous returns to page 1.
**Notes**: `hasMore` is inferred from a full page of 50.

## TSK-005 Render the create form with every field
```yaml
priority: P0
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/new
  - orkka-ui:packages/ui/src/components/task/TaskCreateForm.tsx
  - orkka-api:GET /api/models
  - i18n:taskCreateForm
```
**Preconditions**: Unmanaged project with two repos, one milestone, seeded agents; model catalog reachable.
**Steps**:
1. Open `/p/{slug}/tasks/new`.
**Expected**:
- Heading "Create Task", subtitle "Describe what you need done. An AI agent will pick it up."
- Fields in order: Title, Description, Repository, Milestone, Assign to agent, Model, "Skip the planning phase", Task Type (Feature/BugFix/Refactor/Improvement), Priority (low/medium/high/critical), Tags, Depends on, Context documents, Attachments.
- Buttons "Create & Submit" (type=submit) and "Save as Draft".
- At ≥ 1280 px the repo file picker panel is shown on the right; below it a "Browse repository files" toggle appears once a repo is chosen.
**Notes**: the milestone select is absent when the project has no milestones; the agent select is absent when no dev agents exist.

## TSK-006 Require a title and repository before creating
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/new
  - orkka-api:POST /api/projects/{projectId}/tasks
  - i18n:taskCreateForm
```
**Preconditions**: form open.
**Steps**:
1. Click "Create & Submit" with everything empty.
2. Fill Title only; click again.
3. `POST /api/projects/{p}/tasks {repoId, title:""}` and `{title:"x"}` (no repoId).
**Expected**:
- Browser validation blocks on the required Title, then on the required Repository select ("Select a repository"); no request is sent.
- API → 400 "Title is required." and 400 "RepoId is required.".
**Notes**: Title max 500, Description max 10,000 (400 "Title must be 500 characters or fewer." / "Description must be 10,000 characters or fewer.").

## TSK-007 Write a description with @-mentions and link repo files as context
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/new
  - orkka-api:GET /api/projects/{projectId}/repos/{repoId}/files/search
  - orkka-api:POST /api/projects/{projectId}/tasks
  - i18n:repoFilePanel
  - i18n:mentionTextarea
```
**Preconditions**: fixture-web repo selected.
**Steps**:
1. Type a description (counter shows `n/5000`).
2. In the repo file panel search `package.json`, click "Add to task context".
3. On a narrow viewport click "Browse repository files" / "Hide file browser".
4. Save as Draft; `GET /api/tasks/{id}`.
**Expected**:
- Linked file shows "Linked" and the hidden `contextFiles` input carries `["package.json"]`.
- The toggle shows/hides the panel below 1280 px.
- Created task has `contextFiles:["package.json"]` and the description text.
**Notes**: "Remove from task context" unlinks.

## TSK-009 Span a task across repositories and read the amber notice
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/new
  - orkka-api:POST /api/projects/{projectId}/tasks
  - orkka-api:GET /api/projects/{projectId}/task-groups/{id}
  - i18n:taskCreateForm
```
**Preconditions**: two repos linked.
**Steps**:
1. Choose repo 1 → the block "Also changes these repositories" appears listing repo 2 only.
2. Tick repo 2.
3. Change the primary repo to repo 2.
4. Re-tick, Save as Draft, then `GET /api/tasks/{id}` and `GET /api/projects/{p}/task-groups/{groupId}`.
**Expected**:
- Ticking shows "This will become 2 tasks, one per repository, merged together."
- Switching the primary to repo 2 removes it from the checkbox list and clears the notice.
- The created task has `groupId`; the group lists the request with the second repo.
**Notes**: hidden entirely for single-repo projects.

## TSK-010 Pick a milestone or leave the task in the backlog
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/new
  - orkka-api:POST /api/projects/{projectId}/tasks
  - i18n:taskMilestone
```
**Preconditions**: milestone M1 exists.
**Steps**:
1. Open the "Milestone" select; default is "Backlog (no milestone)".
2. Choose M1; Save as Draft.
**Expected**:
- Task created with `milestoneId` = M1; the board card shows the milestone label.
- `POST …/tasks {milestoneId:"<other project's milestone>"}` → 400 "The specified milestone does not belong to this project."
**Notes**: without milestones the select is not rendered.

## TSK-011 Assign a dev agent and have its model pre-selected
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/new
  - orkka-api:POST /api/projects/{projectId}/tasks
  - i18n:taskCreateForm
```
**Preconditions**: seeded agents.
**Steps**:
1. Open "Assign to agent" — default "Auto-assign (recommended)"; options read `Name — Role` (Nova — Full Stack Dev, Pixel — Frontend Dev, Atlas — Backend Dev, …).
2. Choose "Pixel — Frontend Dev".
3. Save as Draft.
**Expected**:
- The Model select value becomes Pixel's configured model; hidden `model` stays empty (no override).
- Created task has `assignedAgentId` = Pixel; `model:null`.
- Only dev roles are offered (no PM, QA, Reviewer, DBA, Legal).
**Notes**: hint text "Pin this task to a specific dev agent instead of automatic routing."

## TSK-012 Choose a per-task model from grouped, annotated options
```yaml
priority: P1
modes: [hybrid-repo, hybrid-folder]
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/new
  - orkka-api:GET /api/models
  - orkka-api:GET /api/me/runner
  - orkka-api:POST /api/projects/{projectId}/tasks
  - i18n:modelBilling
```
**Preconditions**: runner online, Claude ready, Codex not signed in.
**Steps**:
1. Open the "Model" select.
2. Pick a Claude model different from the agent default; then pick a Codex model.
3. Save as Draft with the Claude override.
**Expected**:
- First option "Use the agent's default model"; then one `<optgroup>` per provider (Anthropic, OpenAI, …).
- Claude rows read "{model} · your runner, 0 credits"; rows of a provider not signed in on the runner read "{model} — provider not connected" and are `disabled` (there is no "{model} · cloud, credits" option any more).
- Hidden `model` input equals the picked id; created task has `model` set.
- Hint: "Runs this task on a different model than the agent's usual one…".
**Notes**: `POST …/tasks {model:"not-a-model"}` → 400 "'not-a-model' is not a selectable model."

## TSK-013 Tick "Skip the planning phase"
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/new
  - orkka-api:POST /api/projects/{projectId}/tasks
  - i18n:taskCreateForm
```
**Preconditions**: form open.
**Steps**:
1. Tick the checkbox labelled "Skip the planning phase"; Save as Draft.
2. Repeat without ticking.
**Expected**:
- First task `skipPlanning:true`; second `skipPlanning:false`.
- Hint reads "The PM still reviews and refines the description, then the task goes straight to the dev agent — no plan to write or approve."
**Notes**: runtime effect is PLN-014.

## TSK-014 Choose the task type with the radio grid
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/new
  - orkka-api:POST /api/projects/{projectId}/tasks
```
**Preconditions**: form open.
**Steps**:
1. Read the "Task Type" grid — "Feature" is pre-checked.
2. Click "Bug Fix"; Save as Draft.
3. `POST …/tasks {type:"Epic"}`.
**Expected**:
- Task `type:"BugFix"`; card icon changes accordingly.
- API → 400 "Type must be Feature, BugFix, Refactor, or Improvement."
**Notes**: labels come from `labels.taskType`.

## TSK-015 Choose the priority with the radio grid
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/new
  - orkka-api:POST /api/projects/{projectId}/tasks
```
**Preconditions**: form open.
**Steps**:
1. Read the "Priority" grid — "Medium" pre-checked, four options with coloured dots.
2. Click "Critical"; Save as Draft.
3. `POST …/tasks {priority:"Urgent"}`.
**Expected**:
- Task `priority:"Critical"`.
- API → 400 "Priority must be Low, Medium, High, or Critical."
**Notes**: —

## TSK-016 Enter comma-separated tags
```yaml
priority: P3
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/new
  - orkka-api:POST /api/projects/{projectId}/tasks
  - i18n:taskCreateForm
```
**Preconditions**: form open.
**Steps**:
1. Type ` bug, auth ,urgent,` into "Tags" (placeholder "bug, auth, urgent (comma-separated)"); Save as Draft.
**Expected**:
- Task `tags:["bug","auth","urgent"]` (trimmed, empties dropped).
**Notes**: hint "Optional. Helps organize and filter tasks."

## TSK-017 Add and remove dependencies with the "Depends on" picker
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/new
  - orkka-api:GET /api/projects/{projectId}/tasks
  - orkka-api:POST /api/tasks/{id}/dependencies
  - i18n:taskCreateForm
```
**Preconditions**: existing tasks D1 (Drafting) and D2 (Done + merged), and an archived Completed-unmerged task D3.
**Steps**:
1. Click "Add dependency" — panel shows "Loading tasks..." then the list.
2. Type `zzz` in "Search tasks..." → "No matching tasks"; clear; click D1.
3. Click the chip's "Remove dependency" ×; add D1 again; click "Done".
4. Save as Draft; `GET /api/tasks/{id}`.
**Expected**:
- The list offers D1 and D3 but not D2 (done+merged) nor the task itself.
- Chip shows `E2E-n` code and title; Remove clears it.
- Created task's `dependsOn` contains D1 (added before any submit); hint "Optional. The task waits at the front of the board until these are done and merged."
**Notes**: a failed dependency add on Create & Submit keeps the task as a draft and toasts "1 dependency failed to add…".

## TSK-018 Attach context documents from the picker
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/new
  - orkka-api:GET /api/projects/{projectId}/documents
  - orkka-api:POST /api/projects/{projectId}/tasks
  - i18n:taskDocuments
```
**Preconditions**: two documents in different folders (`POST /api/projects/{p}/documents`).
**Steps**:
1. Under "Context documents" click "Attach" → "Loading..." then groups by folder path.
2. Filter with "Filter documents..." (no match → "No documents match your filter"); clear; tick one document; click "Done".
3. Click the row's "Remove"; re-add; Save as Draft.
**Expected**:
- Selected document row shows title + monospace path; hidden `contextDocumentIds` carries its id.
- Created task has `contextDocumentIds:[id]`; hint "Attached documents are included in the agent's context when it works on this task."
- Empty project shows "No documents in this project yet".
**Notes**: only the newest version per folder+title is offered.

## TSK-019 Attach files by drag-drop and by the file dialog, then remove one
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/new
  - orkka-api:POST /api/tasks/{taskId}/attachments/upload-url
  - orkka-api:POST /api/tasks/{taskId}/attachments
  - orkka-api:GET /api/tasks/{taskId}/attachments
  - i18n:taskCreateForm
```
**Preconditions**: two small files (`a.png` 3 KB, `b.txt` 1 KB).
**Steps**:
1. Drop `a.png` onto "Drop files here or click to browse".
2. Click the dropzone and pick `b.txt` via the hidden `<input type=file multiple>`.
3. Click the × on `b.txt`; re-add it.
4. Save as Draft; `GET /api/tasks/{id}/attachments`.
**Expected**:
- Previews list each file with a thumbnail (image) or file icon, name and size "3 KB"; removing drops the row.
- After create, each file goes through `upload-url` → blob PUT → `POST …/attachments`; GET lists both with `fileName`, `fileSize`, `fileType`.
- A failing upload toasts "1 attachment failed to upload — try again from the task page." and still redirects.
**Notes**: picking the same file twice works (input value reset).

## TSK-020 Create and submit a task
```yaml
priority: P0
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/new
  - orkka-api:POST /api/projects/{projectId}/tasks
  - orkka-api:POST /api/tasks/{id}/submit
  - i18n:taskCreateForm
```
**Preconditions**: form open; repo chosen.
**Steps**:
1. Title "e2e submit", description, click "Create & Submit".
2. `GET /api/tasks/{id}`.
**Expected**:
- Button shows loading, both buttons disabled while pending; toast "Task created and submitted — your team will pick it up."; browser returns to the board.
- API sequence: `POST …/tasks` 201 (lane Drafting, status Draft) then `POST /api/tasks/{id}/submit` → lane `Ready`, status `InQueue`.
**Notes**: block the task immediately in fixtures that must not run; a submit failure toasts the server message verbatim and still redirects (the draft exists).

## TSK-021 Save a task as a draft
```yaml
priority: P0
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/new
  - orkka-api:POST /api/projects/{projectId}/tasks
  - i18n:taskCreateForm
```
**Preconditions**: form open.
**Steps**:
1. Title "e2e draft", repo, click "Save as Draft".
**Expected**:
- Toast "Task created — saved as a draft on the board."; redirect to the origin page.
- `GET /api/tasks/{id}` → `currentLane:"Drafting"`, `status:"Draft"`, `code:"E2E-{n}"`, no submit call made.
**Notes**: —

## TSK-023 Show the runner note and the time forecast under the model picker
```yaml
priority: P2
modes: [hybrid-repo]
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/new
  - orkka-api:POST /api/projects/{projectId}/tasks/forecast
  - i18n:modelBilling
  - i18n:forecast
```
**Preconditions**: Claude ready; new project without history.
**Steps**:
1. Open the form; wait 250 ms.
2. Stop the runner; reload the form.
**Expected**:
- Step 1: note "Runs on your runner via your Anthropic subscription · 0 credits"; forecast "Not enough history to estimate yet".
- Step 2: note "Runs on your runner via your Anthropic subscription · 0 credits — your runner is offline right now, so the task waits until it reconnects." with a "Runner settings" link; never a "Billed cloud run" note.
- `POST …/tasks/forecast {model?, assignedAgentId?}` → 200 (may be an empty forecast) with cost fields `null` (forecasts are time-only) and no `executionTarget*` fields.

## TSK-024 Validate task creation over the API
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/tasks
```
**Preconditions**: project P with repo R; another project Q with repo S and agent X.
**Steps**:
1. `POST /api/projects/{P}/tasks {repoId:S, title:"x"}`.
2. `POST … {repoId:R, title:"x", assignedAgentId:X}`.
3. `POST … {repoId:R, title:"x", spansRepoIds:[S]}`.
4. `POST … {repoId:R, title:"x", description:"<10001 chars>"}`.
5. `POST /api/projects/{P}/tasks` with a valid body.
**Expected**:
- 1 → 400 "The specified repository does not belong to this project."
- 2 → 400 (agent validation message).
- 3 → 400 "A repository this task spans does not belong to this project."
- 4 → 400 "Description must be 10,000 characters or fewer."
- 5 → 201 with `number`, `code:"E2E-n"`, `currentLane:"Drafting"`, `status:"Draft"`, `runnerUserId` = caller (no `executionTarget`); a `TaskCreated` event is broadcast.
**Notes**: —

## TSK-025 Render the task detail page with details grid and sidebar cards
```yaml
priority: P0
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-api:GET /api/projects/{projectId}/tasks/number/{number}
  - orkka-api:GET /api/tasks/{taskId}/messages
  - orkka-api:GET /api/tasks/{taskId}/plans/latest
  - orkka-api:GET /api/tasks/{taskId}/attachments
  - orkka-api:GET /api/tasks/{taskId}/executions
  - i18n:taskDetailLive
```
**Preconditions**: task T1 in Drafting with description and a tag.
**Steps**:
1. Open `/p/{slug}/tasks/{number}`.
**Expected**:
- Breadcrumb "Board / E2E-n / {title}"; action row with "Submit to Queue" and "Delete".
- Editable header with title, badges, description; "Conversation" card with "No messages yet. Start a conversation." and a composer.
- Sidebar "Details": Created, Started ("—"), Repository (repo name); cards "Dependencies" ("No dependencies"), "Context documents", "Attachments (0)" with "Upload", "Execution History (0)".
- Requests: task by number, messages, plans/latest, attachments, executions, milestones, repos, agents.
**Notes**: Managed projects hide the Repository row.

## TSK-026 Show the queue position for a queued task
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-api:GET /api/tasks/{id}
  - i18n:taskDetailLive
```
**Preconditions**: three tasks submitted to Ready while no runner pickup is possible (runner stopped) so they remain `InQueue`.
**Steps**:
1. Open the third task's page.
**Expected**:
- Details show "Queue Position" `#3`; the board card reads "Queue #3".
- `GET /api/tasks/{id}` returns `queuePosition:3`.
**Notes**: restart the runner afterwards (tasks then run at $0) or block them.

## TSK-027 Redirect legacy ULID task URLs to the number and show not-found
```yaml
priority: P2
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-ui:/p/[projectId]/tasks/[taskId]/review
  - orkka-ui:/p/[projectId]/tasks/[taskId]/merge
  - i18n:reviewPage
```
**Preconditions**: task T1 with ULID `U` and number `n`.
**Steps**:
1. Open `/p/{slug}/tasks/{U}`; then `/p/{slug}/tasks/{U}/review`; then `/p/{slug}/tasks/{U}/merge`.
2. Open `/p/{slug}/tasks/999999`.
**Expected**:
- Each redirects (307) to `/p/{slug}/tasks/{n}`, `…/{n}/review`, `…/{n}/merge`.
- Unknown number renders "Task not found" with the id and a "Back to Board" link.
**Notes**: —

## TSK-028 Edit the title inline
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-ui:packages/ui/src/components/task/TaskDetailEditable.tsx
  - orkka-api:PATCH /api/tasks/{id}
  - i18n:taskDetailEditable
```
**Preconditions**: task T1; owner session.
**Steps**:
1. Click the title, type " v2", press Enter.
2. Click again, type junk, press Escape.
**Expected**:
- Enter (or blur) issues `PATCH /api/tasks/{id} {title:"… v2"}` → 200; title re-renders.
- Escape discards the edit; no request.
- A viewer without TasksEdit sees plain text (not editable).
**Notes**: —

## TSK-029 Edit the description with Save and Cancel
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-api:PATCH /api/tasks/{id}
  - i18n:taskDetailEditable
  - i18n:mentionTextarea
```
**Preconditions**: task T1 without description.
**Steps**:
1. Click "Click to add a description..."; type markdown; click "Save".
2. Click the description; edit; click "Cancel".
**Expected**:
- Save issues `PATCH {description}` → 200 and the markdown renders.
- Cancel restores the saved text without a request.
**Notes**: the textarea supports @-mentions like the create form — typing `@` opens the file picker ("Type to search files...", "Searching...", "No matching files"; "Select a repository first" when the task has no repo).

## TSK-030 Change priority, type, repo, agent and milestone from the header chips
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-api:PATCH /api/tasks/{id}
  - i18n:taskDetailEditable
```
**Preconditions**: Drafting task T1; two repos, one milestone, dev agents.
**Steps**:
1. Use the chip selects "Priority", "Type", "Repo", "Agent" ("Auto-assign"), "Milestone" ("Backlog (no milestone)").
2. Stage a second task in Developing and try to change its Repo.
**Expected**:
- Each change issues one `PATCH /api/tasks/{id}` with the single field (`priority`, `type`, `repoId`, `assignedAgentId` ("" clears), `milestoneId` ("" = backlog)) → 200. There is no "Runs on" chip (removed with cloud execution).
- Started task: repo change → 400 "This task's work is already merged…" or "An agent is working on this task…" as applicable.
**Notes**: PATCH requires TasksEdit.

## TSK-031 Submit a draft from the task page
```yaml
priority: P0
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-ui:packages/ui/src/components/task/TaskActions.tsx
  - orkka-api:POST /api/tasks/{id}/submit
  - i18n:taskActions
```
**Preconditions**: Drafting task T1 with repo; Drafting task T2 created without a repo via PATCH `repoId:""` (or planner-created).
**Steps**:
1. On T1 click "Submit to Queue".
2. On T2 click "Submit to Queue".
3. `POST /api/tasks/{T1}/submit` again.
**Expected**:
- T1 → lane Ready, status InQueue; the button disappears (not Drafting any more).
- T2 → toast "A repository must be assigned before submitting."; stays Drafting.
- Step 3 → 400 "Only tasks in Drafting can be submitted."
**Notes**: a provider blocker (no AI provider connected) is also surfaced verbatim as a toast.

## TSK-034 Re-queue a blocked task and block a running-lane task
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-api:POST /api/tasks/{id}/retry
  - orkka-api:POST /api/tasks/{id}/block
  - orkka-api:POST /api/tasks/{id}/unblock
  - i18n:taskActions
```
**Preconditions**: task T1 staged in Developing (blocked); task T2 staged in CodeReview with status WaitingCodeReview (via `PATCH lane PlanReady`… use `POST /block` then `POST /unblock` in CodeReview to reach the resting status).
**Steps**:
1. On T1 click "Re-queue"; then click "Block".
2. On T2 confirm "Re-queue" is offered.
3. `POST /api/tasks/{T1}/unblock`.
**Expected**:
- Re-queue → `POST …/retry` → status `InQueue` in the same lane; the button becomes "Block".
- Block → `POST …/block` → status `Blocked`, badge "Blocked", chip count on the board.
- T2 (WaitingCodeReview) shows "Re-queue" rather than "Block".
- Unblock → 200, status back to the lane's resting status.
**Notes**: Re-queue needs TasksExecute, Block needs TasksEdit; neither appears in Drafting or Done. Re-block T1 after step 1 if it must not run.

## TSK-035 Reset a task with the two-step confirm
```yaml
priority: P1
surface: [ui, api, hub]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-api:POST /api/tasks/{id}/reset
  - hub:TaskHub.LaneChanged
  - i18n:taskActions
```
**Preconditions**: task T1 staged in Developing; task T2 in Drafting; task T3 archived.
**Steps**:
1. On T1 click "Reset" → buttons "Wipe & Reset" and "Cancel" appear; click "Cancel"; click "Reset" then "Wipe & Reset".
2. `POST /api/tasks/{T2}/reset`; `POST /api/tasks/{T3}/reset`.
**Expected**:
- Cancel restores the single "Reset" button.
- Confirm → banner "Resetting task" ("Resetting..." button) then a `LaneChanged` lands the task in Drafting with messages/plans wiped.
- T2 → 400 "Task is already in Drafting."; T3 → 400 "Unarchive the task first."; a merged task → 400 "This task's branch was already merged — reset would not undo the merged code…".
**Notes**: Reset is hidden for Drafting, merged and archived tasks.

## TSK-036 Archive and unarchive a task
```yaml
priority: P1
surface: [ui, api, hub]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-api:POST /api/tasks/{id}/archive
  - orkka-api:POST /api/tasks/{id}/unarchive
  - hub:TaskHub.TaskUpdated
  - i18n:taskActions
```
**Preconditions**: task T1 staged in Developing; task T2 in Drafting.
**Steps**:
1. On T1 click "Archive"; reload; click "Unarchive".
2. Open T2.
**Expected**:
- Archive → 200, `isArchived:true`; the board hides the card unless "Include archived" is on; the page now offers "Unarchive".
- Unarchive → `isArchived:false`.
- T2 (Drafting) shows no Archive button (Delete instead).
**Notes**: TasksEdit.

## TSK-037 Delete a draft with the two-step confirm; refuse elsewhere
```yaml
priority: P1
surface: [ui, api, hub]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-api:DELETE /api/tasks/{id}
  - hub:TaskHub.TaskDeleted
  - i18n:taskActions
```
**Preconditions**: Drafting task T1; task T2 staged in Ready.
**Steps**:
1. On T1 click "Delete" → "Confirm Delete" / "Cancel"; click "Cancel"; repeat and click "Confirm Delete".
2. `DELETE /api/tasks/{T2}`.
3. `GET /api/tasks/{T1}`.
**Expected**:
- Confirm → 204, redirect away, the board card vanishes via `TaskDeleted`.
- T2 → 400 "Only tasks in Drafting can be deleted. Use archive instead."
- Step 3 → 404.
**Notes**: TasksDelete; the button is absent outside Drafting.

## TSK-038 Upload and delete attachments from the gallery
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-ui:packages/ui/src/components/task/AttachmentGallery.tsx
  - orkka-api:POST /api/tasks/{taskId}/attachments/upload-url
  - orkka-api:POST /api/tasks/{taskId}/attachments
  - orkka-api:DELETE /api/attachments/{id}
  - i18n:taskMisc
```
**Preconditions**: task T1.
**Steps**:
1. In "Attachments (0)" click "Upload" and pick `a.png`.
2. Hover the row and click its delete ×.
3. `POST /api/tasks/{id}/attachments/upload-url {fileName, contentType, fileSize}`.
**Expected**:
- Sequence upload-url → blob PUT → `POST …/attachments` → the card reads "Attachments (1)" with thumbnail, name and "3.0 KB".
- Delete → `DELETE /api/attachments/{id}` 204 and the row disappears; "No attachments yet".
- Step 3 → 200 `{uploadUrl, blobName}`.
**Notes**: Upload/delete need TasksEdit.

## TSK-039 Drop files anywhere on the task page to attach them
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-api:POST /api/tasks/{taskId}/attachments
  - i18n:taskDetailLive
```
**Preconditions**: task T1; owner session.
**Steps**:
1. Drag a file over the page; drop it on the conversation card.
**Expected**:
- Overlay "Drop files to attach" / "They'll be added to this task's attachments" appears while dragging; "Uploading attachments..." pill during upload; gallery count increments.
- A member without TasksEdit gets no overlay.
**Notes**: dragging non-file data does not trigger the overlay.

## TSK-040 Add and remove dependencies from the sidebar card
```yaml
priority: P1
surface: [ui, api, hub]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-ui:packages/ui/src/components/task/TaskDependenciesCard.tsx
  - orkka-api:POST /api/tasks/{id}/dependencies
  - orkka-api:DELETE /api/tasks/{id}/dependencies/{dependsOnTaskId}
  - hub:TaskHub.TaskDependenciesChanged
  - i18n:taskDependencies
```
**Preconditions**: tasks T1 (Drafting) and B1 (Ready, blocked); B1 not merged.
**Steps**:
1. On T1's "Dependencies" card click "Add", filter, click B1; click "Done".
2. Open B1 — read "Blocks".
3. Hover T1's row and click "Remove dependency".
**Expected**:
- Add → `POST /api/tasks/{T1}/dependencies {dependsOnTaskId:B1}` 200; "Waiting on" lists `E2E-n B1 title` with a status dot; amber banner "This task can't move forward yet…done and merged."
- B1 shows T1 under "Blocks" (read-only).
- Remove → `DELETE …/dependencies/{B1}` 204; card returns to "No dependencies".
**Notes**: a Completed-but-unmerged blocker shows the "Awaiting merge" pill; AI-detected edges carry an "AI" badge.

## TSK-041 Reject invalid dependency edges over the API
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/tasks/{id}/dependencies
```
**Preconditions**: tasks A, B (Drafting), D (Done+merged), X archived; A already depends on B.
**Steps**:
1. `POST /api/tasks/{A}/dependencies {dependsOnTaskId:A}`.
2. `… {dependsOnTaskId:D}`; `… {dependsOnTaskId:X}`.
3. `… {dependsOnTaskId:B}` again.
4. `POST /api/tasks/{B}/dependencies {dependsOnTaskId:A}`.
5. `… {dependsOnTaskId:B, reason:"<1001 chars>"}`.
**Expected**:
- 1 → 400 "A task cannot depend on itself."
- 2 → 400 "That task is already done and merged — there is nothing to wait on." / "Cannot depend on an archived task."
- 3 → 400 "This dependency already exists."
- 4 → 400 "This would create a circular dependency." (UI shows "Could not add — this would create a circular dependency.")
- 5 → 400 "Reason must be 1000 characters or fewer."
**Notes**: TasksEdit.

## TSK-042 Gate forward lane moves on unmet dependencies
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:PATCH /api/tasks/{id}/lane
  - orkka-api:POST /api/tasks/{id}/dependencies
```
**Preconditions**: A staged in Ready (blocked) depending on B (Drafting).
**Steps**:
1. `PATCH /api/tasks/{A}/lane {targetLane:"Developing"}`.
2. Stage B to Done via lane moves; repeat step 1.
**Expected**:
- 1 → 400 "This task can't move forward yet. Waiting on: E2E-n (not done). Dependencies must be done and merged first."
- 2 → 200 once B is `Completed` and `isMerged` (a PR-less task moved to Done is marked merged).
**Notes**: the board card shows "Waiting on E2E-n" while blocked by a dependency.

## TSK-043 Enforce the lane-move rules over the API
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:PATCH /api/tasks/{id}/lane
```
**Preconditions**: task T1 in Drafting; task T2 staged in Developing.
**Steps**:
1. `PATCH /api/tasks/{T1}/lane {targetLane:"Sideways"}`; `{targetLane:"Drafting"}`; `{targetLane:"Developing"}`; `{targetLane:"Ready"}`.
2. `PATCH /api/tasks/{T2}/lane {targetLane:"Developing"}`; `{targetLane:"PlanReady"}`; `{targetLane:"Done"}`; `{targetLane:"CodeReview"}`.
3. `PATCH /api/tasks/{T1}/lane` with a key lacking TasksExecute; on a foreign task id.
**Expected**:
- 1 → 400 "'Sideways' is not a valid pipeline lane."; 400 "Task is already in this lane."; 400 "Tasks in Drafting can only be submitted to Ready."; 200 `status:"InQueue"`.
- 2 → 400 "Task is already in this lane."; 200 `status:"WaitingPlanApproval"`; 200 `status:"Completed"`, `isArchived:true`, `isMerged:true` (no PR); 200 back to CodeReview `status:"InQueue"`, `isArchived:false`.
- 3 → 403; 404.
**Notes**: Developing/Testing/CodeReview/Ready set `InQueue`; Planning/PendingQuestions set `Active`.

## TSK-044 Open the code-changes modal for a task with snapshots
```yaml
priority: P2
modes: [hybrid-repo]
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-api:GET /api/tasks/{taskId}/code-changes
  - orkka-api:GET /api/tasks/{taskId}/code-changes/{snapshotId}
  - i18n:codeChanges
```
**Preconditions**: task that completed a Developing run on the runner (snapshot recorded: paths and counts, no diff).
**Steps**:
1. Click "View code changes".
2. Pick a snapshot; `GET …/code-changes` and `…/code-changes/{id}`.
**Expected**:
- Modal "Code changes" lists sessions "{lane} · {when}" with "N files changed"; selecting one renders the diff read live from the runner, labelled "Read live from the developer's machine: this is the branch as it is now, not a stored copy of that session."
- API: list of snapshots (`fileCount`, `additions`, `deletions`, no diff); `…/code-changes/{id}` → `{diff, currentBranch:true, unavailable:null}`; a fresh task returns an empty list and the button is not rendered.
**Notes**: "This diff is too large to show in full." appears for truncated diffs; runner offline → HYB-047.

## TSK-045 Read the execution history card
```yaml
priority: P2
modes: [hybrid-repo]
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-api:GET /api/tasks/{taskId}/executions
  - orkka-api:GET /api/projects/{projectId}/executions
  - i18n:executionHistory
```
**Preconditions**: task with at least one completed run.
**Steps**:
1. Open the task page; expand a run row.
2. `GET /api/tasks/{id}/executions`; `GET /api/projects/{p}/executions`.
**Expected**:
- "Execution History (N)" rows with status badge (Completed/Failed/Running/…), agent, "Started: {time}", billed tokens breakdown (Input/Output/Cache read/Cache write) and "Chat with {agent} on the Team page".
- API lists executions with `status`, `agentId`, token counters.
**Notes**: $0 on the runner.

## TSK-046 Approve a manually verified task from the verification banner
```yaml
priority: P1
modes: [hybrid-repo]
surface: [ui, api, hub]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-api:POST /api/tasks/{taskId}/verification
  - orkka-api:PATCH /api/projects/{id}
  - hub:TaskHub.LaneChanged
  - i18n:taskDetailLive
```
**Preconditions**: project settings `manualVerificationEnabled:true`, `qaEnabled:false`, `autoApprovePlan:true`; standard fixture task run on the runner until status `WaitingUserVerification`.
**Steps**:
1. Open the task page; read the banner; click the copy icon on the checkout command.
2. Click "Approve".
3. `POST /api/tasks/{id}/verification {decision:"approve"}` again.
**Expected**:
- Banner "Ready for your verification" with "Check it out locally to run and test it:" and `git fetch … && git checkout {branch}` copyable.
- Approve → `POST …/verification {decision:"approve"}` 204; task moves to CodeReview `InQueue` (or Done when `skipCodeReview`), a system message "Verified by the requester — handing off to code review." appears.
- Step 3 → 400 "Task is not waiting for verification."
**Notes**: `request_changes` needs `feedback` (400 "Feedback is required when requesting changes.") and re-queues Developing.

## TSK-047 Apply or dismiss the model recommendation banner
```yaml
priority: P2
surface: [ui, api, hub]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-ui:packages/ui/src/components/task/ModelRecommendationBanner.tsx
  - orkka-api:POST /api/tasks/{id}/model-recommendation
  - hub:TaskHub.TaskUpdated
  - i18n:modelRecommendation
```
**Preconditions**: task with `recommendedModel:"claude-haiku-4-5"` (set by a PM review, or via DB for the harness).
**Steps**:
1. Read the banner; click "Use Claude Haiku 4.5".
2. On a second such task click "Keep current model".
3. `POST /api/tasks/{id}/model-recommendation {action:"apply"}` on a task without a recommendation; `{action:"maybe"}`.
**Expected**:
- Banner "This looks like a lighter task — Claude Haiku 4.5 could handle it for less" with the hint about the agent keeping its model.
- Apply → 200, task `model:"claude-haiku-4-5"`, `recommendedModel:null`, a system message "Model suggestion applied…"; banner gone.
- Dismiss → 200, `recommendedModel:null`, model unchanged.
- Step 3 → 400 "This task has no pending model recommendation."; 400 "Action must be 'apply' or 'dismiss'."
**Notes**: TasksEdit.

## TSK-049 Show the waiting-for-answer, waiting-for-runner and resetting banners
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-api:PATCH /api/tasks/{id}/lane
  - i18n:taskDetailLive
```
**Preconditions**: task A staged in PendingQuestions (`PATCH lane PendingQuestions` → Active; then set WaitingAnswer via a real question in PLN-011 or accept the lane banner); task B with status WaitingForRunner (TSK-032 setup); task C mid-reset.
**Steps**:
1. Open each task page.
**Expected**:
- A (lane PendingQuestions): banner "Agent is waiting for your answer" / "Reply in the conversation below to resume the agent's work."
- B: banner "Waiting for your runner".
- C: banner "Resetting task" with spinner until `LaneChanged` to Drafting.
**Notes**: the PendingQuestions banner keys on the lane, not the status.

## TSK-050 Read and edit a cross-repo task group from the sidebar panel
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-ui:packages/ui/src/components/task/TaskGroupPanel.tsx
  - orkka-api:GET /api/projects/{projectId}/task-groups/{id}
  - orkka-api:PATCH /api/projects/{projectId}/task-groups/{id}
  - orkka-api:POST /api/projects/{projectId}/task-groups/{id}/repo-requests
  - i18n:taskGroup
```
**Preconditions**: task created with `spansRepoIds` (TSK-009) so a group exists.
**Steps**:
1. Open the task; read the "Cross-repo task" card (members, status pill, "Agreed contract").
2. Click "Edit", type a contract, click "Save"; click "Edit" then "Cancel".
3. `POST …/task-groups/{id}/repo-requests {repoName, reason}` then approve it in the card with "Add it".
4. `PATCH …/task-groups/{id} {contract:"x"}` on a Merged/Cancelled group.
**Expected**:
- Save → `PATCH …/task-groups/{id} {contract}` 200; the contract renders in monospace.
- The pending request row "An agent asked to add {repo} to this task." offers "Add it" / "Decline"; approving adds a member.
- Step 4 → 400 "This cross-repo task has already settled, so its contract can't be changed."
**Notes**: ordinary tasks render no panel.

## TSK-051 Query task forecasts
```yaml
priority: P3
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/tasks/forecast
  - orkka-api:GET /api/tasks/{taskId}/forecast
```
**Preconditions**: task T1.
**Steps**:
1. `POST /api/projects/{p}/tasks/forecast {}`.
2. `GET /api/tasks/{T1}/forecast?surface=TaskCreate`; `…?surface=PlanApproval`.
**Expected**:
- 200 each; a project without history returns an empty/`null` estimate that the UI renders as "Not enough history to estimate yet"; with history the agent-time range is filled while `costMedian`, `costLow`, `costHigh` are always `null` (every task runs on the developer's own subscription) and there are no `executionTarget*` fields.
**Notes**: reads are open to any member.

## TSK-052 Return 403 for task writes without the matching permission
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/tasks
  - orkka-api:PATCH /api/tasks/{id}
  - orkka-api:DELETE /api/tasks/{id}
  - orkka-api:POST /api/tasks/{id}/submit
  - orkka-api:POST /api/tasks/{id}/archive
  - orkka-api:POST /api/tasks/{id}/dependencies
  - orkka-api:POST /api/tasks/{taskId}/attachments/upload-url
  - orkka-api:POST /api/projects/{projectId}/interactive-sessions
```
**Preconditions**: four restricted keys, each missing exactly one of TasksCreate / TasksEdit / TasksDelete / TasksExecute; Drafting task T1.
**Steps**:
1. No TasksCreate: `POST /api/projects/{p}/tasks`; `POST …/interactive-sessions`.
2. No TasksEdit: `PATCH /api/tasks/{T1}`; `POST …/archive`; `POST …/dependencies`; `POST …/attachments/upload-url`.
3. No TasksDelete: `DELETE /api/tasks/{T1}`.
4. No TasksExecute: `POST …/submit`; `PATCH …/lane`; `POST …/reset`; `POST …/messages`; `POST …/interactive-sessions`.
5. Each key: `GET /api/tasks/{T1}`.
**Expected**:
- Every write → 403; every read → 200.
**Notes**: interactive sessions need both TasksCreate and TasksExecute.

## TSK-053 List waiting items in the inbox
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/inbox
  - orkka-api:GET /api/inbox
  - i18n:inbox
```
**Preconditions**: task A staged in PlanReady (WaitingPlanApproval); task B staged in Developing and blocked; another project with no waiting tasks.
**Steps**:
1. Open `/inbox`.
2. Click "Open task" on A.
3. `GET /api/inbox`.
**Expected**:
- Heading "Inbox", subtitle "Everything waiting on you, most urgent first"; rows for A ("Approve plan") and B ("Blocked") with project key/code and "waiting {time}".
- "Open task" navigates to A's page.
- API returns items with `kind` ∈ `plan_approval`, `blocked`, plus `taskId`, `projectId`, `code`, `lane`, `status`; completed/archived tasks are excluded.
- Empty state: "Nothing needs you right now".
**Notes**: scope toggles "Everyone" / "Mine" filter by creator.

## TSK-054 Create notifications when a task starts waiting on a human
```yaml
priority: P1
modes: [hybrid-repo]
surface: [api, hub, ui]
automation: planned
refs:
  - orkka-api:GET /api/notifications
  - orkka-api:POST /api/notifications/{id}/read
  - hub:TaskHub.NotificationCreated
  - orkka-ui:/notifications
  - i18n:notifications
```
**Preconditions**: `autoApprovePlan:false`; fixture task submitted and run on the runner until `WaitingPlanApproval`.
**Steps**:
1. Watch the bell while the task parks; `GET /api/notifications`.
2. Open `/notifications`; click the item; `POST /api/notifications/{id}/read`.
**Expected**:
- A `NotificationCreated` event increments the bell; the list contains type `PlanApprovalRequired` ("Plan ready for your review") linking to the task.
- Read → 200 and the unread badge decrements; "Mark all as read" clears the rest.
- Other waiting states produce `QuestionAsked`, `CodeReviewRequired`, `VerificationRequired`, `RunnerOffline`, `TaskMergeFailed`.
**Notes**: $0 on the runner.

## TSK-056 Follow the Agent Activity feed while a run streams
```yaml
priority: P1
modes: [hybrid-repo]
surface: [ui, hub]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-ui:packages/ui/src/components/task/AgentStream.tsx
  - hub:TaskHub.StreamEvent
  - i18n:agentStream
since: 2026-09-15
```
**Preconditions**: task submitted with "Skip the planning phase" on the runner, or a live interactive session (SES-003); task page open before the run starts.
**Steps**:
1. Read the "Agent Activity" card before the first chunk arrives.
2. Watch it while the agent works; hover a tool chunk.
3. Wait for the lane change.
**Expected**:
- Heading "Agent Activity"; placeholder "Waiting for agent output..." with a pulsing indicator until the first `StreamEvent`.
- Chunks render in order with kind labels "Thinking", "Tool", "Code", "Progress" and "Error" (red); the list auto-scrolls to the newest chunk; the card never refetches the task on chunk arrival.
- On `LaneChanged` the stream resets to empty for the new phase (the previous phase's chunks are in the timeline/executions, not here).
**Notes**: the same stream carries `ApprovalRequested`/`ApprovalSettled` for interactive sessions (SES-005).

## TSK-057 Update the live working-tree diff via WorkspaceChanged
```yaml
priority: P2
modes: [hybrid-repo]
surface: [ui, api, hub]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-ui:/p/[projectId]/board
  - orkka-api:GET /api/tasks/{taskId}/code-changes
  - orkka-api:GET /api/tasks/{taskId}/code-changes/{snapshotId}
  - hub:TaskHub.WorkspaceChanged
since: 2026-09-15
```
**Preconditions**: runner advertises `working-tree-diff`; a Developing run (or interactive turn, SES-003) editing the fixture.
**Steps**:
1. Open the board's Code view (BRD-036) and select the task while the agent edits files.
2. `GET /api/tasks/{id}/code-changes` during the run and again after the lane change.
**Expected**:
- Each `WorkspaceChanged {files, stats, revision, snapshotId}` (debounced ≥ 1.5 s, ≤ 1 per 4 s) updates the file list at once; the diff is then fetched by `snapshotId`; an older `revision` arriving late is dropped.
- During the run the list has one row `isLive:true` whose `revision` rises; `GET …/code-changes/{snapshotId}` reads the working tree live from the runner against the merge-base (committed + staged + unstaged + untracked, `.gitignore` honoured) — `isLive:true`, `currentBranch:false`.
- After the run the live row is gone and a permanent snapshot for the phase is listed (TSK-044).
**Notes**: a runner without the capability disables the capture for that run — the Code view then shows only tool-call previews.

## TSK-059 Spend checkpoint endpoints refuse a task that is not at a checkpoint
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/tasks/{id}/spend/continue
  - orkka-api:POST /api/tasks/{id}/spend/stop
since: 2026-10-26
```
**Preconditions**: A Drafting task.
**Steps**:
1. `POST /api/tasks/{id}/spend/continue`; `POST /api/tasks/{id}/spend/stop`.
**Expected**:
- Both → 400 "This task is not waiting at a spend checkpoint." (runner runs cost no credits, so a checkpoint is never reached in this product).
