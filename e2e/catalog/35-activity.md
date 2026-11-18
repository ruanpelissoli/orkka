---
area: ACT
title: Activity — shift report of agent runs, outcomes and spend
modes-default: [all]
---

Page `/p/{slug}/activity` → `ActivityPage`. Controls: window `SegmentedControl` "Last 24h" / "Last 7 days" / "Last 30 days"; agent `<select aria-label="All agents">`; status `<select aria-label="All statuses">` (Running / Completed / Failed / Cancelled); view `SegmentedControl` **Digest** (`view-digest`) / **Timeline** (`view-timeline`). Data: `GET /api/projects/{p}/executions?from&to&status&agentId&limit=500` (server default window 24 h, default limit 300, max 500, `truncated` flag, `summary {runs, tasksTouched, failed, durationSeconds, tokens, cost, wastedCost}`, `tasks[]`). Zones: `activity-summary` strip, `density-strip` (drag → `density-selection`, "Narrowed to the selected range." + **Show the whole window**), Digest → "Needs you" (`attention-list`), "What moved" (`outcome-list`), "Where the credits went" / "Where the tokens went" (`spend-by-agent`, `costliest-tasks`); Timeline → `execution-bar`s + "Lanes" legend. `RunDrawer` (`run-drawer`) fetches `GET /api/tasks/{id}/executions`. Polling 20 s while a run is open, 60 s otherwise, plus board-hub refetches. Team page `AgentOpsStats` reads `GET /api/projects/{p}/agent-stats?window=24h|7d|30d`. All reads are open to members.

Data precondition shared by most cases: **the suite project has ≥ 1 finished execution** — run the standard fixture task ("add a list item '<token>' to the features list") to Done in the current mode before this area, and keep one task that failed (e.g. a task whose check command fails) for the attention cases.

## ACT-001 Render the Activity page and its empty state
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/activity
  - orkka-api:GET /api/projects/{projectId}/executions
  - i18n:activityPage
```
**Preconditions**: fresh Ready project (no runs).
**Steps**:
1. Open `/p/{slug}/activity`.
**Expected**:
- Sidebar "Activity" highlighted; heading "Activity" + subtitle "What your agents did, what came of it, and what it cost."
- Window control with "Last 24h" selected; agent select showing "All agents" (rendered only when the project has agents); status select "All statuses"; view control with "Digest" selected (`view-digest` active, `view-timeline` present).
- Server-rendered `GET …/executions?limit=500` → 200 `{executions:[], tasks:[], summary:{runs:0,…}, truncated:false}`; page shows "No runs in this window." + "Submit a task or start Autopilot and the timeline fills in as agents work."; no `activity-summary`, no `density-strip`.

## ACT-002 Switch the window between 24h, 7 days and 30 days
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/executions
  - i18n:activityPage
```
**Preconditions**: project with runs (shared precondition).
**Steps**:
1. Click "Last 7 days".
2. Click "Last 30 days"; then "Last 24h".
**Expected**:
- Each click issues `GET …/executions?from={now-7d ISO}&to={now ISO}&limit=500` (then 30 d, then 24 h); `from`/`to` in the response bracket the chosen span.
- `activity-summary` and the lists re-render from the new response; the density strip's axis spans the new window (Timeline tick labels switch from times to dates for 30 d).
- A stale response from an earlier click never overwrites a newer one (request sequencing).

## ACT-003 Filter by agent
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/executions
  - i18n:activityPage
```
**Preconditions**: runs by ≥ 2 agents in the window (e.g. PM planning run + developer run).
**Steps**:
1. Pick a developer agent in the agent select.
2. Pick "All agents".
**Expected**:
- `GET …/executions?…&agentId={id}` → every row has that `agentId`; `summary.runs` equals the row count for that agent (the agent filter applies to the summary too); `spend-by-agent` has one row.
- "All agents" removes the parameter and restores all rows.

## ACT-004 Filter by status without changing the window summary
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/executions
  - i18n:activityPage
```
**Preconditions**: window with ≥ 1 Completed and ≥ 1 Failed run.
**Steps**:
1. Note `activity-summary` values; pick "Failed" in the status select.
2. Pick "Completed"; then "All statuses".
3. `GET …/executions?status=Bogus`.
**Expected**:
- `GET …/executions?…&status=Failed` returns only `status:"Failed"` rows (`execution-bar[data-status="Failed"]` in Timeline), while `summary.runs` and the strip numbers stay the whole-window figures.
- Unknown status → 400 field `Status` "Unknown execution status."

## ACT-005 Switch between Digest and Timeline views
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/activity
  - orkka-ui:packages/ui/src/components/activity/ExecutionTimeline.tsx
  - i18n:activityPage
```
**Preconditions**: project with runs.
**Steps**:
1. With Digest selected, read the zone headings.
2. Click `view-timeline`.
3. Click `view-digest`.
**Expected**:
- Digest: sections "Needs you" (count pill when > 0), "What moved" (count = tasks), and "Where the credits went" (or "Where the tokens went" when every run in the window cost $0 — Local / subscription runs).
- Timeline: a time axis ending in "now", one row per task (`{KEY} {title}` link to `/p/{slug}/tasks/{taskId}`), `execution-bar` elements coloured by lane with `data-status`, running bars pulsing, and the legend "Lanes" listing the lane names except Drafting/Plan ready/Done.
- Switching views does not refetch.

## ACT-006 Match the summary strip to the executions endpoint
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/executions
  - i18n:activityPage
```
**Preconditions**: project with runs in the last 24 h, at least one failed.
**Steps**:
1. `GET /api/projects/{p}/executions?limit=500` and read `summary`.
2. Open the page; read `activity-summary`.
**Expected**:
- Strip shows "{summary.runs} runs", "{summary.tasksTouched} tasks", "{summary.failed} failed" (red, only when > 0), "{formatDuration(summary.durationSeconds)} of agent time".
- Billed modes: "{credits} credits" where credits = `summary.cost` × 1000, and "{credits} credits wasted" (title "Credits spent on runs that failed or were cancelled.") when `wastedCost > 0`.
- Costless window (`cost == 0 && tokens > 0`): "{tokens} tokens" instead of credits and no wasted figure.

## ACT-007 Surface failures and waits in the attention list
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:packages/ui/src/components/activity/AttentionList.tsx
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - i18n:activityPage
```
**Preconditions**: window with one failed run and one task waiting on a human (e.g. `WaitingPlanApproval`).
**Steps**:
1. In Digest read "Needs you".
2. Click the task title of the failed item.
3. Back; click **Open run** on the same item.
**Expected**:
- `attention-list` items carry `data-attention-kind` (`failed`, `mergeFailure`, `setupFailure`, `retryLoop`, `longRunning`, `awaitingHuman`, `stalled`) with badges "Failed" / "Merge blocked" ("Update blocked" on Managed) / "Setup failed" / "Retry loop" / "Still running" / "Waiting on you" / "Stalled"; the failed item shows its error text on the page (mono, red) plus agent name and credits.
- Title link navigates to `/p/{slug}/tasks/{n}`; **Open run** opens `run-drawer` for that execution.
- More than 6 items collapse behind **Show all {n}** / **Show less**; an empty list reads "Nothing needs you." + "No failures, no retry loops, nothing waiting on a person."

## ACT-008 List task outcomes with lane progression
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:packages/ui/src/components/activity/OutcomeList.tsx
  - orkka-api:GET /api/projects/{projectId}/executions
  - i18n:activityPage
```
**Preconditions**: a task that went to Done (merged) in the window; on Unmanaged projects it has a `prUrl`.
**Steps**:
1. Read "What moved".
2. `GET …/executions` and compare `tasks[]`.
**Expected**:
- `outcome-list` has one `li[data-outcome]` per task in `tasks[]`: `{KEY} {title}` link, outcome badge ("Merged" / "PR open" / "Blocked" / "Waiting on you" / "Done" / "In progress"; Managed projects read "Live" / "In review"), lane progression chips (e.g. Planning → Developing → Code review), "{n} runs", credits, and "{n} review rounds" when `reviewCycleCount > 1`.
- "PR" external link present only on non-Managed projects when `prUrl` is set.

## ACT-009 Break spend down by agent and by task
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:packages/ui/src/components/activity/SpendPanel.tsx
  - orkka-api:GET /api/projects/{projectId}/executions
  - i18n:activityPage
```
**Preconditions**: project with runs.
**Steps**:
1. Read the "Where the credits went" zone.
2. Click a task in `costliest-tasks`.
**Expected**:
- `spend-by-agent` table columns "Agent", "Runs", "Success", "Avg", "Credits" (or "Tokens" when costless); one row per agent (`Unassigned` for agentless runs), success % coloured (≥ 90 % green, ≥ 60 % amber, else red), running marker "●" when a run is open, wasted credits as "−{n}".
- `costliest-tasks` lists tasks by spend with "{n} runs" and credits; clicking navigates to the task page. With no spend: "No credits recorded in this window."

## ACT-010 Open a run in the drawer and run the task again
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:GET /api/tasks/{taskId}/executions
  - orkka-api:POST /api/tasks/{id}/retry
  - i18n:activityPage
```
**Preconditions**: a failed task run in the window (retryable); TasksExecute.
**Steps**:
1. In Timeline click the failed `execution-bar` (or **Open run** in "Needs you").
2. Read the drawer; click **Run again**.
**Expected**:
- `GET /api/tasks/{taskId}/executions` → drawer `run-drawer` titled "Run": status badge ("Failed"), lane chip, agent name · role, stats "Duration", "Credits", "Tokens", "Files changed"; red mono error block; "Files changed" list (`ADD`/`MOD`/`DEL` + path) when any; "Agent log" `<pre>` when present.
- **Run again** (hint "Puts the task back in the queue where it stopped.") → `POST /api/tasks/{id}/retry` → toast "Task queued to run again."; drawer closes and the page refetches.
- A run that has left the window shows "That run is no longer in this window."; members without TasksExecute see no **Run again**.
**Notes**: the retry re-queues real work on the runner — cancel the task afterwards.

## ACT-011 Narrow the window by dragging on the density strip
```yaml
priority: P2
surface: [ui]
automation: planned
refs:
  - orkka-ui:packages/ui/src/components/activity/ActivityDensityStrip.tsx
  - i18n:activityPage
```
**Preconditions**: ≥ 2 runs at different times in the window.
**Steps**:
1. Pointer-drag across the middle third of `density-strip`.
2. Read the strip and zones; click **Show the whole window**.
3. Drag again; then single-click the strip.
**Expected**:
- 48 buckets render with heights ∝ run starts (`data-runs`), failures stacked in red.
- After the drag a `density-selection` overlay covers the dragged span; the line "Narrowed to the selected range." with **Show the whole window** becomes visible; `activity-summary` switches to range totals (runs, tasks, failed, duration, credits — no tokens/wasted), and the lists only include rows whose start falls in the range (no refetch). An empty slice reads "No runs in the selected range." + "Drag across the strip again, or clear the selection."
- **Show the whole window** and a plain click (< 4 px travel) both clear the selection.

## ACT-012 Show the truncation notice when the page is capped
```yaml
priority: P2
surface: [api, ui]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/executions
  - i18n:activityPage
```
**Preconditions**: project with ≥ 2 runs.
**Steps**:
1. `GET …/executions?limit=1`.
2. `GET …/executions?limit=9999`.
3. UI: intercept the page's executions request and respond with the step-1 body (Playwright route) — or use a project with > 500 runs.
**Expected**:
- Step 1 → `executions.length == 1`, `truncated:true`, while `summary.runs` still counts the whole window.
- Step 2 → at most 500 rows (`limit` clamped 1..500).
- UI shows the amber line "Showing the {count} most recent runs — narrow the window to see everything."

## ACT-013 Recover from a failed load with Retry
```yaml
priority: P2
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/activity
  - i18n:activityPage
```
**Preconditions**: project with runs; Playwright network interception.
**Steps**:
1. Open the page; abort the next `GET …/executions` request; click "Last 7 days".
2. Restore the network; click **Retry**.
**Expected**:
- Card "Couldn't load activity." with a **Retry** button replaces the lists (the summary strip from the last good response may remain).
- Retry refetches the same query and the lists render.

## ACT-014 Validate the executions endpoint parameters and shape
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/executions
```
**Preconditions**: project with runs; API key.
**Steps**:
1. `GET …/executions` (no params).
2. `GET …/executions?from=2026-01-02T00:00:00Z&to=2026-01-01T00:00:00Z`.
3. `GET …/executions?from={now-7d}&to={now}&status=Completed&agentId={id}&limit=10`.
4. `GET /api/projects/{otherOrgProjectOrUnknown}/executions`.
**Expected**:
- Default: `to` ≈ now, `from` = `to` − 24 h; rows sorted newest first, each `{executionId, taskId, taskKey, taskTitle, lane, status, agentId, agentName, agentRole, startedAt, completedAt, durationSeconds, cost, tokensUsed, errorMessage}`; `tasks[]` rows carry `prUrl`, `reviewCycleCount`, `qaCycleCount`, `baseSyncCycleCount`, `lastActivityAt`.
- `from ≥ to` → 400 field `From` "'from' must be earlier than 'to'."
- Combined filters → ≤ 10 rows, all `Completed` and by that agent; `summary` reflects the agent filter but not the status filter.
- Unknown project → 404.

## ACT-015 Show agent stats for a window on the Team page
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/agent-stats
  - orkka-ui:/p/[projectId]/team
  - i18n:teamStats
```
**Preconditions**: project with runs by a developer agent.
**Steps**:
1. Open `/p/{slug}/team?agent={agentId}`; find `agent-ops-stats`.
2. In the "Time window" control (`aria-label="Time window"`) click "24h", "7d", "30d".
3. `GET …/agent-stats?window=7d` and `?window=bogus`.
**Expected**:
- Each click requests `GET /api/projects/{p}/agent-stats?window=24h|7d|30d` → 200 `{window, from, to, agents:[{agentId, runs, successRate, avgDurationSeconds, lastError, running…}]}`.
- The panel shows "Runs", "Success", "Avg run", "Last error" (or "No runs in this window" / "{n} running") matching the response for that agent; default window is 7d.
- An unrecognised `window` falls back to the default (`window:"7d"` in the envelope).

## ACT-016 Refresh live while a run is in progress
```yaml
priority: P3
modes: [hybrid-repo]
surface: [ui, hub]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/activity
  - hub:TaskHub.StatusChanged
  - hub:TaskHub.LaneChanged
  - orkka-api:GET /api/projects/{projectId}/executions
```
**Preconditions**: runner online; a submitted task about to start.
**Steps**:
1. Open the Activity page; submit the standard task from another tab.
2. Watch the page for two minutes without interacting.
**Expected**:
- Within ~2 s of the hub `StatusChanged`/`LaneChanged` events a refetch occurs (debounced); a "Running" bar / "Still running" attention item appears without reload.
- While a run is open the page refetches every ~20 s; once no run is Running the cadence drops to ~60 s.
