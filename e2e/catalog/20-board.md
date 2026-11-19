---
area: BRD
title: Board — kanban, filters, autopilot, live updates
modes-default: [all]
---

Staging note used by every case below: "stage a task in lane L" means `POST /api/projects/{p}/tasks` (Drafting/Draft), then `PATCH /api/tasks/{id}/lane {targetLane:"Ready"}` and further `PATCH …/lane` hops as needed, calling `POST /api/tasks/{id}/block` right after each hop so no agent claims the task (the queue only picks `InQueue`). Unblock only when a case needs an agent.

## BRD-001 Render the board with all nine lanes and the header controls
```yaml
priority: P0
surface: [ui, api]
automation: automated
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-api:GET /api/projects/{projectId}/board
  - orkka-ui:packages/ui/src/components/board/TaskBoard.tsx
  - i18n:boardPage
```
**Preconditions**: project `e2e-board-<stamp>` (Unmanaged) with the fixture-web repo linked; no tasks.
**Steps**:
1. Open `/p/{slug}/board`.
2. `GET /api/projects/{p}/board`.
**Expected**:
- Heading "Board"; sidebar item Board is highlighted.
- Lane columns in order: Drafting, Ready, Planning, Plan Ready, Pending Questions, Developing, Testing, Code Review, Done, each with a `0` count.
- Header shows the Board view switch, the "Autopilot" button, "Start session" and "New Task".
- API returns `{lanes:[…]}` with the same nine lane keys and empty `tasks` arrays.
**Notes**: the page is `position:absolute` inside the shell; lanes scroll horizontally, the page body does not.

## BRD-002 Switch between Code, Board and List views with aria-current
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-ui:/p/[projectId]/tasks
  - orkka-ui:packages/ui/src/components/board/BoardViewSwitch.tsx
  - i18n:boardPage
```
**Preconditions**: project with at least one task.
**Steps**:
1. On `/p/{slug}/board` locate the `role="group"` named "Board view".
2. Click "Code"; then click "Board"; then click "List".
**Expected**:
- Initially the "Board" item is a `<span aria-current="page">`; "Code" and "List" are links.
- "Code" navigates to `/p/{slug}/board?view=code`, heading reads "Code", the count "N tasks" appears in the header and "Code" carries `aria-current`.
- "Board" returns to `/p/{slug}/board`.
- "List" navigates to `/p/{slug}/tasks` where "List" carries `aria-current`.
**Notes**: with `?task=` present, Code ⇄ Board links keep the `task` param.

## BRD-003 Open a task card in the modal via ?task= shallow URL
```yaml
priority: P0
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-ui:packages/ui/src/components/board/TaskDetailModal.tsx
  - orkka-api:GET /api/tasks/{id}
  - i18n:taskDetailModal
```
**Preconditions**: task T1 in Drafting.
**Steps**:
1. Open the board and click card T1.
2. Read `location.search`.
3. Reload the page with `?task={number}`.
4. Press the × in the modal header.
**Expected**:
- Modal titled "Task Detail" opens showing T1's title, status and lane badges; the URL becomes `/p/{slug}/board?task={number}` without a page navigation (board state kept).
- Reload with the param opens the same modal directly.
- Close removes `task` from the URL and the modal disappears.
**Notes**: the modal fetches task, messages, plan, attachments, executions, repos, milestones and agents independently; only the task gates the render.

## BRD-004 Flip through ongoing tasks with the modal carousel and open the full page
```yaml
priority: P2
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-ui:packages/ui/src/components/board/TaskDetailModal.tsx
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - i18n:taskDetailModal
```
**Preconditions**: two tasks staged in Ready (status `InQueue`) and one in Drafting.
**Steps**:
1. Open the Drafting task's modal.
2. Click the "Next ongoing task" arrow; click "Previous ongoing task"; press ArrowRight with focus outside inputs.
3. Click "Open full page".
**Expected**:
- Counter reads "–/2" on the Drafting task; Next opens the first InQueue task and the counter reads "1/2"; Previous wraps to "2/2"; ArrowRight advances again.
- Arrows are disabled (`disabled`) when only one ongoing task exists and it is the one shown.
- "Open full page" navigates to `/p/{slug}/tasks/{number}`.
**Notes**: only `InQueue` and `Active` tasks are in the carousel.

## BRD-005 Show the modal error state and recover with Retry
```yaml
priority: P3
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-ui:packages/ui/src/components/board/TaskDetailModal.tsx
  - i18n:taskDetailModal
```
**Preconditions**: board open.
**Steps**:
1. Navigate to `/p/{slug}/board?task=999999` (a number that does not exist).
2. Observe the modal; click "Retry".
**Expected**:
- Modal shows "Failed to load task" with the description "The task could not be loaded. It may have been deleted or you may not have access." and a "Retry" button.
- Retry re-issues the fetch and shows the same error.
**Notes**: the selected id resolves to `null` when no lane task matches, so the modal stays closed for unknown numbers unless a search result matches; automation may stub `GET /api/tasks/{id}` to 500 instead.

## BRD-006 Render the board metrics strip and its empty state
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-api:GET /api/projects/{projectId}/board-metrics
  - orkka-ui:packages/ui/src/components/board/BoardMetricsStrip.tsx
  - i18n:boardMetrics
```
**Preconditions**: fresh project with no runs.
**Steps**:
1. Open the board.
2. `GET /api/projects/{p}/board-metrics?window=7d`.
**Expected**:
- The strip shows "No agent runs yet in this window." (no four dashes).
- API 200 with `window:"7d"`, `current.runs:0`, `current.shipped:0`, `deltas` present.
- On a project with completed runs the strip shows "Shipped", "Agent spend" (or "Tokens" when spend is 0), "Agent time / task" and "Failure rate" cells.
**Notes**: `window` accepts `24h|7d|30d`; invalid values fall back per `StatsWindow.Parse`.

## BRD-007 Change the stats window selector and refetch metrics
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-api:GET /api/projects/{projectId}/board-metrics
  - orkka-ui:packages/ui/src/components/team/StatsWindowSelector.tsx
  - i18n:teamStats
```
**Preconditions**: board open.
**Steps**:
1. In the segmented control labelled "Time window" click "24h", then "30d".
**Expected**:
- Each click issues `GET …/board-metrics?window=24h` then `?window=30d`; the strip re-renders for the new window.
- The selected segment is visually active; the other two remain clickable.
**Notes**: the same control is used on the Team page.

## BRD-008 Search tasks by title from the board search input
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-api:GET /api/projects/{projectId}/board/search
  - i18n:boardSearch
```
**Preconditions**: tasks "alpha widget" (Drafting) and "beta gadget" (Ready, blocked).
**Steps**:
1. Type `alpha` into the input with placeholder "Search tasks...".
2. Wait 300 ms.
**Expected**:
- Lanes are replaced by a card grid containing only "alpha widget"; the filter-chip row is hidden while searching.
- Request `GET /api/projects/{p}/board/search?q=alpha&includeMessages=false&includeArchived=false` returns one summary.
- Typing `zzz` shows "No tasks match your search".
- Clearing the input restores the lanes.
**Notes**: search results are capped at 100 by the API; cards in grid mode are not draggable.

## BRD-009 Toggle "Also search chat messages" to widen the search
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-api:GET /api/projects/{projectId}/board/search
  - orkka-api:POST /api/tasks/{taskId}/messages
  - i18n:boardSearch
```
**Preconditions**: task T1 whose title does not contain "pineapple" but has a message containing "pineapple" (`POST /api/tasks/{id}/messages {content:"pineapple note"}`).
**Steps**:
1. Search `pineapple` → observe no results.
2. Tick the "Also search chat messages" checkbox.
**Expected**:
- Before: "No tasks match your search".
- After: request carries `includeMessages=true` and T1 appears in the grid.
**Notes**: the checkbox re-runs the current query immediately.

## BRD-010 Toggle "Include archived" to show archived tasks in lanes and search
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-api:GET /api/projects/{projectId}/board
  - orkka-api:GET /api/projects/{projectId}/board/search
  - orkka-api:POST /api/tasks/{id}/archive
  - i18n:boardSearch
```
**Preconditions**: task T1 staged in Developing then archived via `POST /api/tasks/{id}/archive`.
**Steps**:
1. Open the board — confirm T1 is absent.
2. Tick "Include archived".
3. Search T1's title with the box still ticked.
**Expected**:
- After ticking, the lanes refetch with `includeArchived=true` and T1 appears in Developing with its archived styling.
- Search request carries `includeArchived=true` and returns T1.
**Notes**: one checkbox drives both the lanes and the search.

## BRD-011 Filter the board by milestone with the "All work" select
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-api:POST /api/projects/{projectId}/milestones
  - orkka-api:POST /api/projects/{projectId}/tasks
  - i18n:boardPage
```
**Preconditions**: milestone M1 created via `POST /api/projects/{p}/milestones {title:"M1", outcome}`; task A created with `milestoneId:M1`, task B without.
**Steps**:
1. Open the board; locate the select with `aria-label="Milestone"`.
2. Choose "M1"; then choose "All work".
**Expected**:
- Default option is "All work" and both A and B are visible.
- Selecting M1 hides B and keeps A; chip counts and "Showing N of M" recount against the milestone slice.
- "All work" restores both.
- With zero milestones the select is not rendered at all.
**Notes**: cards show the milestone title as a label.

## BRD-012 Filter the board with the attention chips and read "Showing N of M"
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-ui:packages/ui/src/components/board/board-filters.ts
  - i18n:boardFilters
```
**Preconditions**: tasks staged: T1 PlanReady (`WaitingPlanApproval`), T2 Developing blocked (`POST /block`), T3 Drafting.
**Steps**:
1. Read the chip row: "Needs you", "Blocked", "Merge failed", "Stalled", "Agent working" with counts.
2. Click "Needs you"; then also click "Blocked"; then click both again to clear.
**Expected**:
- Counts: Needs you 1, Blocked 1, Merge failed 0, Stalled 0, Agent working 0; trailing text "Showing 3 of 3".
- "Needs you" active → only T1 remains in its lane; text "Showing 1 of 3".
- Adding "Blocked" is additive (OR): T1 and T2 shown, "Showing 2 of 3".
- Clearing both restores all lanes.
**Notes**: needsYou = WaitingAnswer|WaitingPlanApproval|WaitingCodeReview|WaitingQaReview|WaitingUserVerification; stalled = OutOfTokens|WaitingForRunner|ReasoningTimeout; agentWorking = lane Planning or Developing.

## BRD-013 Count the "Merge failed" and "Stalled" chips from task state
```yaml
priority: P2
modes: [hybrid-repo]
surface: [ui, hub]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-ui:packages/ui/src/components/board/board-filters.ts
  - hub:TaskHub.TaskMergeFailed
  - i18n:boardFilters
```
**Preconditions**: task T1 with `mergeFailureReason` set (a real failed merge, see PR-014) and task T2 with status `WaitingForRunner` (created while the runner is stopped).
**Steps**:
1. Open the board.
2. Click "Merge failed"; clear; click "Stalled".
**Expected**:
- "Merge failed" count 1 and shows T1 only; T1's card carries the "Merge failed" badge.
- "Stalled" count 1 and shows T2 only; T2's card reads "Waiting for your runner".
**Notes**: both chips share the warning colour.

## BRD-014 Drag a task backward to an earlier lane (PATCH lane)
```yaml
priority: P0
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-api:PATCH /api/tasks/{id}/lane
  - orkka-ui:packages/ui/src/components/board/TaskBoard.tsx
```
**Preconditions**: owner session; task T1 staged in Developing (blocked).
**Steps**:
1. Pointer-drag T1's card ≥ 8 px and drop it onto the "Ready" column.
2. `GET /api/tasks/{id}`.
**Expected**:
- While dragging, only earlier lanes are highlighted as drop targets; later lanes render at 40% opacity.
- On drop the card moves to Ready immediately and `PATCH /api/tasks/{id}/lane {targetLane:"Ready"}` returns 200 with `currentLane:"Ready"`, `status:"InQueue"`.
- GET confirms the lane.
**Notes**: unblock is implied — Ready sets `InQueue`; re-block immediately if the agent must not pick it up.

## BRD-015 Snap a forward drag back without calling the API
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-ui:packages/ui/src/components/board/TaskBoard.tsx
```
**Preconditions**: task T1 in Ready.
**Steps**:
1. Drag T1 onto "Developing" and release.
2. Watch network for `PATCH …/lane`.
**Expected**:
- The card returns to Ready; no PATCH request is issued.
- Dropping onto the same lane also issues nothing.
**Notes**: forward moves are system-driven only in the UI; the API itself accepts forward moves (see TSK-042).

## BRD-016 Revert an optimistic lane move when the API rejects it
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-api:PATCH /api/tasks/{id}/lane
```
**Preconditions**: task T1 in Done whose branch was merged (`isMerged:true`, `prUrl` set) — or stub the PATCH to 400.
**Steps**:
1. Drag T1 from Done to Code Review.
**Expected**:
- Card moves optimistically, the PATCH returns 400 with "This task's branch was already merged — moving it back would not undo the merged code…" and the card snaps back to Done.
**Notes**: no toast is shown by the board; the revert is silent.

## BRD-017 Hide drag-and-drop from a member without TasksExecute
```yaml
priority: P2
surface: [ui, api]
automation: manual
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-api:PATCH /api/tasks/{id}/lane
```
**Preconditions**: second member account with all permissions except TasksExecute (Enterprise org); task T1 in Developing.
**Steps**:
1. Signed in as that member, drag T1 to Ready.
2. As the member's API key, `PATCH /api/tasks/{id}/lane {targetLane:"Ready"}`.
**Expected**:
- The drop does nothing (card stays, no request).
- API returns 403.
**Notes**: needs a second human account; `RequirePermission` is hide-only, the API is the boundary.

## BRD-019 Show LaneSummaryCard on agent lanes and DoneSummaryCard on Done
```yaml
priority: P2
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-ui:packages/ui/src/components/board/LaneSummaryCard.tsx
  - orkka-ui:packages/ui/src/components/board/DoneSummaryCard.tsx
  - i18n:boardMetrics
```
**Preconditions**: project with at least one completed run (metrics non-empty).
**Steps**:
1. Open the board and inspect the top of each lane.
**Expected**:
- Drafting and Ready have no pinned card.
- Planning, Plan Ready, Pending Questions, Developing, Testing and Code Review show a "This window" card with "Agent time" and "Runs".
- Done shows the "This window" outcome card (shipped count / spend).
- Lanes with a metric show "avg {duration}" in their header with the tooltip "Average agent time in this lane across N runs — not how long cards wait here."
**Notes**: lanes with no metric instead show "{count} waiting" when tasks there need a person.

## BRD-020 Start an interactive session from the board header
```yaml
priority: P1
modes: [hybrid-repo]
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-ui:packages/ui/src/components/task/StartSessionButton.tsx
  - orkka-api:POST /api/projects/{projectId}/interactive-sessions
  - i18n:interactiveSession
```
**Preconditions**: runner online; project with exactly one repo.
**Steps**:
1. Click "Start session".
**Expected**:
- Button reads "Starting…", then the browser lands on the new task page; `POST /api/projects/{p}/interactive-sessions {repoId}` returned 201 with `currentLane:"Developing"`, `status:"Active"`, `isInteractive:true`, title "Interactive session".
- A `TaskCreated` board event adds the card to Developing for other viewers.
**Notes**: ends via "Finish & push" (covered in the sessions area). A live turn on the runner costs $0.

## BRD-021 Pick a repo before starting a session in a multi-repo project
```yaml
priority: P2
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-ui:packages/ui/src/components/task/StartSessionButton.tsx
  - i18n:interactiveSession
```
**Preconditions**: project with fixture-web and fixture-api linked.
**Steps**:
1. Click "Start session".
2. Observe the menu; press it again.
**Expected**:
- A `role="menu"` opens with the label "Which repo?" and one `menuitem` per repo name; no request is sent until a repo is chosen.
- Clicking the button again closes the menu.
- With zero repos the button is not rendered.
**Notes**: an API error is shown under the button in a `role="alert"`.

## BRD-022 Hide "Start session" and "New Task" behind their permissions
```yaml
priority: P2
surface: [ui]
automation: manual
refs:
  - orkka-ui:/p/[projectId]/board
```
**Preconditions**: member A lacks TasksExecute; member B lacks TasksCreate.
**Steps**:
1. Open the board as A; then as B.
**Expected**:
- A sees "New Task" but not "Start session" (and no lane-target toggle).
- B sees "Start session" but not "New Task".
**Notes**: second-account case; hide-only.

## BRD-023 Open the New Task form from the board and return to the board after creating
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-ui:/p/[projectId]/tasks/new
  - i18n:boardPage
```
**Preconditions**: board open.
**Steps**:
1. Click "New Task".
2. Fill Title "e2e from board", choose the repo, click "Save as Draft".
**Expected**:
- Navigation to `/p/{slug}/tasks/new?from=board`; heading "Create Task".
- After saving, toast "Task created — saved as a draft on the board." and the browser returns to `/p/{slug}/board` with the new card in Drafting.
**Notes**: `?from=tasks` returns to the list instead (TSK-001).

## BRD-024 Open the Autopilot card and validate the spend cap
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-ui:packages/ui/src/components/board/AutopilotToggle.tsx
  - i18n:autopilotToggle
```
**Preconditions**: autopilot not running.
**Steps**:
1. Click the "Autopilot" button (`aria-haspopup="dialog"`).
2. Read the "Cloud spend cap" input; clear it; type `0`; type `-5`; type `25`.
**Expected**:
- Button toggles `aria-expanded` and the card shows "Run the board unattended" with the description.
- Cap input has `min="1"`, default value `20` and hint "Per session. Tasks on your own runner cost nothing and are not capped."
- "Start autopilot" is disabled for empty, `0` and `-5`; enabled for `25`.
**Notes**: the card reopens with the last saved cap when the project has one.

## BRD-025 Show "Run with no cap" only to BillingManage holders and send uncapped
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-api:POST /api/projects/{projectId}/autopilot
  - orkka-api:DELETE /api/projects/{projectId}/autopilot
  - i18n:autopilotToggle
```
**Preconditions**: owner session (bypasses BillingManage); an API key minted WITHOUT BillingManage.
**Steps**:
1. Open the card; tick "Run with no cap" — the cap input becomes disabled.
2. Click "Start autopilot".
3. Stop it with "Stop autopilot".
4. With the restricted key: `POST /api/projects/{p}/autopilot {uncapped:true}`.
**Expected**:
- Start sends `{uncapped:true, answerEveryQuestion:false}` → 200 `running:true`, `capUsd:null`; the button reads "Autopilot $0.00".
- Step 4 returns 403.
- A member without BillingManage never sees the checkbox.
**Notes**: stopping is Manual and shows "Last run: Stopped · 0 shipped" on reopen.

## BRD-026 Show "Answer every question automatically" only with ProjectsManage and reset it each open
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-api:POST /api/projects/{projectId}/autopilot
  - i18n:autopilotToggle
```
**Preconditions**: owner session; API key without ProjectsManage.
**Steps**:
1. Open the card; tick the checkbox `data-testid="autopilot-answer-every-question"`; close the card without starting; reopen.
2. Tick it again and click "Start autopilot".
3. Open the card while running.
4. With the restricted key: `POST …/autopilot {capUsd:5, answerEveryQuestion:true}`.
**Expected**:
- After reopen the checkbox is unticked (never remembered).
- Start sends `answerEveryQuestion:true`; the running panel shows `data-testid="autopilot-answering-every-question"` "Answering every question automatically".
- Step 4 returns 403.
**Notes**: the hint says a task still stops after five automatic answers.

## BRD-027 Start autopilot and read the running panel counters
```yaml
priority: P0
modes: [hybrid-repo]
surface: [ui, api, hub]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-api:POST /api/projects/{projectId}/autopilot
  - orkka-api:GET /api/projects/{projectId}/autopilot
  - hub:TaskHub.AutopilotChanged
  - i18n:autopilotToggle
```
**Preconditions**: runner online; two Drafting tasks with repo and description; `autoApprovePlan` on to avoid a human gate.
**Steps**:
1. Open the card, keep cap 20, click "Start autopilot".
2. Reopen the card.
3. `GET /api/projects/{p}/autopilot`.
**Expected**:
- 200 `running:true`, `sessionId`, `capUsd:20`; the button shows "Autopilot $0.00 / $20.00".
- Running panel "Autopilot is running" lists Shipped, In flight, Left to pick up, Spent ("$0.00 of $20.00"); "Waiting on you" appears only when > 0.
- Drafting tasks are submitted in order; the board receives `AutopilotChanged` and lane events without reload.
**Notes**: $0 on the runner; stop the session at the end (BRD-028).

## BRD-028 Stop autopilot and read the stop reason
```yaml
priority: P0
surface: [ui, api, hub]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-api:DELETE /api/projects/{projectId}/autopilot
  - hub:TaskHub.AutopilotChanged
  - i18n:autopilotToggle
```
**Preconditions**: autopilot running (BRD-027) or started on an empty board.
**Steps**:
1. Open the card and click "Stop autopilot".
2. Reopen the card.
**Expected**:
- Button reads "Stopping…" then the card closes; `DELETE …/autopilot` returned 200 with `running:false`, `stopReason:"Manual"`.
- Reopened card shows "Last run: Stopped · N shipped" (plus ", N waiting on you" when applicable).
- A second browser on the board sees the toggle flip via `AutopilotChanged`.
**Notes**: the note under Stop says running work finishes.

## BRD-029 Reject an invalid autopilot start over the API
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/autopilot
```
**Preconditions**: project without `autopilotCloudCapUsd` saved.
**Steps**:
1. `POST /api/projects/{p}/autopilot {}`.
2. `POST …/autopilot {capUsd:0}`.
3. `POST …/autopilot {capUsd:10}` twice.
4. `DELETE …/autopilot` when nothing is running.
**Expected**:
- 1 → 400 "Set a spend cap, or explicitly choose to run without one."
- 2 → 400 "The spend cap must be greater than zero."
- 3 → first 200 `running:true`; second 200 returns the same running status (no second session).
- 4 → 200 with the current (idle) status.
**Notes**: stop the session after step 3.

## BRD-030 Show the automation-paused badge on the Autopilot button
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-api:PATCH /api/projects/{id}
  - i18n:autopilotToggle
```
**Preconditions**: project settings with `automationEnabled:false` (`PATCH /api/projects/{id}` with the full settings JSON, or the Autonomy toggle on `/automation`).
**Steps**:
1. Open the board.
2. Hover the ⏸ badge; open the card and start autopilot with cap 20; stop it.
**Expected**:
- Badge `data-testid="autopilot-automation-paused"` is present with title "Automation is paused for this project — scheduled Autopilot windows won't fire (manual start still works)."
- Manual start still returns 200.
**Notes**: restore `automationEnabled:true` afterwards.

## BRD-031 Receive TaskCreated on the board without reload
```yaml
priority: P0
surface: [ui, api, hub]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-api:POST /api/projects/{projectId}/tasks
  - hub:TaskHub.TaskCreated
```
**Preconditions**: board open in the browser (SignalR connected, `JoinBoard` done).
**Steps**:
1. From the API key: `POST /api/projects/{p}/tasks {repoId, title:"live-created"}`.
**Expected**:
- Within 5 s a card "live-created" appears in Drafting with the lane count incremented; no navigation occurred.
**Notes**: the hub needs a Clerk session; API keys cannot join hubs.

## BRD-032 Receive LaneChanged and StatusChanged on the board
```yaml
priority: P0
surface: [ui, api, hub]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-api:PATCH /api/tasks/{id}/lane
  - orkka-api:POST /api/tasks/{id}/block
  - hub:TaskHub.LaneChanged
  - hub:TaskHub.StatusChanged
```
**Preconditions**: board open; task T1 in Developing (blocked).
**Steps**:
1. `PATCH /api/tasks/{id}/lane {targetLane:"Ready"}` from the API key.
2. `POST /api/tasks/{id}/block`.
**Expected**:
- The card animates out of Developing into Ready without reload.
- After step 2 the card shows the "Blocked" badge and the "Blocked" chip count increments.
**Notes**: an open modal for T1 updates its header badges too.

## BRD-033 Receive TaskUpdated and TaskDeleted on the board
```yaml
priority: P1
surface: [ui, api, hub]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-api:PATCH /api/tasks/{id}
  - orkka-api:DELETE /api/tasks/{id}
  - hub:TaskHub.TaskUpdated
  - hub:TaskHub.TaskDeleted
```
**Preconditions**: board open; task T1 in Drafting.
**Steps**:
1. `PATCH /api/tasks/{id} {title:"renamed live", priority:"High"}`.
2. `DELETE /api/tasks/{id}`.
**Expected**:
- Card title changes to "renamed live" and the priority marker updates.
- Card disappears and the Drafting count decrements; if T1 was open in the modal it closes or shows the error state.
**Notes**: delete is Drafting-only (TSK-034).

## BRD-034 Search endpoint honours flags and caps results
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/board/search
```
**Preconditions**: tasks with titles `needle-1..3`; one archived; one with a message containing `haystack`.
**Steps**:
1. `GET /api/projects/{p}/board/search?q=needle`.
2. `…?q=needle&includeArchived=true`.
3. `…?q=haystack` then `…?q=haystack&includeMessages=true`.
4. `GET /api/projects/{other-org-slug}/board/search?q=x`.
**Expected**:
- 1 → 200 list of 2 (archived excluded); 2 → 3.
- 3 → 0 then 1.
- 4 → 404.
**Notes**: results are `TaskSummary` shapes, max 100.

## BRD-035 Board endpoint returns lanes with summaries and honours includeArchived
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/board
  - orkka-api:POST /api/tasks/{id}/archive
```
**Preconditions**: tasks in several lanes, one archived.
**Steps**:
1. `GET /api/projects/{p}/board`.
2. `GET /api/projects/{p}/board?includeArchived=true`.
3. Same call with an API key bound to another project of the org.
**Expected**:
- 1 → nine lanes in pipeline order, each task summary carries `id, number, code, title, currentLane, status, priority, isBlocked, blockedBy, mergeFailureReason, milestoneId` (no `executionTarget` — removed with cloud execution); archived task absent.
- 2 → archived task present.
- 3 → 200 (project binding is only enforced on MCP — documented asymmetry).
**Notes**: reads are open to every member.

## BRD-036 Render the Code view and select a task in the rail
```yaml
priority: P2
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - i18n:codeView
```
**Preconditions**: task T1 staged in Developing (blocked), task T2 in Drafting.
**Steps**:
1. Open `/p/{slug}/board?view=code`.
2. Click T1 in the left rail; click "Back to task list".
**Expected**:
- Heading "Code"; header count "2 tasks"; rail sections "In flight · 1" and "Other tasks · 1"; empty pane text "Pick a task to watch its code".
- Selecting T1 shows its conversation column and the file tree "Changed files" with "No files changed yet."; the URL gains `?view=code&task={number}`.
- "Back to task list" deselects.
**Notes**: live diffs need a running agent (covered by hybrid task runs).

## BRD-037 Show the compact provider-mismatch strip on the board
```yaml
priority: P2
modes: [hybrid-repo, hybrid-folder]
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-ui:apps/local/components/runner/ProviderMismatchBanner.tsx
  - orkka-api:PUT /api/projects/{projectId}/agents/{agentId}
  - i18n:providerMismatch
```
**Preconditions**: runner online with only Claude ready; one agent moved to a Codex model via `PUT /api/projects/{p}/agents/{id} {model:"<codex model id>"}`.
**Steps**:
1. Open the board.
2. Click "Review team".
3. Restore the agent's model; reload.
**Expected**:
- A one-line amber strip under the header reads "1 agent uses a model from a provider that isn't signed in on your runner — its tasks will wait until you sign in there." with a "Review team" link to `/p/{slug}/team`.
- With no mismatch (or a runner that never connected) the strip is absent.
**Notes**: the full banner with "Switch team to {provider}" lives on the Team page (TEAM-033).

## BRD-038 Show the newest agent message on the card via TaskLastMessageChanged
```yaml
priority: P2
modes: [hybrid-repo]
surface: [ui, hub]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-ui:packages/ui/src/components/board/TaskCard.tsx
  - hub:TaskHub.TaskLastMessageChanged
since: 2026-09-15
```
**Preconditions**: board open; a task whose agent is mid-run on the runner (or a live interactive session, SES-003).
**Steps**:
1. Watch the task's card while the agent streams messages.
2. Read the card's preview line and its `title` attribute.
3. Note the unread dot and whether the lanes refetch.
**Expected**:
- A `TaskLastMessageChanged {taskId, preview, messageType, agentName, createdAt}` event updates only that card: a one-line preview prefixed with "{agentName}:", the full text in the `title` tooltip; a `Question` preview is styled amber and an `Error` preview red.
- No unread indicator lights up and no lane refetch happens for these events (informational only; refreshing on every chunk would storm the API).
- Reloading the board renders the same preview from `lastAgentMessage` in the board payload.
**Notes**: the preview is not shown for tasks without an agent message.

## BRD-039 Workspace board shows every project's tasks, each badged with its project
```yaml
priority: P0
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/board
  - orkka-api:GET /api/board
  - orkka-ui:packages/ui/src/components/board/TaskCard.tsx
  - i18n:workspaceBoard
since: 2026-11-19
```
**Preconditions**: a fresh workspace with projects `e2e-shop-<stamp>` (key SHOP) and `e2e-bill-<stamp>` (key BILL), one Drafting task in each (both number 1).
**Steps**:
1. Open `/board` (or click "Workspace board" in the sidebar).
2. `GET /api/board`.
**Expected**:
- Heading "Workspace board", subtitle "Every task in {workspace name}, across its projects"; the sidebar item "Workspace board" is highlighted.
- The Drafting lane holds both cards; each card carries a project badge (`e2e-shop-…`, `e2e-bill-…`) and its own code (SHOP-1, BILL-1).
- The project filter reads "All projects" and lists both projects by name; there is no metrics strip and no time-window selector (metrics are per project).
- API: 200 `{lanes:[…]}` in pipeline order; every card has `project {id, key, name, slug, hasTesting}` and a `code` with its own project's key.
**Notes**: every task in Orkka is the developer's own work (tracker intake only takes tickets assigned to them), so this is everything assigned to them in the workspace — there is no assignee filter.

## BRD-040 Narrow the workspace board to one project from the filter; it stays in the URL
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/board
  - orkka-api:GET /api/board
  - i18n:workspaceBoard
since: 2026-11-19
```
**Preconditions**: as BRD-039.
**Steps**:
1. On `/board`, pick `e2e-bill-…` in the project filter.
2. Reload; click "Open project board".
3. Open `/board?project=no-such-project`; click "Show all projects".
4. `GET /api/board?projectId={billSlug}`, then `?projectId={billId}`.
**Expected**:
- The URL becomes `/board?project={billSlug}`; only BILL-1 shows and cards carry no project badge; an open `?task=` is dropped.
- After reload the filter still reads `e2e-bill-…`; "Open project board" goes to `/p/{billSlug}/board`.
- An unknown `?project=` shows every project with the notice "That project is not in this workspace, so every project is shown."; "Show all projects" removes the parameter.
- API: both forms return only BILL's tasks.

## BRD-041 The workspace board's Testing lane, drag rules and archived toggle
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/board
  - orkka-api:GET /api/board
  - orkka-api:PATCH /api/tasks/{id}/lane
  - orkka-ui:packages/ui/src/components/board/LaneColumn.tsx
since: 2026-11-19
```
**Preconditions**: as BRD-039, with QA enabled on BILL only (`PATCH /api/projects/{bill} {settings:{…, qaEnabled:true}}`); a SHOP task and a BILL task staged in Code Review; one more SHOP task archived.
**Steps**:
1. `GET /api/board`; `GET /api/board?projectId={shopSlug}`.
2. On `/board`, drag the BILL card from Code Review to Testing; drag the SHOP card toward Testing, then to Developing.
3. Tick "Include archived".
**Expected**:
- With every project the lanes include Testing (BILL tests); narrowed to SHOP they do not. `project.hasTesting` is true on BILL cards, false on SHOP cards.
- The BILL card drops into Testing (`PATCH …/lane {targetLane:"Testing"}` → 200). While the SHOP card is dragged, Testing is dimmed and refuses the drop (no request); Developing accepts it.
- "Include archived" refetches `GET /api/board?includeArchived=true` and the archived SHOP card appears faded.

## BRD-042 Open a task from the workspace board in its own project
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:/board
  - orkka-ui:packages/ui/src/components/board/TaskDetailModal.tsx
since: 2026-11-19
```
**Preconditions**: as BRD-039 (SHOP-1 and BILL-1 share the number 1).
**Steps**:
1. On `/board`, click the BILL-1 card.
2. Reload; click the modal's link to the task page.
**Expected**:
- The URL gains `?task=BILL-1` (the code, not the number — numbers repeat across projects) and the modal shows BILL-1, not SHOP-1.
- After reload the same task is open; its full-page link goes to `/p/{billSlug}/tasks/1`.

## BRD-043 New task from the workspace board asks which project
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:/board
  - orkka-ui:/p/[projectId]/tasks/new
  - i18n:workspaceBoard
since: 2026-11-19
```
**Preconditions**: as BRD-039.
**Steps**:
1. On `/board` (all projects), click "New Task"; pick `e2e-shop-…`.
2. On `/board?project={billSlug}`, click "New Task".
3. Repeat step 1 in a workspace with a single project.
**Expected**:
- Step 1: a menu opens with the heading item "New task in…" (not selectable) and both projects; picking SHOP goes to `/p/{shopSlug}/tasks/new?from=board`.
- Step 2: no menu — the button links straight to `/p/{billSlug}/tasks/new?from=board`.
- Step 3: no menu — the only project is used.
- Without TasksCreate the button is absent.

## BRD-044 The workspace board updates live for every project
```yaml
priority: P1
surface: [ui, hub]
automation: planned
refs:
  - orkka-ui:/board
  - orkka-ui:packages/core/src/hooks/use-board-signalr.ts
  - hub:TaskHub.LaneChanged
  - hub:TaskHub.TaskCreated
since: 2026-11-19
```
**Preconditions**: as BRD-039; `/board` open in the browser.
**Steps**:
1. From another client, `PATCH /api/tasks/{shopTask}/lane {targetLane:"Ready"}` then `POST /api/tasks/{shopTask}/block`.
2. `POST /api/projects/{bill}/tasks {title:"live-created"}`.
3. Pick `e2e-shop-…` in the filter and move the BILL task the same way.
**Expected**:
- The hub connection invokes `JoinBoard` once per project of the workspace (each checked against the caller's workspace, as on a project board).
- Step 1: the SHOP card moves to Ready without a reload.
- Step 2: the board refetches `GET /api/board` and "live-created" appears in Drafting with its BILL badge and code.
- Step 3: narrowed to SHOP, the connection has left BILL's group (`LeaveBoard`) and the BILL move changes nothing on screen.

## BRD-045 Workspace board isolation: another workspace's projects never show
```yaml
priority: P0
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/board
  - orkka-api:GET /api/board/search
since: 2026-11-19
```
**Preconditions**: workspaces A and B, each with a project and a task (`needle-A`, `needle-B`).
**Steps**:
1. With `X-Orkka-Workspace: A`: `GET /api/board`; `GET /api/board/search?q=needle`.
2. `GET /api/board?projectId={B's project id}` and `?projectId={B's slug}`; `GET /api/board/search?q=needle&projectId={B's id}`.
3. `GET /api/board?projectId={random ULID}`.
4. In a workspace with no projects: `GET /api/board`.
**Expected**:
- 1 → only A's task; the search returns `needle-A` only.
- 2 and 3 → 404 (another workspace's project is indistinguishable from none).
- 4 → 200 with the lanes and no tasks.

## BRD-046 Search the workspace board
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/board
  - orkka-api:GET /api/board/search
since: 2026-11-19
```
**Preconditions**: as BRD-039 with titles "Export the cart" (SHOP) and "Export invoices" (BILL).
**Steps**:
1. On `/board`, type `export` in the search box.
2. Narrow the filter to `e2e-shop-…`; search again.
3. `GET /api/board/search?q=export&includeArchived=true&includeMessages=true`.
**Expected**:
- Step 1: the grid shows both cards with their project badges (`GET /api/board/search?q=export…`).
- Step 2: only "Export the cart" (`…&projectId={shopSlug}`).
- Step 3: 200, the same card shape as the board, at most 100.

## BRD-047 The project board links to the workspace board
```yaml
priority: P2
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-ui:/board
  - i18n:boardPage
since: 2026-11-19
```
**Preconditions**: any project.
**Steps**:
1. Open `/p/{slug}/board`; click "Workspace board" next to the view switch.
**Expected**:
- Lands on `/board` showing every project of the workspace. The link is hidden below the `sm` breakpoint (the sidebar item "Workspace board" remains).
