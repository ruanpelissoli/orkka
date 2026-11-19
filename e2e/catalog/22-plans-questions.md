---
area: PLN
title: Plans and questions — plan approval gate, agent questions
modes-default: [hybrid-repo]
---

Fixture: project `e2e-plans-<stamp>` with fixture-web linked, runner online with Claude ready ($0). "Standard task" = "add a list item '<token>' to the features list". Project settings `autoApprovePlan:false` unless stated. The plan-review API accepts `approve|reject` (also `approved|rejected`); the UI's "Request Changes" flow sends `Rejected` with feedback — there is no separate request-changes decision for plans.

## PLN-001 Park a submitted task in Plan Ready awaiting approval
```yaml
priority: P0
surface: [ui, api, hub]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/tasks
  - orkka-api:POST /api/tasks/{id}/submit
  - orkka-api:GET /api/tasks/{taskId}/plans/latest
  - hub:TaskHub.LaneChanged
  - orkka-ui:/p/[projectId]/board
```
**Preconditions**: fixture above.
**Steps**:
1. Create the standard task and submit it.
2. Watch the board until the card reaches "Plan Ready" (≤ 10 min).
3. `GET /api/tasks/{id}` and `GET /api/tasks/{id}/plans/latest`.
**Expected**:
- Card moves Ready → Planning → Plan Ready via `LaneChanged`; final `currentLane:"PlanReady"`, `status:"WaitingPlanApproval"`.
- Latest plan has `version:1`, `status` PendingReview (rendered "Waiting Plan Approval"), `content.summary`, `content.approach`, `content.filesChanged`.
- Card shows the "Review plan" attention state and the "Needs you" chip counts 1.
**Notes**: the PM may first ask questions (PLN-011); answer them to continue.

## PLN-002 Read the plan viewer: summary, approach, files and show/hide
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-ui:packages/ui/src/components/task/PlanViewer.tsx
  - i18n:planViewer
```
**Preconditions**: task in Plan Ready (PLN-001).
**Steps**:
1. Open the task page.
2. Click a file row under "Files (N)".
3. After approving (PLN-003) reload and click "Show" / "Hide".
**Expected**:
- Card "Execution Plan" with a status badge and "v1"; summary markdown; "Approach" block; "Files (N)" rows with a C/M/D action letter and monospace path; clicking a row expands its description.
- Action row (PlansReview): forecast summary, "Approve Plan" and "Request Changes".
- Once Approved/Rejected the content collapses by default and the header offers "Show"/"Hide".
**Notes**: the plan-approval forecast is fetched with `surface=PlanApproval`.

## PLN-003 Approve the plan from the task page
```yaml
priority: P0
surface: [ui, api, hub]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-api:POST /api/tasks/{taskId}/plans/{planId}/review
  - hub:TaskHub.PlanReviewed
  - hub:TaskHub.LaneChanged
  - i18n:planViewer
```
**Preconditions**: task in Plan Ready.
**Steps**:
1. Click "Approve Plan".
2. `GET /api/tasks/{id}`.
**Expected**:
- Request `POST /api/tasks/{id}/plans/{planId}/review {decision:"Approved"}` → 200 with the plan `status:"Approved"`, `approvedAt` set.
- `PlanReviewed` and `LaneChanged` events; task `currentLane:"Developing"`, `status:"InQueue"`; the badge reads "Approved" and the actions disappear.
- The dev agent picks the task up on the runner and later opens a PR (PR-001).
**Notes**: —

## PLN-004 Request changes with feedback and get a new plan version
```yaml
priority: P0
surface: [ui, api, hub]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-api:POST /api/tasks/{taskId}/plans/{planId}/review
  - orkka-api:GET /api/tasks/{taskId}/plans
  - orkka-api:GET /api/tasks/{taskId}/messages
  - hub:TaskHub.LaneChanged
  - i18n:planViewer
```
**Preconditions**: task in Plan Ready with plan v1.
**Steps**:
1. Click "Request Changes"; type "Also add a unit test" into the textarea ("What should be changed?"); click "Reject Plan".
2. Wait for the task to return to Plan Ready; `GET …/plans` and `…/messages`.
**Expected**:
- Request `{decision:"Rejected", feedback:"Also add a unit test"}` → 204.
- Task → `Planning` / `InQueue`; a reviewer message "Plan v1 was REJECTED.\n\nFeedback: Also add a unit test …" is in the thread; plan v1 is removed from `GET …/plans`.
- The agent re-plans and a plan with `version:2` parks the task in Plan Ready again.
**Notes**: the same button pair also offers Cancel (PLN-005).

## PLN-005 Cancel the request-changes textarea and reject without feedback
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-api:POST /api/tasks/{taskId}/plans/{planId}/review
  - i18n:planViewer
```
**Preconditions**: task in Plan Ready.
**Steps**:
1. Click "Request Changes", type text, click "Cancel".
2. Click "Request Changes" and "Reject Plan" with the textarea empty.
**Expected**:
- Cancel restores "Approve Plan" / "Request Changes" and clears the text; no request.
- Empty feedback is accepted: request `{decision:"Rejected"}` → 204; the reviewer message omits the "Feedback:" line.
**Notes**: —

## PLN-006 List plan versions and the latest plan
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/tasks/{taskId}/plans
  - orkka-api:GET /api/tasks/{taskId}/plans/latest
```
**Preconditions**: task that went through PLN-004 (v2 pending).
**Steps**:
1. `GET /api/tasks/{id}/plans`; `GET /api/tasks/{id}/plans/latest`.
2. Same calls on a Drafting task.
**Expected**:
- List contains the current pending plan (rejected versions are deleted); latest returns v2 with `status` PendingReview.
- Drafting task: list `[]`, latest `null`/204 as implemented, and the task page renders no plan card.
**Notes**: reads open to any member.

## PLN-007 Validate plan review requests
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/tasks/{taskId}/plans/{planId}/review
```
**Preconditions**: task with an approved plan P1 (PLN-003) and a pending plan on another task.
**Steps**:
1. `POST /api/tasks/{id}/plans/{P1}/review {decision:"maybe"}`.
2. `… {decision:"approve"}` on the already-approved P1.
3. `POST /api/tasks/{id}/plans/{random-ulid}/review {decision:"approve"}`.
4. `… {decision:"reject", feedback:"<5001 chars>"}`.
**Expected**:
- 1 → 400 "Decision must be 'approve', 'reject', 'approved', or 'rejected'."
- 2 → 409 "Only plans in PendingReview status can be reviewed."
- 3 → 404.
- 4 → 400 "Feedback must be 5,000 characters or fewer."
**Notes**: lower-case `approve`/`reject` are accepted alongside the UI's `Approved`/`Rejected`.

## PLN-008 Hide plan actions and return 403 without PlansReview
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-api:POST /api/tasks/{taskId}/plans/{planId}/review
```
**Preconditions**: pending plan; restricted key without PlansReview; (optional) member account without PlansReview.
**Steps**:
1. `POST /api/tasks/{id}/plans/{planId}/review {decision:"approve"}` with the restricted key.
2. As the member, open the task page.
**Expected**:
- 403; the plan stays PendingReview.
- The member sees the plan content but no "Approve Plan" / "Request Changes".
**Notes**: step 2 is a second-account check (manual if no member account).

## PLN-009 Surface the pending plan in the inbox and the bell
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/inbox
  - orkka-api:GET /api/inbox
  - orkka-api:GET /api/notifications
  - i18n:inbox
```
**Preconditions**: task in Plan Ready.
**Steps**:
1. Open `/inbox`; `GET /api/inbox`; `GET /api/notifications`.
**Expected**:
- Inbox row "Approve plan" for the task (`kind:"plan_approval"`); a `PlanApprovalRequired` notification exists.
- After approval the row disappears.
**Notes**: —

## PLN-010 Skip the human gate when auto-approve plan is on
```yaml
priority: P1
surface: [api, hub]
automation: planned
refs:
  - orkka-api:PATCH /api/projects/{id}
  - orkka-api:POST /api/tasks/{id}/submit
  - orkka-api:GET /api/tasks/{taskId}/plans/latest
  - hub:TaskHub.LaneChanged
```
**Preconditions**: project settings `autoApprovePlan:true` (PATCH the full settings JSON).
**Steps**:
1. Submit the standard task; collect `LaneChanged` events.
**Expected**:
- Lanes go Ready → Planning → Developing with no PlanReady stop; `status` never `WaitingPlanApproval`.
- Latest plan is `Approved` with `approvedAt` set automatically.
**Notes**: autopilot sessions also auto-approve plans regardless of the setting.

## PLN-011 Answer a single-choice question card and resume the agent
```yaml
priority: P0
surface: [ui, api, hub]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-ui:packages/ui/src/components/chat/QuestionBlocks.tsx
  - orkka-api:POST /api/tasks/{taskId}/messages
  - hub:TaskHub.NewMessage
  - hub:TaskHub.LaneChanged
  - i18n:agentChat
```
**Preconditions**: a task whose description is deliberately ambiguous (e.g. "add an item to the list" without naming it) so the PM asks; task parked in Pending Questions (`WaitingAnswer`).
**Steps**:
1. Open the task; the banner "Agent is waiting for your answer" shows; the latest agent message renders a question card.
2. Click one option chip (`aria-pressed`), then "Send answers".
**Expected**:
- Chips: one per option; the recommended one carries a "Recommended" pill; picking another deselects the first (single-select).
- Submit → `POST /api/tasks/{id}/messages {content:"…question → answer…"}` 201; the user bubble renders as question/answer pairs; a `LaneChanged` returns the task to its source lane with `InQueue`.
- Cards on older messages are non-interactive (dimmed, disabled).
**Notes**: the composer under the thread also answers free-text (`AgentsChat` shows it; the API needs TasksExecute).

## PLN-012 Answer multi-select questions with "Other..." free text and see the preparing state
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-ui:packages/ui/src/components/chat/QuestionBlocks.tsx
  - orkka-api:POST /api/tasks/{taskId}/messages
  - i18n:agentChat
```
**Preconditions**: a question payload with `multiSelect:true` and `allowCustom:true` (real run, or a seeded `Question` message whose content holds the ```questions block).
**Steps**:
1. Read the hint "Select all that apply"; tick two options (`role="checkbox"`, `aria-checked`).
2. Click "Other..."; type into "Type your answer..."; click "Send answers".
3. While the agent streams an unterminated block observe the card.
**Expected**:
- Both options stay selected; the custom text is appended to the answer list; "Send answers" is disabled until at least one answer exists.
- Posted content formats as `question → option a, option b, custom text`.
- Mid-stream shows "Preparing questions..." with a pulsing dot; no raw JSON is rendered.
**Notes**: two ```questions blocks in one message share a single "Send answers" button.

## PLN-013 Let autopilot answer every question without waiting for a human
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/autopilot
  - orkka-api:GET /api/tasks/{taskId}/messages
  - orkka-ui:/p/[projectId]/board
  - i18n:autopilotToggle
```
**Preconditions**: owner (ProjectsManage); an ambiguous Drafting task; runner online.
**Steps**:
1. Start autopilot with "Answer every question automatically" ticked (cap 20).
2. Watch the task; `GET /api/tasks/{id}/messages`.
**Expected**:
- The task never rests in `WaitingAnswer`; the thread contains an auto-answer message listing "- {question} → **{recommended}**" and the running panel shows "Answering every question automatically".
- Without the option, autopilot still auto-answers only unguarded questions and stops on guarded topics (deleting data, billing, credentials, production).
- The fifth auto-answer is the last: a sixth question parks the task for a human.
**Notes**: stop autopilot at the end.

## PLN-014 Skip planning entirely for a task created with "Skip the planning phase"
```yaml
priority: P1
surface: [api, hub]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/tasks
  - orkka-api:POST /api/tasks/{id}/submit
  - orkka-api:GET /api/tasks/{taskId}/plans/latest
  - hub:TaskHub.LaneChanged
```
**Preconditions**: standard task created with `skipPlanning:true`.
**Steps**:
1. Submit; collect `LaneChanged` events until Developing.
2. `GET …/plans/latest`.
**Expected**:
- Lanes: Ready → Developing; no Planning, no PlanReady, no `WaitingPlanApproval`.
- A synthetic approved plan exists (`status:"Approved"`) created by the skip path.
**Notes**: the PM still reviews in Ready.

## PLN-015 Approve and reject plans through the MCP tools
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:POST /mcp
  - orkka-api:GET /api/tasks/{taskId}/plans/latest
```
**Preconditions**: Owner API key; two tasks in Plan Ready.
**Steps**:
1. `POST /mcp` `tools/list`.
2. `tools/call approve_plan {task_id}` on the first; `tools/call reject_plan {task_id, feedback}` on the second.
3. Repeat with a key lacking PlansReview.
**Expected**:
- `approve_plan` and `reject_plan` are listed for the owner key; calls return success text; the first task moves to Developing, the second to Planning with the feedback message (same effects as PLN-003/004).
- The restricted key does not list the two tools and a direct call is refused.
**Notes**: `get_inbox` says "WaitingPlanApproval → get_task then approve_plan/reject_plan".

## PLN-016 Reply to a question from the task composer instead of the card
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-ui:packages/ui/src/components/task/ConversationThread.tsx
  - orkka-api:POST /api/tasks/{taskId}/messages
  - i18n:conversationThread
```
**Preconditions**: task parked in Pending Questions.
**Steps**:
1. Type a free-text answer into "Send a message..." and submit.
2. With a restricted key lacking TasksExecute: `POST /api/tasks/{id}/messages {content:"x"}`.
3. `POST … {content:""}`.
**Expected**:
- 201, the message appears in the thread and the task resumes (same as PLN-011).
- 403.
- 400 "Content is required."
**Notes**: sending while an agent is mid-run shows "The agent is mid-run — your message is saved and will be included in its next run."
