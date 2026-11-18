---
area: RDM
title: Roadmap — milestones, AI planning, shipping
modes-default: [all]
---

Roadmap = ordered milestones (`Planned | Active | Completed | Cancelled`, `integrationMode: Branch | Trunk`, `shipState: NotReady | Pending | Shipping | Shipped | Failed`) plus a Backlog of tasks with no milestone. AI planning is a queued Worker job (`milestone-plans/{jobId}`) that spends credits through the platform key, so those cases are `cloud-*` with `costUsd`.

## RDM-001 Roadmap page renders with the empty state
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/roadmap
  - orkka-api:GET /api/projects/{projectId}/milestones
  - i18n:roadmapPage
```
**Preconditions**: fresh Unmanaged project with no milestones.
**Steps**:
1. Open `/p/{slug}/roadmap`.
**Expected**:
- Heading "Roadmap", subtitle "What this project is working toward, in order."; sidebar "Roadmap" highlighted.
- Buttons **Plan with AI** and **New milestone**; empty text "No milestones yet. Group related tasks under an outcome to see progress here."
- Backlog section titled "Backlog" with hint "Tasks that aren't part of a milestone yet."; `GET …/milestones` → `{milestones:[], …}`.

## RDM-002 Create a milestone from the New milestone form
```yaml
priority: P0
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/roadmap
  - orkka-api:POST /api/projects/{projectId}/milestones
  - orkka-api:GET /api/projects/{projectId}/milestones
  - i18n:roadmapPage
```
**Preconditions**: project `Ready`; caller has TasksCreate.
**Steps**:
1. Click **New milestone**; type "E2E milestone A" in **Title**, "Homepage lists three features" in **Outcome** (placeholder "What is true when this is done?"); click **Create**.
**Expected**:
- `POST …/milestones {title, outcome}` → 201 with `status: "Planned"`, `integrationMode: "Branch"`, `position` = last.
- Card shows the title, outcome, status "Planned", progress "0 of 0 done" / "No tasks yet", **Edit**, **Delete**, **Move up/down**.
- **Cancel** closes the form without a request.

## RDM-003 Title and outcome validation
```yaml
priority: P2
surface: [api, ui]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/milestones
  - orkka-api:PATCH /api/projects/{projectId}/milestones/{id}
  - orkka-ui:/p/[projectId]/roadmap
```
**Preconditions**: project `Ready`.
**Steps**:
1. `POST …/milestones {"title":""}`; `{"title":"<201 chars>"}`; `{"title":"ok","outcome":"<2001 chars>"}`.
2. `PATCH …/milestones/{id} {"title":"<201 chars>"}`.
3. In the UI the Title input has `maxLength=200`.
**Expected**:
- 400 "Title is required."; 400 "Title must be 200 characters or fewer."; 400 "Outcome must be 2000 characters or fewer." (same messages on PATCH).
- UI **Create** is disabled with an empty title; typing past 200 characters is clipped.

## RDM-004 Edit a milestone's title and outcome
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/roadmap
  - orkka-api:PATCH /api/projects/{projectId}/milestones/{id}
  - i18n:roadmapPage
```
**Preconditions**: RDM-002 milestone.
**Steps**:
1. Click **Edit**; change Title to "E2E milestone A2" and clear Outcome; click **Save**.
**Expected**:
- `PATCH …/milestones/{id}` → 200; card shows the new title and "No outcome set".
- Without TasksEdit → 403 and the card is unchanged.

## RDM-005 Move milestones up and down
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/roadmap
  - orkka-api:PATCH /api/projects/{projectId}/milestones/{id}
  - orkka-api:GET /api/projects/{projectId}/milestones
```
**Preconditions**: milestones A, B, C created in that order.
**Steps**:
1. On C click **Move up**; on A click **Move down**.
2. `GET …/milestones`.
**Expected**:
- After **Move up** on C the order is A, C, B; after **Move down** on A it is C, A, B — each click issues one PATCH carrying the new `position`.
- `GET …/milestones` returns the same order (sorted by `position` ascending); **Move up** is disabled on the first card and **Move down** on the last.

## RDM-006 Delete a milestone moves its tasks to the Backlog
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/roadmap
  - orkka-api:DELETE /api/projects/{projectId}/milestones/{id}
  - orkka-api:GET /api/tasks/{id}
  - i18n:roadmapPage
```
**Preconditions**: milestone with one draft task attached (`milestoneId` set).
**Steps**:
1. Click **Delete**; accept the `window.confirm` "Delete this milestone? Its tasks move to the Backlog — nothing is deleted."
2. `GET /api/tasks/{taskId}`.
**Expected**:
- `DELETE …/milestones/{id}` → 204; card gone; the task appears under "Backlog" and its `milestoneId` is null.
- Dismissing the confirm sends nothing. Without TasksDelete → 403.

## RDM-007 Milestone detail page
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/roadmap/[milestoneId]
  - orkka-api:GET /api/projects/{projectId}/milestones/{id}
since: 2026-09-15
```
**Preconditions**: Milestone with 2 tasks (one Draft, one Completed).
**Steps**:
1. Click the milestone title; read the page.
2. `GET …/milestones/{id}`.
**Expected**:
- "Tasks (2)" lists both by number; no integration-branch hint or ship-state controls.
- API includes `tasks[]` and `neighbours`; `branchName` is null. Unknown id → 404.

## RDM-008 Plan with AI: submit a goal and watch the job progress
```yaml
priority: P0
modes: [hybrid-repo]
surface: [ui, api]
automation: planned
costUsd: 0
refs:
  - orkka-ui:/p/[projectId]/roadmap
  - orkka-api:POST /api/projects/{projectId}/milestones/plan
  - orkka-api:GET /api/projects/{projectId}/milestone-plans/{jobId}
  - hub:TaskHub.MilestonePlanChanged
  - i18n:roadmapPage
```
**Preconditions**: fixture repo linked; credits available.
**Steps**:
1. Click **Plan with AI**; type "Add a footer with three links" in "What do you want to build?"; click **Plan**.
2. Poll `GET …/milestone-plans/{jobId}` every 3 s until `status` is `Completed` or `Failed`.
**Expected**:
- `POST …/milestones/plan {goal}` → 200/202 with `MilestonePlanJobResponse {jobId, status:"Pending"|"Running", step}`.
- Progress labels cycle "Queued" → "Reading your project" → "Planning the work" → "Working out the build order", with "{time} elapsed" and "This keeps running if you leave. The plan will be here when you come back."
- Completed job carries `plan` with `tasks[]` (each title, description, dependencies); UI shows "Proposed plan", "{count} tasks", hint "Edit or drop anything before accepting. Nothing is created until you do."

## RDM-009 Cancel the goal form and discard a proposal
```yaml
priority: P2
modes: [hybrid-repo]
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/roadmap
  - i18n:roadmapPage
```
**Preconditions**: RDM-008 proposal on screen (or the goal form open); no spend of its own.
**Steps**:
1. Open **Plan with AI**, type text, click **Cancel**.
2. With a proposal shown, click **Discard**.
**Expected**:
- Cancel closes the form; no request is sent.
- Discard removes the proposal; `GET …/milestones` unchanged (nothing created).

## RDM-010 Planning while the runner is offline fails with the runner reason
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/milestones/plan
  - orkka-api:GET /api/projects/{projectId}/milestone-plans/{jobId}
since: 2026-09-15
```
**Preconditions**: Runner stopped.
**Steps**:
1. Roadmap → "Plan with AI"; submit a goal.
2. Poll the job.
**Expected**:
- The job fails with `RunnerOffline`; the UI says "Your runner is offline or unavailable — run `./orkka start` to …". No platform-key fallback, no credits spent.

## RDM-011 Active plan job survives a reload
```yaml
priority: P2
modes: [hybrid-repo]
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/roadmap
  - orkka-api:GET /api/projects/{projectId}/milestone-plans/active
```
**Preconditions**: RDM-008 job still `Running` (piggybacks on that case's spend).
**Steps**:
1. Reload the roadmap; `GET …/milestone-plans/active`.
**Expected**:
- API returns the running job; UI re-attaches to it and shows the progress panel (not the empty goal form); when it completes the proposal appears without another submit.

## RDM-012 Accept the plan to create the milestone and its tasks
```yaml
priority: P0
modes: [hybrid-repo]
surface: [ui, api]
automation: planned
costUsd: 0
refs:
  - orkka-ui:/p/[projectId]/roadmap
  - orkka-api:POST /api/projects/{projectId}/milestones/accept
  - orkka-api:GET /api/projects/{projectId}/milestones
  - orkka-api:GET /api/projects/{projectId}/board
  - i18n:roadmapPage
```
**Preconditions**: RDM-008 proposal with ≥ 2 tasks.
**Steps**:
1. Click **Remove** on one proposed task; click **Create milestone and tasks**.
2. Try again with every task removed (API: `POST …/milestones/accept {tasks:[]}`).
**Expected**:
- Accept → 201; a new milestone with the goal-derived title and N−1 tasks (`status: "Draft"`, `milestoneId` set, dependencies preserved) appear on the board and in the milestone card ("{count} not started").
- Empty → 400 "A plan needs at least one task."; project with no repo → 400 "This project has no repositories yet — tasks need one. Link a repo first."

## RDM-013 Generate tasks for an existing milestone from its detail page
```yaml
priority: P1
modes: [hybrid-repo]
surface: [ui, api]
automation: planned
costUsd: 0
refs:
  - orkka-ui:/p/[projectId]/roadmap/[milestoneId]
  - orkka-api:POST /api/projects/{projectId}/milestones/plan
  - orkka-api:POST /api/projects/{projectId}/milestones/accept
  - i18n:milestonePage
```
**Preconditions**: empty milestone with an outcome.
**Steps**:
1. Open the detail page — hint "This milestone has no tasks yet. Generate them when you are ready to start it."; click **Generate tasks**.
2. Accept the proposal.
**Expected**:
- `POST …/milestones/plan {milestoneId, goal: <outcome>}`; accept adds tasks to **this** milestone (no new milestone); button label becomes **Plan more tasks** once tasks exist.

## RDM-014 Progress line counts done, in flight and needs-you
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/roadmap
  - orkka-api:GET /api/projects/{projectId}/milestones
  - i18n:roadmapPage
```
**Preconditions**: milestone with 3 tasks: one Completed, one Developing, one WaitingPlanApproval.
**Steps**:
1. Open the roadmap.
**Expected**:
- Card reads "1 of 3 done", "1 in flight", "1 need you"; `MilestoneCountsResponse` in the API matches (`done:1, inFlight:1, needsAttention:1, total:3`).

## RDM-015 Dependencies gate on the milestone completing
```yaml
priority: P1
surface: [api, ui]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/milestones/{id}
since: 2026-09-15
```
**Preconditions**: Milestone A with an unfinished task; milestone B after it with a submitted task.
**Steps**:
1. Open B's detail page; watch B's task.
**Expected**:
- B shows '"{A title}" comes before this and is not finished. You can still plan this one.'
- B's task is not picked up until A completes; A's completion sets `status:"Completed"` and `shipState:"Shipped"` together (trunk only — no ship sweep).

## RDM-018 Backlog lists tasks without a milestone
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/roadmap
  - orkka-ui:/p/[projectId]/tasks/new
  - orkka-api:GET /api/projects/{projectId}/board
  - i18n:taskMilestone
```
**Preconditions**: one draft task created with Milestone "Backlog (no milestone)" and one attached to a milestone.
**Steps**:
1. Open the roadmap Backlog section.
**Expected**:
- Only the unattached task is listed; the create form's **Milestone** select shows "Backlog (no milestone)" as the default option.

## RDM-019 Forecast summary on a milestone card
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/roadmap
  - orkka-api:GET /api/projects/{projectId}/milestones/{id}/forecast
  - i18n:forecast
```
**Preconditions**: milestone with open tasks; a project with finished-task history and one without.
**Steps**:
1. `GET …/milestones/{id}/forecast`; read the card.
**Expected**:
- With history: "~{time} of agent work left" (forecasts are time-only — the cost is always `null`, so the "{cost} and ~{time}" variant never shows) plus "based on {covered} of {open} open tasks" when `tasksCovered < tasksOpen`.
- Without history: "Not enough history to estimate yet".

## RDM-021 Milestone status update and validation
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:PATCH /api/projects/{projectId}/milestones/{id}
```
**Preconditions**: a Planned milestone.
**Steps**:
1. `PATCH {"status":"Cancelled"}`; then `{"status":"Someday"}`; then `{"repoId":"not-a-ulid"}`.
**Expected**:
- 200 with `status: "Cancelled"` (card shows "Cancelled").
- 400 "Status must be one of: Planned, Active, Completed, Cancelled."; 400 "RepoId must be a valid id or an empty string to clear it."

## RDM-022 Milestone write endpoints enforce permissions
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/milestones
  - orkka-api:PATCH /api/projects/{projectId}/milestones/{id}
  - orkka-api:DELETE /api/projects/{projectId}/milestones/{id}
  - orkka-api:POST /api/projects/{projectId}/milestones/plan
  - orkka-api:POST /api/projects/{projectId}/milestones/accept
  - orkka-api:GET /api/projects/{projectId}/milestones
```
**Preconditions**: three keys: A (`TasksEdit` only), B (`TasksCreate` only), C (`TasksDelete` only).
**Steps**:
1. A: POST create, POST plan, POST accept, DELETE. B: PATCH, DELETE. C: POST create, PATCH.
2. Any key: `GET …/milestones`.
**Expected**:
- Every call outside the key's permission → 403; the matching ones succeed (B create 201, A PATCH 200, C DELETE 204); GET → 200 for all.
- Roadmap UI hides **New milestone**/**Plan with AI** without TasksCreate, **Edit** without TasksEdit, **Delete** without TasksDelete.

## RDM-023 Foreign or unknown milestone ids are 404
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/milestones/{id}
  - orkka-api:GET /api/projects/{projectId}/milestones/{id}/forecast
  - orkka-api:GET /api/projects/{projectId}/milestone-plans/{jobId}
```
**Preconditions**: a milestone id from another project in the same org.
**Steps**:
1. `GET /api/projects/{projectA}/milestones/{idFromProjectB}`; `…/forecast`; `GET …/milestone-plans/01HZZZZZZZZZZZZZZZZZZZZZZZ`.
**Expected**:
- All 404 (never 403 — the resolver hides existence).

## RDM-024 Update milestone counts live via MilestoneChanged
```yaml
priority: P2
surface: [ui, api, hub]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/roadmap
  - orkka-ui:/p/[projectId]/roadmap/[milestoneId]
  - orkka-api:POST /api/projects/{projectId}/tasks
  - orkka-api:DELETE /api/tasks/{id}
  - orkka-api:DELETE /api/projects/{projectId}/milestones/{id}
  - hub:TaskHub.MilestoneChanged
  - i18n:roadmapPage
since: 2026-09-15
```
**Preconditions**: milestone `M` with 2 tasks; roadmap open in tab A, `/roadmap/{M}` open in tab B.
**Steps**:
1. `POST /api/projects/{p}/tasks {title:"e2e-rdm-live", repoId, milestoneId:M}` (draft).
2. `DELETE /api/tasks/{id}` for that draft.
3. Create a throwaway milestone `M2`, open it in tab A's list, then `DELETE /api/projects/{p}/milestones/{M2}`.
**Expected**:
- Step 1: both tabs update without reload — `M`'s card reads "0 of 3 done" and the detail's progress line shows `total:3`; the event carries fully computed `{milestoneId, status, total, done, inFlight, needsAttention, notStarted}` which replaces (never merges) the counts.
- Step 2: counts return to 2 in both tabs.
- Step 3: the `M2` row disappears from the roadmap on `MilestoneChanged {deleted:true}`.
**Notes**: the roadmap joins the same board group as the kanban; a status change (Shipped/Failed) arrives on the same event and re-badges the card.
