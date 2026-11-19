---
area: HYB
title: Hybrid — bring your own repo, run on your machine
modes-default: [hybrid-repo]
---

## HYB-008 Create a project and add the fixture repo by URL
```yaml
priority: P0
surface: [api, ui]
automation: planned
refs:
  - orkka-api:POST /api/projects
  - orkka-api:POST /api/projects/{projectId}/repos
  - orkka-ui:/p/[projectId]/repos
  - i18n:addRepoByUrl
since: 2026-09-15
```
**Preconditions**: Runner online; the developer's own git can read the fixture remote (no GitHub App involved).
**Steps**:
1. `POST /api/projects {name:"e2e-hyb-<stamp>", key:"E2E"}`.
2. `POST /api/projects/{id}/repos {gitUrl:"<fixture remote>", name:"orkka-e2e-fixture-web", defaultBranch:"main"}`.
3. Open `/p/{slug}/repos`.
**Expected**:
- Step 1: 201 with `slug`.
- Step 2: 201; `GET …/repos` → one repo with `provider` detected from the host (`github` for github.com), `defaultBranch:"main"`.
- Step 3: the repository list shows the repo with its forge icon, an "Open remote" link and tabs "Code", "Commits", "Branches", "Pull Requests".
**Notes**: cleanup deletes the project. Locally the fixture is a bare repo reached through a `url.<base>.insteadOf` rule (see the harness README).

## HYB-009 Preflight repo access from the runner's own git credentials
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/repos/access-check
since: 2026-09-15
```
**Preconditions**: HYB-008 project; runner online with `repo-access-check`; this machine's git can read the fixture repo.
**Steps**:
1. `GET /api/projects/{p}/repos/access-check`.
**Expected**:
- 200 `{supported:true, repos:[{repoId, name:"orkka-e2e-fixture-web", ok:true, reason:null, advice:null}]}`.

## HYB-010 Report an unreadable repo from the access preflight with a reason and advice
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/repos/access-check
since: 2026-09-15
```
**Preconditions**: Project additionally links a repo this machine's git cannot read (the harness uses an alias whose local bare remote does not exist).
**Steps**:
1. `GET …/repos/access-check`.
**Expected**:
- That repo's entry has `ok:false` with a non-empty `reason` and `advice` (same classifier sentences a failed run would produce); the fixture repo stays `ok:true`.
**Notes**: Needs a repo owned by another account — manual.

## HYB-011 Return supported:false from the access preflight while the runner is offline
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/repos/access-check
since: 2026-09-15
```
**Preconditions**: Runner stopped.
**Steps**:
1. `GET …/repos/access-check`.
**Expected**:
- 200 `{supported:false, repos:[]}` (not a 5xx).

## HYB-012 Clone the fixture repo as a bare mirror on the runner on first use
```yaml
priority: P0
surface: [api, cli]
automation: planned
refs:
  - orkka-api:POST /api/tasks/{id}/submit
  - orkka-api:GET /api/tasks/{taskId}/executions
since: 2026-09-15
```
**Preconditions**: HYB-008 project; `~/.orkka/repos` (or `ORKKA_REPO_CACHE`) contains no directory for this repo yet.
**Steps**:
1. Create and submit a task (HYB-015).
2. Once the task leaves Ready, inspect `~/.orkka/repos/<orgId>/…` on this machine.
**Expected**:
- A bare mirror for `orkka-e2e-fixture-web` exists (`git --git-dir <dir> remote get-url origin` = the fixture URL); a `worktrees/main` checkout appears alongside it.
- `GET /api/tasks/{taskId}/executions` shows the run on this machine's runner (there is no other place a task can run).
**Notes**: The clone uses the user's own credentials; `gh auth token` fallback when the ambient credential fails.

## HYB-013 Chat with Luna from the Team page using the runner chat workspace
```yaml
priority: P1
surface: [ui, api, cli]
automation: planned
costUsd: 0
refs:
  - orkka-ui:/p/[projectId]/team
  - orkka-api:POST /api/projects/{projectId}/agents/{agentId}/conversations
  - orkka-api:POST /api/projects/{projectId}/agents/{agentId}/conversations/{conversationId}/messages
  - i18n:agentChatTab
since: 2026-09-15
```
**Preconditions**: Runner online with `chat-tools`; HYB-008 project.
**Steps**:
1. Open `/p/{slug}/team?agent=<pm agentId>&tab=chat`.
2. Click "New conversation"; type "List the files in the repository root and the text of the h1 on the homepage" into the composer (placeholder "Message Luna"); click "Send".
**Expected**:
- Reply streams and names real fixture files (e.g. `package.json`, `orkka.json`) and quotes "Orkka E2E Fixture".
- On disk `~/.orkka/repos/<orgId>/chat-workspace/<projectId>/` contains a worktree of the fixture repo (read-only workspace per repo).
- `GET /api/billing/usage/breakdown` adds no cost for this conversation.

## HYB-014 Enable auto-approve plan and code review for the pipeline project
```yaml
priority: P0
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/settings
  - orkka-api:PATCH /api/projects/{id}
  - i18n:pipelineSettings
since: 2026-09-15
```
**Preconditions**: HYB-008 project.
**Steps**:
1. Open `/p/{slug}/settings`; in the automation card turn on "Auto-approve plan" and "Auto-approve code review"; leave "QA lane" off.
**Expected**:
- Each toggle issues `PATCH /api/projects/{id}` with the full `settings` JSON; `GET /api/projects/{id}` returns `settings.autoApprovePlan:true`, `settings.autoApproveCodeReview:true`, `settings.qaEnabled:false`.

## HYB-015 Create a task pinned to the cheapest Claude model and submit it to the runner
```yaml
priority: P0
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/new
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-api:POST /api/projects/{projectId}/tasks
  - orkka-api:POST /api/tasks/{id}/submit
  - i18n:taskCreateForm
  - i18n:taskCard
since: 2026-09-15
```
**Preconditions**: HYB-014 settings; runner online with Claude ready.
**Steps**:
1. Open `/p/{slug}/tasks/new`; fill Title "Add feature item e2e-<token>", Description "Add a list item 'e2e-<token>' to the features list on the homepage (data-testid=features).", Repository = fixture repo, Model = cheapest Claude model (label annotated "runner"); leave "Skip planning" off.
2. Click "Create and submit".
**Expected**:
- `POST /api/projects/{p}/tasks` → 201 with `runnerUserId` = caller and no `executionTarget`/`executionTargetReason` fields (every task runs on its owner's runner); then `POST /api/tasks/{id}/submit` → 200.
- The model note reads "Runs on your runner via your Anthropic subscription · 0 credits"; there is no "Runs on" chip, execution badge or "Billed cloud run" text anywhere.

## HYB-016 Watch the task advance Drafting → Ready → Planning → Developing → CodeReview → Done
```yaml
priority: P0
surface: [ui, hub, api]
automation: planned
costUsd: 0
refs:
  - orkka-ui:/p/[projectId]/board
  - hub:TaskHub.LaneChanged
  - hub:TaskHub.StatusChanged
  - orkka-api:GET /api/projects/{projectId}/board
  - i18n:boardPage
since: 2026-09-15
```
**Preconditions**: HYB-015 task submitted with auto-approve plan + code review on.
**Steps**:
1. Keep `/p/{slug}/board` open; record every `LaneChanged` event for the task until it reaches Done (timeout 25 min).
**Expected**:
- Lane sequence is exactly Drafting, Ready, Planning, Developing, CodeReview, Done (no PlanReady, no Testing because `qaEnabled` is off); the card moves without page reload.
- Final `GET /api/tasks/{id}` → `status:"Completed"`, `lane:"Done"`, `runnerUserId` = caller.

## HYB-017 Record Orkka's own pull request for the task branch
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/tasks/{taskId}/pull-request
  - orkka-api:GET /api/repos/{repoId}/pulls
since: 2026-09-15
```
**Preconditions**: HYB-015 task in CodeReview or later.
**Steps**:
1. `GET /api/tasks/{taskId}/pull-request`.
2. `git ls-remote <remote> 'refs/heads/orkka/*'`.
**Expected**:
- Step 1: `{number, state:"open", headBranch}` with `headBranch` starting `orkka/`; `url` is `orkka-local://pull/<number>` — Orkka's own review record, nothing exists on the forge yet.
- Step 2: the task branch was pushed by the runner with the developer's credentials.

## HYB-018 Record zero-cost usage rows for a task that ran on the runner
```yaml
priority: P0
surface: [api, ui]
automation: planned
refs:
  - orkka-api:GET /api/tasks/{taskId}/executions
  - orkka-api:GET /api/usage/breakdown
  - orkka-api:GET /api/usage/summary
  - orkka-ui:/settings/usage
  - i18n:usagePage
since: 2026-09-15
```
**Preconditions**: HYB-016 task Done.
**Steps**:
1. `GET /api/tasks/{taskId}/executions`.
2. `GET /api/usage/breakdown?groupBy=task`; `GET /api/usage/summary`.
3. Open `/settings/usage`; group the breakdown by Project.
**Expected**:
- Every execution row has tokens > 0 and a recorded cost of 0 (runner work records `FlatCostBase: 0`); the breakdown row for the task has tokens and no charge — any dollar figure is a list-price estimate.
- The Usage page lists the project with "At list price — not a charge" (or "No list-price estimate — runs on your subscription").
**Notes**: there are no credits, balances or invoices (the cloud product was deleted 2026-11-17).

## HYB-019 Carry the task's change on the published branch
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/tasks/{taskId}/pull-request
  - orkka-api:GET /api/projects/{projectId}/repos/{repoId}/files/content
since: 2026-09-15
```
**Preconditions**: HYB-016 task Done (published).
**Steps**:
1. `GET /api/tasks/{taskId}/pull-request`.
2. `GET /api/projects/{p}/repos/{r}/files/content?path=<homepage file>&ref=<headBranch>`.
**Expected**:
- The record carries `forgeUrl` (the published forge PR); `main` is unchanged — Orkka never merges on a forge.
- The file on the head branch contains `e2e-<token>` inside the `data-testid="features"` list.
**Notes**: the suite closes the forge PR and deletes the branch afterwards.

## HYB-020 Show the Approve controls on the review page only while awaiting code review
```yaml
priority: P0
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]/review
  - orkka-api:PATCH /api/projects/{id}
  - i18n:prReview
since: 2026-09-15
```
**Preconditions**: Project settings `autoApprovePlan:true`, `autoApproveCodeReview:false`; a task submitted and now `WaitingCodeReview`.
**Steps**:
1. Open `/p/{slug}/tasks/{n}/review`.
**Expected**:
- PrHeader shows "Open", "N files changed", "Mergeable"; buttons "Approve" and "Request Changes" are visible (PrReviewActions renders them only for `taskStatus === "WaitingCodeReview"`).
- While the reviewer agent still works the page shows "The code reviewer agent is still reviewing…" and no buttons.

## HYB-021 Approve the pull request and publish it to the forge
```yaml
priority: P0
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]/review
  - orkka-api:POST /api/tasks/{taskId}/pull-request/reviews
  - i18n:forgePr
since: 2026-09-15
```
**Preconditions**: HYB-020 state; the runner has `gh` signed in (or the harness's stub gh).
**Steps**:
1. Click "Approve".
**Expected**:
- `POST /api/tasks/{taskId}/pull-request/reviews {decision:"approve"}` → 200; `CodeReviewed` fires.
- Within 5 min the runner runs `gh pr create --repo <owner/name> --head orkka/<KEY-n> --base main`; the task reaches Done with `publishedPrUrl` = the forge PR URL; the review page shows "View pull request on GitHub".

## HYB-022 Publish an approved pull request explicitly through the API
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/tasks/{taskId}/pull-request/merge
  - orkka-api:GET /api/tasks/{taskId}/pull-request
since: 2026-09-15
```
**Preconditions**: A task `WaitingCodeReview` on a GitHub remote; runner online with `forge-cli`.
**Steps**:
1. `POST /api/tasks/{taskId}/pull-request/merge`.
2. Repeat the call.
**Expected**:
- 200/204; `GET …/pull-request` → `forgeUrl` set; task `status:"Completed"`, lane Done.
- The repeat returns the same `forgeUrl` or a 4xx "already" — never a 500 and never a second forge PR (FRG-002).

## HYB-023 Refuse a PR review from an API key without CodeReviewReview
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/tasks/{taskId}/pull-request/reviews
  - orkka-api:POST /api/tasks/{taskId}/pull-request/merge
since: 2026-09-15
```
**Preconditions**: API key minted with `["TasksCreate"]` only; a task `WaitingCodeReview`.
**Steps**:
1. `POST …/pull-request/reviews {decision:"approve"}` with that key.
2. `POST …/pull-request/merge` with that key.
**Expected**:
- Both 403; PR still open; task still `WaitingCodeReview`.

## HYB-024 Hold the task in PlanReady until a human approves when autoApprovePlan is off
```yaml
priority: P0
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-api:GET /api/tasks/{taskId}/plans/latest
  - orkka-api:POST /api/tasks/{taskId}/plans/{planId}/review
  - i18n:planViewer
since: 2026-09-15
```
**Preconditions**: Settings `autoApprovePlan:false`; task submitted on the runner.
**Steps**:
1. Wait for lane PlanReady (status `WaitingPlanApproval`); open the task page.
2. In "Execution Plan" click "Show"; click "Approve Plan".
**Expected**:
- PlanViewer shows "Approach" and "Files (N)"; buttons "Approve Plan", "Request Changes", "Reject Plan".
- `POST /api/tasks/{taskId}/plans/{planId}/review {decision:"approve"}` → 200; lane moves to Developing; the board card's "Review plan" attention tag disappears.

## HYB-025 Request changes to a plan and receive a revised plan
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-api:POST /api/tasks/{taskId}/plans/{planId}/review
  - orkka-api:GET /api/tasks/{taskId}/plans
  - i18n:planViewer
since: 2026-09-15
```
**Preconditions**: Task `WaitingPlanApproval` on the runner.
**Steps**:
1. Click "Request Changes"; type "Also add a unit test for the new item" (placeholder "What should be changed?"); confirm.
**Expected**:
- `POST …/plans/{planId}/review {decision:"request_changes", feedback}` → 200; lane returns to Planning; `GET /api/tasks/{taskId}/plans` grows to 2 entries and the task returns to `WaitingPlanApproval` with the new plan mentioning a test.

## HYB-026 Reject a plan with a reason
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-api:POST /api/tasks/{taskId}/plans/{planId}/review
  - i18n:planViewer
since: 2026-09-15
```
**Preconditions**: Task `WaitingPlanApproval`.
**Steps**:
1. Click "Reject Plan"; type a reason; confirm (Cancel is available and closes the textarea).
**Expected**:
- `POST …/plans/{planId}/review {decision:"reject", feedback}` → 200; the task leaves `WaitingPlanApproval` and the plan's status is `Rejected` in `GET …/plans`; the attention tag "Review plan" disappears.

## HYB-027 Request changes on the pull request and get a follow-up commit
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]/review
  - orkka-api:POST /api/tasks/{taskId}/pull-request/reviews
  - orkka-api:GET /api/tasks/{taskId}/pull-request
  - i18n:prReview
since: 2026-09-15
```
**Preconditions**: Task `WaitingCodeReview` on the runner.
**Steps**:
1. Click "Request Changes"; type "Rename the item to 'e2e-<token>-v2'" (placeholder "Describe the changes you'd like to see..."); click "Submit Review".
**Expected**:
- `POST …/pull-request/reviews {decision:"request_changes", body}` → 200; lane goes back to Developing; a new commit lands on the `orkka/task-…` branch; the task returns to `WaitingCodeReview` and "Reviews" lists a "Changes Requested" entry.

## HYB-028 Answer the agent's pending questions from the task conversation
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-api:POST /api/tasks/{taskId}/messages
  - orkka-api:GET /api/tasks/{taskId}/messages
  - i18n:taskDetailLive
since: 2026-09-15
```
**Preconditions**: Task description ends with "Before planning, ask me which colour the item should be — do not assume."; submitted on the runner.
**Steps**:
1. Wait for lane PendingQuestions (status `WaitingAnswer`); open the task page.
2. Pick an option chip (or "Other" free text "green") and click "Submit".
**Expected**:
- Banner "Agent is waiting for your answer" with hint "Reply in the conversation below to resume the agent's work.".
- `POST /api/tasks/{taskId}/messages` → 201; the answer appears in the thread; the lane leaves PendingQuestions within 2 min.
- Sending a message with an API key lacking `TasksExecute` returns 403.

## HYB-029 Park a runner task as WaitingForRunner while the runner is offline
```yaml
priority: P0
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/tasks/{taskId}/submit
  - orkka-api:GET /api/tasks/{taskId}
  - orkka-ui:/p/[projectId]/tasks/[taskId]
since: 2026-09-15
```
**Preconditions**: A Drafting task; runner stopped (presence offline).
**Steps**:
1. Click "Submit to Queue".
**Expected**:
- Within one queue sweep `GET /api/tasks/{id}` → `status:"WaitingForRunner"`.
- Task page banner "Waiting for your runner" with hint "This task runs on your machine, but your runner is offline. It resumes automatically when the runner reconnects." — no "Run in cloud instead" button (there is no cloud fallback).
- Board card shows attention "Waiting for your runner" and the `stalled` filter chip counts it.

## HYB-030 Resume a parked task automatically when the runner reconnects
```yaml
priority: P0
surface: [cli, api]
automation: planned
refs:
  - cli:connect
  - orkka-api:GET /api/tasks/{id}
  - orkka-api:GET /api/me/runner
since: 2026-09-15
```
**Preconditions**: HYB-029 task `WaitingForRunner`.
**Steps**:
1. Start Orkka on this machine (`./orkka start`, which runs the runner).
**Expected**:
- `GET /api/me/runner` → `online:true` within 30 s; the task leaves `WaitingForRunner` (InQueue → Active) without user action and completes on this machine with zero cost.

## HYB-038 Reset a runner task and remove its worktree from this machine
```yaml
priority: P1
surface: [ui, api, cli]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-api:POST /api/tasks/{id}/reset
  - i18n:taskActions
  - i18n:taskDetailLive
since: 2026-09-15
```
**Preconditions**: A task in Developing or CodeReview that ran on this machine; its worktree exists under the bare mirror's `worktrees/` directory.
**Steps**:
1. Click "Reset", then "Wipe & Reset".
**Expected**:
- `POST /api/tasks/{id}/reset` → 200; the page shows "Resetting task" with hint "Stopping the agent, wiping the conversation and plans, and removing the workspace…"; status `Resetting` then `Draft` in lane Drafting.
- `GET /api/tasks/{taskId}/messages` and `/plans` are empty; the task's worktree directory is gone from disk; the `orkka/task-…` branch is removed from the bare mirror.

## HYB-039 Block, re-queue and retry a task from the task actions
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-api:POST /api/tasks/{id}/block
  - orkka-api:POST /api/tasks/{id}/unblock
  - orkka-api:POST /api/tasks/{id}/retry
  - i18n:taskActions
since: 2026-09-15
```
**Preconditions**: A task `InQueue` on the runner.
**Steps**:
1. Click "Block".
2. Click "Re-queue".
**Expected**:
- Step 1: `POST /api/tasks/{id}/block` → 200; status `Blocked`; card badge "Blocked"; "Re-queue" appears (there is no "Run in cloud instead" / "Run on my machine" swap any more).
- Step 2: `POST /api/tasks/{id}/retry` (or `/unblock`) → 200; status back to `InQueue` and the task proceeds on the runner.
- With an API key lacking `TasksEdit`, `POST …/block` → 403; lacking `TasksExecute`, `POST …/retry` → 403.

## HYB-040 List editors and open a task worktree only for tasks that ran on this machine
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-api:GET /api/tasks/{taskId}/editors
  - orkka-api:POST /api/tasks/{taskId}/open-in-editor
  - i18n:editor
since: 2026-09-15
```
**Preconditions**: Runner online with `editor`; VS Code installed here; task A in Developing on this machine; task B that ran on another member's machine.
**Steps**:
1. Open task A; `GET /api/tasks/{A}/editors`.
2. Click "Open in editor" (or "Open in Visual Studio Code" when exactly one editor is listed).
3. Open task B; `GET /api/tasks/{B}/editors`.
**Expected**:
- Step 1: `{supported:true, editors:[{key, displayName}]}`; button visible with hint "Edits you make land on the agent's branch."
- Step 2: `POST /api/tasks/{A}/open-in-editor {editor}` → `{openedWith}`; status text "Opened in Visual Studio Code"; the editor window opens on the task worktree.
- Step 3: `supported:false` (or empty list) and the button is absent on task B.

## HYB-041 Run the per-repo setup command in every new task workspace
```yaml
priority: P1
surface: [api, ui, cli]
automation: planned
refs:
  - orkka-api:PATCH /api/projects/{projectId}/repos/{repoId}
  - orkka-api:GET /api/tasks/{taskId}/executions
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - i18n:taskDetailLive
  - cli:commands
since: 2026-09-15
```
**Preconditions**: Runner with `command-trust`; `PATCH …/repos/{r} {setupCommand:"npm ci"}`, and the command vouched for — either `"setup": "npm ci"` in the fixture's orkka.json on `main`, or approved on this machine after a first refusal (RUN-030/RUN-031: `./orkka commands approve --yes <id>`).
**Steps**:
1. Submit a task on the runner; wait for Developing.
2. `GET /api/tasks/{taskId}/executions` and read the activity stream.
**Expected**:
- The workspace preparation step ran `npm ci` before the agent started (`node_modules` exists in the task worktree); the activity shows the setup output.
- Unvouched (neither declared nor approved): the runner refuses it, the thread gets the RUN-030 note, and the run carries on without the setup step.
- Variant: `setupCommand:"exit 1"` → task page shows "The workspace isn't ready" with hint "Fix the repository's setup command (Repositories → workspace commands), then re-run the task…".

## HYB-042 View code changes for a task: snapshot files and counts, diff read live from the runner
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-api:GET /api/tasks/{taskId}/code-changes
  - orkka-api:GET /api/tasks/{taskId}/code-changes/{snapshotId}
  - i18n:codeChanges
since: 2026-09-15
```
**Preconditions**: Runner with `working-tree-diff`; a task past Developing.
**Steps**:
1. Click "View code changes".
2. Click the latest snapshot.
**Expected**:
- Modal "Code changes" lists snapshots "{lane} — {when}" with "N files changed" (`GET …/code-changes`).
- Selecting one fetches `GET …/code-changes/{snapshotId}` — read live from the task owner's runner, never from Orkka's storage (snapshots keep paths, line counts and a hash only) — and renders the diff of the homepage file containing `e2e-<token>`; "No changes in this snapshot." only for empty snapshots.
- A finished snapshot answers `currentBranch:true, unavailable:null` and the modal says "Read live from the developer's machine: this is the branch as it is now, not a stored copy of that session."
**Notes**: the offline case is HYB-047.

## HYB-043 Resume a run after the runner disconnects mid-development
```yaml
priority: P1
surface: [cli, api]
automation: planned
refs:
  - cli:connect
  - orkka-api:GET /api/tasks/{id}
  - orkka-api:GET /api/tasks/{taskId}/executions
  - orkka-api:GET /api/tasks/{taskId}/pull-request
since: 2026-09-15
```
**Preconditions**: Runner with `agent-resume`; a task `Active` in Developing on this machine.
**Steps**:
1. Kill the `orkka.dll` process (the one `./orkka start` runs — host and runner).
2. After 60 s run `./orkka start` again.
**Expected**:
- While offline the task does not fail; it is parked (`WaitingForRunner`) or stays `Active` pending reconnect, never `Blocked`.
- After reconnect the run resumes; exactly one PR is opened for the task; the task reaches Done; `executions` shows the interrupted run and its resumption, not a duplicated full run.

## HYB-044 Stop the agent process on the runner when a running task is reset
```yaml
priority: P1
surface: [api, cli]
automation: planned
refs:
  - orkka-api:POST /api/tasks/{id}/reset
  - orkka-api:GET /api/tasks/{taskId}/executions
since: 2026-09-15
```
**Preconditions**: Runner advertises `task-cancel`; a task `Active` in Developing (a `claude` child process is visible in Task Manager).
**Steps**:
1. `POST /api/tasks/{id}/reset`.
**Expected**:
- Within 30 s the agent child process is gone; the execution row is `Cancelled` (not `Completed`); the task returns to Drafting.
- No further commits land on the task branch after the reset.

## HYB-047 Live diff unavailable when the runner is offline — never a stored copy
```yaml
priority: P1
surface: [api, ui, cli]
automation: planned
refs:
  - orkka-api:GET /api/tasks/{taskId}/code-changes
  - orkka-api:GET /api/tasks/{taskId}/code-changes/{snapshotId}
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - i18n:codeChanges
  - cli:connect
since: 2026-11-17
```
**Preconditions**: A finished task with at least one end-of-phase snapshot (`isLive:false`, `fileCount > 0`); the harness's own sandboxed runner online.
**Steps**:
1. `GET /api/tasks/{id}/code-changes/{snapshotId}` with the runner online.
2. Stop the runner (the harness kills only the one it started); poll the same GET.
3. Open the task's "View code changes" modal and pick that snapshot.
4. Restart the runner.
**Expected**:
- Step 1: 200 `{currentBranch:true, unavailable:null}` and a non-empty `diff`.
- Step 2: within a few minutes 200 with `diff:""` and `unavailable` = "The diff is read from the developer's machine and Orkka keeps no copy of it. It could not be read right now…"; `GET …/code-changes` still lists the snapshot with the same `fileCount`/`additions`/`deletions`.
- Step 3: the modal shows "This diff is read from the developer's machine — Orkka keeps no copy of your code — and it can't be read right now (the runner is offline, or the branch is gone). The files and line counts above are still accurate."; the Code View toolbar reads "diff not readable right now — the runner is offline or the branch is gone".
- Step 4: the diff reads again.
**Notes**: no spec yet (the cloud-era `tests/hybrid-repo/runner-security.spec.ts` was never carried into this harness); the workflow spec's Story step reads a live diff with the runner online only. A deleted task branch gives the same `unavailable` answer.

## HYB-046 Publish to the forge without a human when autoApproveCodeReview is on
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/tasks/{taskId}
  - orkka-api:PATCH /api/projects/{id}
costUsd: 0
since: 2026-09-15
```
**Preconditions**: HYB-014 settings (both auto-approves on); a task submitted on the runner; `gh` signed in (or the stub).
**Steps**:
1. Poll `GET /api/tasks/{id}` until Done (timeout 25 min) without touching the review page.
**Expected**:
- The task never sits in `WaitingCodeReview`; after the reviewer agent approves, the merge sweep publishes the forge PR; `status:"Completed"`, `publishedPrUrl` set.
