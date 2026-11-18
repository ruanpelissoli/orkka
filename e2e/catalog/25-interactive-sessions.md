---
area: SES
title: Interactive sessions — live pair-programming on a real branch
modes-default: [hybrid-repo]
---

An interactive session **is a task from the first keystroke**: `POST /api/projects/{projectId}/interactive-sessions` creates a `Developing`/`Active` task with `isInteractive:true` and `interactiveSessionStartedAt` set, bound to one repo, never `InQueue` and excluded from the queue sweep and from `maxParallelTasks`. The task page renders it unchanged plus two affordances: the `InteractiveSessionBanner` ("Session live" → "Wrapping up", one "Finish & push" button) and the `InteractiveApprovalPanel` (one card per command the agent paused on — "Allow once" / "Always allow {key}" / "Deny"). Turns are driven by `POST /api/tasks/{taskId}/messages`; each turn is one execution that warm-resumes the previous one. Approval rows are `Pending | Allowed | AllowedForSession | Denied | Expired`; the database row is the arbiter (a settled prompt answers 409), prompts expire after 10 minutes unanswered, on pickup and on close. An expired prompt tells the agent it may ask again; a denied one tells it not to retry. Finish is always an explicit human action (`POST /api/tasks/{taskId}/interactive-session/finish`, idempotent, sets `interactiveFinishRequestedAt`); the worker commits, pushes, opens the PR and the task enters Code Review like any other. Source: `orkka-api/.claude/docs/INTERACTIVE.md`. The board-header entry point is covered in BRD-020..022.

## SES-001 Open an interactive session over the API and read the task it creates
```yaml
priority: P0
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/interactive-sessions
  - orkka-api:GET /api/tasks/{id}
  - orkka-api:GET /api/projects/{projectId}/board
  - orkka-api:src/Modules/Orkka.Modules.Tasks/Features/InteractiveSessions/StartInteractiveSessionEndpoint.cs
since: 2026-09-15
```
**Preconditions**: unmanaged project `P` with the fixture-web repo `R`; owner key; runner online.
**Steps**:
1. `POST /api/projects/{P}/interactive-sessions {repoId:R}`.
2. `GET /api/tasks/{id}` and `GET /api/projects/{P}/board`.
3. `POST /api/projects/{P}/interactive-sessions {repoId:R, title:"Fix the footer", model:"<cheapest enabled Anthropic model>"}`.
4. Finish both sessions (SES-010) or reset them during cleanup.
**Expected**:
- Step 1 → 201 with `Location: /api/tasks/{id}`; body `isInteractive:true`, `interactiveSessionStartedAt` non-null, `interactiveFinishRequestedAt:null`, `currentLane:"Developing"`, `status:"Active"`, `title:"Interactive session"`, `startedAt` non-null, `skipPlanning:true`, `milestoneId:null`, `executionTarget:"UserRunner"`, `repoId:R`; `number` is the project's next task number.
- Step 2: the board lists the card in the Developing lane; the task is never `InQueue`.
- Step 3 → 201 with the explicit title kept verbatim and `model` echoed.
**Notes**: a session can only start fresh — there is no endpoint that turns an existing task interactive. Runs on the runner cost $0.

## SES-002 Reject a session on a foreign repo, an unselectable model or without both permissions
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/interactive-sessions
since: 2026-09-15
```
**Preconditions**: SES-001 project `P` and repo `R`; a second project `Q` with repo `R2`; keys `K-create` (TasksCreate only) and `K-exec` (TasksExecute only).
**Steps**:
1. `POST /api/projects/{P}/interactive-sessions {repoId:R2}`.
2. `POST … {repoId:R, model:"not-a-model"}`.
3. With `K-create`, then with `K-exec`: `POST … {repoId:R}`.
4. `POST /api/projects/01JUNKPROJECTIDXXXXXXXXXXX/interactive-sessions {repoId:R}`.
**Expected**:
- Step 1 → 400 "The specified repository does not belong to this project.".
- Step 2 → 400 "'not-a-model' is not a selectable model.".
- Step 3 → 403 for both keys (the endpoint requires TasksCreate **and** TasksExecute).
- Step 4 → 404.
**Notes**: the session always opens on the caller's runner (`runnerUserId` = caller); a model whose provider is not signed in there is not rejected — the thread says the runner does not cover it (AIR-003).

## SES-003 Drive a turn from the task page and watch the session banner
```yaml
priority: P0
surface: [ui, api, hub]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-ui:packages/ui/src/components/task/InteractiveSessionBanner.tsx
  - orkka-api:POST /api/tasks/{taskId}/messages
  - orkka-api:GET /api/tasks/{taskId}/messages
  - orkka-api:GET /api/tasks/{taskId}/executions
  - orkka-api:GET /api/tasks/{id}
  - hub:TaskHub.StreamEvent
  - i18n:interactiveSession
  - i18n:conversationThread
since: 2026-09-15
```
**Preconditions**: runner online with providers ready; session opened via BRD-020 (browser lands on the task page).
**Steps**:
1. Read the banner immediately, then again after ~10 s.
2. Type `Add a list item 'e2e-ses-<token>' to the features list and commit it` into "Send a message..." and send.
3. Watch the Agent Activity feed and the conversation until the agent replies.
4. `GET /api/tasks/{id}`, `GET …/executions`.
**Expected**:
- Banner (green accent card): heading "Session live", line "Preparing the workspace…" until the worktree exists, then "Working on {branch}"; hint "Messages go straight to the agent. Nothing is pushed until you finish."; button "Finish & push".
- Step 2: `POST …/messages` → 200; the message shows in the thread; within seconds the agent streams (`StreamEvent` chunks) and replies; the file edit is visible later in the working-tree diff (TSK-057).
- Step 4: `branchName` set; exactly one execution per turn (a second message adds a second execution that resumes the first's session); no `InQueue` status at any point.
**Notes**: the agent is re-resolved every turn, so changing the agent chip mid-session takes effect on the next message (cold start). $0 on the runner.

## SES-004 Name the session from its first message, unless the user renamed it first
```yaml
priority: P2
surface: [api, ui]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/interactive-sessions
  - orkka-api:POST /api/tasks/{taskId}/messages
  - orkka-api:PATCH /api/tasks/{id}
  - orkka-api:GET /api/tasks/{id}
  - orkka-ui:/p/[projectId]/tasks/[taskId]
since: 2026-09-15
```
**Preconditions**: runner online; two fresh sessions `A` and `B` (SES-001).
**Steps**:
1. On `A` send `Rename the features heading to "What you get"`; poll `GET /api/tasks/{A}` while the turn runs.
2. On `B` first `PATCH /api/tasks/{B} {title:"My footer fix"}`, then send the same message.
**Expected**:
- `A.title` changes from "Interactive session" to a short title derived from the message before or during the first turn; the task page header and the board card update live.
- `B.title` stays "My footer fix" — the auto-title only overwrites the placeholder.
**Notes**: titling happens once; later messages never rename.

## SES-005 Approve a gated command once from the approval card
```yaml
priority: P1
surface: [ui, api, hub]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-ui:packages/ui/src/components/task/InteractiveApprovalPanel.tsx
  - orkka-api:GET /api/tasks/{taskId}/approvals
  - orkka-api:POST /api/tasks/{taskId}/approvals/{approvalId}
  - orkka-api:GET /api/projects/{projectId}/repos/{repoId}/branches
  - hub:TaskHub.StreamEvent
  - i18n:interactiveSession
since: 2026-09-15
```
**Preconditions**: live session (SES-003) with a committed change on its branch.
**Steps**:
1. Send `Push the branch to origin now`.
2. When the card appears, `GET /api/tasks/{id}/approvals`.
3. Click "Allow once".
4. `GET …/approvals` again; `GET /api/projects/{P}/repos/{R}/branches`.
**Expected**:
- Amber card: heading "Waiting for your approval", a `<pre>` with the command verbatim (`git push …`), hint "The agent is paused until you decide.", buttons "Allow once", "Always allow git push", "Deny". The agent stream is idle while the card is shown.
- Step 2 → 200 `[{id, taskId, toolUseId, tool:"Bash", command, approvalKey:"git push", createdAt}]`.
- Step 3: `POST …/approvals/{id} {decision:"allow"}` → 204; the card disappears at once; a `StreamEvent` of type `ApprovalSettled` with `metadata.approvalId` arrives; the agent continues and reports the push.
- Step 4: approvals → `[]`; the session branch is listed on the remote.
**Notes**: only actions whose effect outlives the worktree ask (`git push`, `gh`, publishing, `git reset --hard`, `git clean`); builds, tests and installs run unasked. Blocklisted programs (`psql`, `ssh`, `kubectl`, `docker`, …) are denied outright and never produce a card.

## SES-006 "Always allow" grants the key for the rest of the run; multi-part lines offer no grant
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-api:GET /api/tasks/{taskId}/approvals
  - orkka-api:POST /api/tasks/{taskId}/approvals/{approvalId}
  - i18n:interactiveSession
since: 2026-09-15
```
**Preconditions**: SES-005 state, a fresh `git push` prompt on screen.
**Steps**:
1. Click "Always allow git push".
2. Send `Amend the commit message and push again`; watch for a card; `GET …/approvals`.
3. Send `Run exactly this: git push && gh pr view`; read the card.
**Expected**:
- Step 1: `POST … {decision:"allow_session"}` → 204; card gone.
- Step 2: the second push runs with **no** card (`approvals` stays `[]`).
- Step 3: the card renders the whole line, `approvalKey` is empty and only "Allow once" and "Deny" are offered (no "Always allow" for a line with several ask-worthy segments).
**Notes**: grants live in the running worker's memory — a session re-picked up after a host restart asks again. `git -C x push` (verb after flags) still keys as `git push`; a subcommand-style program whose verb cannot be read is not keyable at all.

## SES-007 Deny a command and see the agent stop retrying it
```yaml
priority: P1
surface: [ui, api, hub]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-api:POST /api/tasks/{taskId}/approvals/{approvalId}
  - orkka-api:GET /api/tasks/{taskId}/approvals
  - orkka-api:GET /api/tasks/{taskId}/messages
  - hub:TaskHub.StreamEvent
  - i18n:interactiveSession
since: 2026-09-15
```
**Preconditions**: live session with a `git push` card on screen (SES-005 step 1).
**Steps**:
1. Click "Deny".
2. Read the agent's next message; wait for the turn to end; `GET …/approvals`.
3. Send `Run psql -c "select 1"`; watch for a card.
**Expected**:
- Step 1: `POST … {decision:"deny"}` → 204; card removed; `ApprovalSettled` event.
- Step 2: the agent says the human declined the push and continues or asks what to do instead; it does **not** raise a second prompt for the same command in that turn; approvals → `[]`.
- Step 3: no card at all — the agent reports the command is restricted and cannot be approved by anyone (blocklist wins before any prompt).
**Notes**: the deny text delivered to the agent is "The human declined this command. Do not retry it…"; the blocklist text is "This command is restricted and cannot be approved by anyone, including the human in this session…".

## SES-008 Let an approval expire and watch the agent ask again
```yaml
priority: P2
surface: [ui, api, hub]
automation: manual
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-api:GET /api/tasks/{taskId}/approvals
  - orkka-api:POST /api/tasks/{taskId}/approvals/{approvalId}
  - hub:TaskHub.StreamEvent
since: 2026-09-15
```
**Preconditions**: live session with a `git push` card on screen; note its `approvalId`.
**Steps**:
1. Leave the card unanswered for 10 minutes.
2. `GET …/approvals`; then `POST …/approvals/{approvalId} {decision:"allow"}`.
3. Read the agent's message; if it asks again, answer the new card.
**Expected**:
- After the timeout the card disappears on its own (`ApprovalSettled`) and approvals → `[]`; the late decision → 409.
- The agent is told the request **expired** (not that the command is restricted) and may ask again — a new card with a new `approvalId` appears when it retries; approving it runs the command.
**Notes**: manual because of the 10-minute wait. Pending prompts are also expired when the session is picked up again (host/worker restart) and when it finishes.

## SES-009 Approval endpoints: pending-only list, 409 on settled prompts, 400 and 403
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/tasks/{taskId}/approvals
  - orkka-api:POST /api/tasks/{taskId}/approvals/{approvalId}
  - orkka-api:src/Modules/Orkka.Modules.Tasks/Features/InteractiveSessions/DecideApprovalEndpoint.cs
since: 2026-09-15
```
**Preconditions**: task `T` (any) with no prompts; a settled approval id `S` from SES-005; key `K-noexec` without TasksExecute; owner key.
**Steps**:
1. `GET /api/tasks/{T}/approvals`.
2. `POST /api/tasks/{T}/approvals/{S} {decision:"maybe"}`.
3. `POST /api/tasks/{T}/approvals/{S} {decision:"allow"}` (already settled).
4. With `K-noexec`: `POST … {decision:"deny"}` on a pending prompt; `GET …/approvals`.
5. `POST /api/tasks/{T}/approvals/not-a-ulid {decision:"allow"}`.
**Expected**:
- Step 1 → 200 `[]` (only `Pending` rows are ever listed).
- Step 2 → 400 "Decision must be 'allow', 'allow_session', or 'deny'.".
- Step 3 → 409 (the update matched no `Pending` row).
- Step 4: POST → 403; GET → 200 (reads are open to members).
- Step 5 → 404 (route constraint).
**Notes**: decisions are case-insensitive (`ALLOW` works). A foreign-org task id → 409 on decide and `[]` on list, never a leak.

## SES-010 Finish & push: the session wraps up, opens a PR and lands in Code Review
```yaml
priority: P0
surface: [ui, api, hub]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-ui:packages/ui/src/components/task/InteractiveSessionBanner.tsx
  - orkka-api:POST /api/tasks/{taskId}/interactive-session/finish
  - orkka-api:GET /api/tasks/{id}
  - orkka-api:GET /api/projects/{projectId}/board
  - hub:TaskHub.LaneChanged
  - hub:TaskHub.StatusChanged
  - i18n:interactiveSession
since: 2026-09-15
```
**Preconditions**: live session whose turn edited the fixture (SES-003); no pending approval.
**Steps**:
1. Click "Finish & push".
2. Read the banner; `GET /api/tasks/{id}`.
3. Wait for the lane change (≤ 5 min); `GET /api/tasks/{id}`; open the board.
**Expected**:
- Step 1: `POST /api/tasks/{id}/interactive-session/finish` → 200 `{taskId, finishRequestedAt}`; the button immediately reads "Finishing…" and stays disabled.
- Step 2: banner heading "Wrapping up", hint "Committing and pushing, then this goes to code review."; `interactiveFinishRequestedAt` set, `interactiveSessionStartedAt` still set.
- Step 3: `LaneChanged` → `CodeReview` and `StatusChanged` → `WaitingCodeReview`; `interactiveSessionStartedAt:null`, PR URL present on the task; the banner is no longer rendered (the task is an ordinary task from here); the board shows the card in Code Review; the PR diff contains the `e2e-ses-<token>` list item.
**Notes**: the finish turn runs under the ordinary guard — pressing Finish *is* the approval to push, so no card appears. Review/merge continue in the PR area (PR-0xx). $0 on the runner.

## SES-011 Finish is idempotent and refuses tasks that are not live sessions
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/tasks/{taskId}/interactive-session/finish
  - orkka-api:src/Modules/Orkka.Modules.Tasks/Features/InteractiveSessions/FinishInteractiveSessionEndpoint.cs
since: 2026-09-15
```
**Preconditions**: live session `L`; a finished session `F` (SES-010); an ordinary task `T`; key `K-noexec` without TasksExecute.
**Steps**:
1. `POST /api/tasks/{L}/interactive-session/finish` twice within a second.
2. `POST /api/tasks/{T}/interactive-session/finish`.
3. `POST /api/tasks/{F}/interactive-session/finish`.
4. With `K-noexec`: `POST /api/tasks/{L}/interactive-session/finish`.
**Expected**:
- Step 1: both 200 with the **same** `finishRequestedAt` (the timestamp is written once).
- Step 2 → 404 (never was a session).
- Step 3 → 400 "This session has already ended.".
- Step 4 → 403.
**Notes**: abandoning a session is the ordinary reset/archive flow (TSK-035/036), not this endpoint.

## SES-012 A session that changed nothing closes as Done with a note
```yaml
priority: P2
surface: [ui, api, hub]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-api:POST /api/tasks/{taskId}/messages
  - orkka-api:POST /api/tasks/{taskId}/interactive-session/finish
  - orkka-api:GET /api/tasks/{id}
  - orkka-api:GET /api/projects/{projectId}/repos/{repoId}/branches
  - hub:TaskHub.LaneChanged
since: 2026-09-15
```
**Preconditions**: fresh session (SES-001).
**Steps**:
1. Send `Which file renders the features list? Just answer, change nothing.`; wait for the reply.
2. Click "Finish & push"; wait for the lane change.
3. `GET /api/tasks/{id}`; `GET …/branches`.
**Expected**:
- The task moves to `Done` / `Completed` with a system note saying nothing was pushed; no PR is created; no session branch exists on the remote.
- The board shows the card in Done; no reviewer is asked to look at an empty diff.
**Notes**: routing keys on whether the branch reached the remote, not on the agent's report.

## SES-013 Sessions bypass the queue and the parallel-task budget
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:PATCH /api/projects/{id}
  - orkka-api:POST /api/projects/{projectId}/interactive-sessions
  - orkka-api:POST /api/projects/{projectId}/tasks
  - orkka-api:POST /api/tasks/{id}/submit
  - orkka-api:GET /api/projects/{projectId}/board
since: 2026-09-15
```
**Preconditions**: project `P` with `settings.maxParallelTasks:1` (PATCH) and `allowParallelSameRepo:false`; runner online.
**Steps**:
1. Open a session on repo `R`.
2. `POST /api/projects/{P}/tasks {title:"e2e-ses-queue", repoId:R}`; `POST /api/tasks/{id}/submit`.
3. Poll `GET /api/projects/{P}/board` for 2 minutes.
**Expected**:
- The submitted task leaves `InQueue` and starts Planning while the session is live — the session is not counted as an active slot nor as a same-repo conflict.
- The session itself never appears with `status:"InQueue"`.
**Notes**: two branches can still conflict at merge time; that is the merge-resolution flow (PR-011/012).

## SES-014 Runner away: the session announces the wait once and closes after the grace period
```yaml
priority: P2
surface: [ui, api, cli]
automation: manual
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-api:GET /api/tasks/{taskId}/messages
  - orkka-api:GET /api/tasks/{id}
  - cli:connect
since: 2026-09-15
```
**Preconditions**: live session on the runner.
**Steps**:
1. Stop the runner (a runner run by `./orkka connect` — Ctrl+C; with `./orkka start` only the whole Orkka stops); send a message to the session.
2. Wait 2 minutes reading the thread.
3. Restart the runner within 15 minutes; wait for the turn.
4. Repeat 1–2, but leave the runner off for > 15 minutes.
**Expected**:
- Step 2: exactly **one** system message says the session is waiting for the runner to reconnect; subsequent 5-second sweeps add nothing.
- Step 3: the turn runs as soon as the runner is back; no cold start beyond the resumed CLI session.
- Step 4: the session is closed with a plain explanation in the thread; `interactiveSessionStartedAt:null`; the branch and its commits remain; the task can be reopened as an ordinary task.
**Notes**: manual because of the wait; `UnreachableBackendGrace` is 15 minutes.

## SES-015 Hide Finish and the approval buttons from a member without TasksExecute
```yaml
priority: P2
surface: [ui]
automation: manual
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-ui:packages/ui/src/components/task/InteractiveApprovalPanel.tsx
  - i18n:interactiveSession
since: 2026-09-15
```
**Preconditions**: live session with a card on screen; a second account that is a member without TasksExecute.
**Steps**:
1. Open the session's task page as the second account.
**Expected**:
- The banner text ("Session live", branch line, hint) is visible but there is no "Finish & push" button.
- The approval card shows the command and the hint but no "Allow once" / "Always allow" / "Deny" buttons.
- The board header for that account has no "Start session" (BRD-022).
**Notes**: hide-only in the UI; the API enforces 403 (SES-009, SES-011).
