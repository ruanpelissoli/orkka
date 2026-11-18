---
area: PR
title: Pull requests — review page, approve/merge, conflicts
modes-default: [hybrid-repo]
---

Fixture: project `e2e-pr-<stamp>` with fixture-web linked, runner online ($0). Settings `autoApprovePlan:true`, `autoApproveCodeReview:false`, `qaEnabled:false`. "Reach Code Review" = submit the standard task and wait until `currentLane:"CodeReview"`, `status:"WaitingCodeReview"` with `prUrl` set (the reviewer agent Sentinel approves, the human confirms).

## PR-001 Render the review page for a task waiting on code review
```yaml
priority: P0
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]/review
  - orkka-api:GET /api/tasks/{taskId}/pull-request
  - orkka-ui:packages/ui/src/components/pr/PrReviewPanel.tsx
  - i18n:prReview
```
**Preconditions**: task reached Code Review.
**Steps**:
1. Open `/p/{slug}/tasks/{n}/review`.
2. Click a file in "Files (N)".
**Expected**:
- Breadcrumb "Board / {title} / Review PR"; PrHeader shows the PR title "[Orkka] {task title}" with "#{number}", state badge "Open", `head → base` branch chips, "+adds −dels", "N files changed", "Mergeable" badge, short SHA and "Sync from GitHub".
- File list rows with A/M/D letters; clicking scrolls the diff to that file; hunks render with line numbers.
- `GET /api/tasks/{id}/pull-request` → `state:"open"`, `files[]`, `reviews[]`, `comments[]`, `mergeable`.
**Notes**: EvidencePanel sits above the PR panel (PR-016).

## PR-002 Show the "Ready for Review" banner on the task page only while waiting on a human
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - i18n:taskDetailLive
```
**Preconditions**: task in Code Review — once while Sentinel is `Active`, once at `WaitingCodeReview`.
**Steps**:
1. Open the task page in both states; click "Review PR".
**Expected**:
- While the agent reviews: no banner.
- At `WaitingCodeReview`: card "Pull Request Ready for Review" with the PR URL and a "Review PR" button linking to `…/review`.
**Notes**: the header chips also expose "Review PR".

## PR-003 Add an inline comment on a diff line
```yaml
priority: P1
surface: [ui, api, hub]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]/review
  - orkka-ui:packages/ui/src/components/pr/InlineCommentForm.tsx
  - orkka-api:POST /api/tasks/{taskId}/pull-request/comments
  - i18n:prReview
```
**Preconditions**: review page open (PR-001).
**Steps**:
1. Hover a changed line, click "Add comment"; type "nit: naming" in "Write a comment..."; press Ctrl+Enter.
2. Open another form and click "Cancel".
3. `POST /api/tasks/{id}/pull-request/comments {path, line:0, body:"x"}` and `{path:"", line:1, body:"x"}`.
**Expected**:
- The comment posts (`POST …/pull-request/comments {path, line, body}` → 200 with the comment) and appears in the thread under the line with author and time; the form closes; "Comment" button is disabled while the textarea is empty.
- Cancel closes the form without a request.
- Step 3 → 400 "Line number must be greater than 0." / "File path is required."
**Notes**: CodeReviewReview; comments also land on the GitHub PR.

## PR-004 Read the review history
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]/review
  - orkka-ui:packages/ui/src/components/pr/PrReviewHistory.tsx
  - orkka-api:GET /api/tasks/{taskId}/pull-request
  - i18n:prReview
```
**Preconditions**: task reached Code Review (Sentinel left a review).
**Steps**:
1. Read the "Reviews" section.
**Expected**:
- One row per review with author login, badge "Approved" / "Changes Requested" / "Commented" / "Dismissed", relative time and body.
- After PR-006 or PR-007 a second row appears for the human decision.
**Notes**: the section is absent when `reviews` is empty.

## PR-005 Hide the review actions unless the PR is open and the task waits on a human
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]/review
  - orkka-ui:packages/ui/src/components/pr/PrReviewActions.tsx
  - i18n:prReview
```
**Preconditions**: task in Code Review with Sentinel `Active`; later at `WaitingCodeReview`; a merged task.
**Steps**:
1. Open the review page in each state.
**Expected**:
- Active: text "The code reviewer agent is still reviewing. If a human decision is needed, the approve controls will appear here."; no buttons.
- WaitingCodeReview: "Approve" and "Request Changes" appear (the switch happens live on `StatusChanged` without reload).
- Merged PR: no actions and no merge button; state badge "Merged".
**Notes**: wrapped in `RequirePermission CodeReviewReview`.

## PR-006 Approve the pull request and land it: publish to the forge or squash into the folder
```yaml
priority: P0
surface: [ui, api]
automation: automated
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]/review
  - orkka-api:POST /api/tasks/{taskId}/pull-request/reviews
  - orkka-api:POST /api/tasks/{taskId}/pull-request/merge
  - i18n:forgePr
since: 2026-09-15
```
**Preconditions**: Task at `WaitingCodeReview`; one on a remote repo (GitHub), one on a linked local folder.
**Steps**:
1. Remote: click "Approve", then "Open pull request" and "Confirm: open pull request".
2. Folder: click "Approve", then "Merge Pull Request" and "Confirm Merge".
**Expected**:
- Remote: `POST …/pull-request/merge` publishes via the runner's forge CLI; task Done with `publishedPrUrl`; the button area shows "Published" and "View pull request on GitHub"; the hint reads "Your runner opens the pull request on the forge with your own CLI login. Orkka never merges there."
- Folder: the change is squash-merged into the folder's `main` (FRG/HYF-018); task Done, `isMerged:true`.

## PR-007 Request changes and send the task back to development
```yaml
priority: P0
surface: [ui, api, hub]
automation: automated
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]/review
  - orkka-api:POST /api/tasks/{taskId}/pull-request/reviews
  - hub:TaskHub.CodeReviewed
  - hub:TaskHub.LaneChanged
  - i18n:prReview
```
**Preconditions**: task at `WaitingCodeReview`.
**Steps**:
1. Click "Request Changes"; the textarea "Describe the changes you'd like to see..." autofocuses; click "Cancel".
2. Click "Request Changes" again, type "Use a data-testid on the new item", click "Submit Review".
3. `POST …/pull-request/reviews {decision:"request_changes"}` (no body).
**Expected**:
- Cancel returns to the Approve/Request pair.
- Submit → `{decision:"request_changes", body}` 204; `LaneChanged` to Developing/InQueue; the review page then shows "Review isn't open right now" (PR-015).
- Step 3 → 400 "Body is required when requesting changes."; `{decision:"nope"}` → 400 "Decision must be 'approve', 'request_changes', or 'comment'."
**Notes**: errors render in a `role="alert"` under the buttons.

## PR-008 Refresh the review page live on CodeReviewed and lane changes
```yaml
priority: P2
surface: [ui, hub]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]/review
  - hub:TaskHub.CodeReviewed
  - hub:TaskHub.LaneChanged
  - hub:TaskHub.StatusChanged
```
**Preconditions**: review page open in browser A; API key in B.
**Steps**:
1. From B `POST …/pull-request/reviews {decision:"comment", body:"fyi"}`.
2. From B `POST …/pull-request/reviews {decision:"request_changes", body:"redo"}`.
**Expected**:
- A's review history gains "Commented" without reload.
- After step 2 A navigates to `/p/{slug}/tasks/{n}` because the lane left Code Review.
**Notes**: `StatusChanged` toggles the action buttons in place.

## PR-009 Merge endpoint outcomes: published, landed, resolution started, refused
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/tasks/{taskId}/pull-request/merge
since: 2026-09-15
```
**Preconditions**: Task A on a remote at WaitingCodeReview; task A2 on a local folder, mergeable; task B with a conflicting PR on a folder (PR-012 setup); task C in Drafting.
**Steps**:
1. `POST /api/tasks/{A}/pull-request/merge`; `…/{A2}/…`; `…/{B}/…`; `…/{C}/…`; a random id.
**Expected**:
- A → 2xx, the forge PR is published (`forgeUrl`).
- A2 → 2xx, squash-merged into the folder.
- B → 202 `{merged:false, resolutionStarted:true, message}`.
- C → 400 with the refusal reason; random id → 404.
**Notes**: CodeReviewMerge.

## PR-010 Sync a published pull request's state from the developer's forge CLI
```yaml
priority: P2
surface: [api, ui]
automation: planned
refs:
  - orkka-api:POST /api/tasks/{taskId}/pull-request/sync
since: 2026-09-15
```
**Preconditions**: A task whose PR was published; the forge PR then merged (or closed) by a human; runner online with the forge CLI signed in.
**Steps**:
1. `POST /api/tasks/{id}/pull-request/sync`.
**Expected**:
- 200 with the state read through the runner's CLI (`gh pr view … --json state`); merged → the record's `forgeState:"merged"`; closed → `"closed"`; open → "nothing to reconcile".
**Notes**: CodeReviewMerge. Orkka never calls the forge API itself.

## PR-011 Read the merge-conflicts status and the "Nothing to fix" page
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]/merge
  - orkka-api:GET /api/tasks/{taskId}/merge-conflicts
  - i18n:mergePage
```
**Preconditions**: task in Developing without a merge problem; a merged task.
**Steps**:
1. Open `/p/{slug}/tasks/{n}/merge` for each; `GET /api/tasks/{id}/merge-conflicts`.
**Expected**:
- Developing task: `status:"none"`; page "Nothing to fix here" / "This task has no merge problems." with "Back to Board".
- Merged task: `status:"resolved"`; page "All merged!".
- Response shape: `status ∈ none|awaitingChoice|resolving|blocked|exhausted|resolved`, `summary`, `files[]`, `failureReason`, `pendingStrategy`, `startedAt`, `queuedStrategy`, `queuedGuidance`.
**Notes**: the page polls every 5 s except in `none|resolved|exhausted`.

## PR-012 Resolve a merge conflict from the Merge Resolution Center
```yaml
priority: P1
surface: [ui, api, hub]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]/merge
  - orkka-ui:packages/ui/src/components/merge/MergeResolutionCenter.tsx
  - orkka-api:GET /api/tasks/{taskId}/merge-conflicts
  - orkka-api:POST /api/tasks/{taskId}/resolve-merge
  - orkka-api:DELETE /api/tasks/{taskId}/resolve-merge/queued
  - hub:TaskHub.StreamEvent
  - i18n:mergePage
```
**Preconditions**: task whose PR conflicts with `main` (push a commit editing the same list line to `main` after the agent branched); the automatic resolver exhausted or paused so `status:"awaitingChoice"`.
**Steps**:
1. Open the merge page; read the summary; expand "Technical details".
2. Type guidance into "Optional: anything the AI should know…"; click "Combine changes".
3. While resolving, open "Not what you wanted?", type guidance, click "Queue this guidance"; then "Cancel the follow-up".
4. Wait for `resolved`.
**Expected**:
- Chooser: "Two updates changed the same things" with cards "Combine both" (Recommended), "Use this task's version" and "Keep the app as it is" (each with "Choose" → "Yes, do it" / "Cancel"), and "Advanced: decide file by file".
- Step 2 → `POST …/resolve-merge {strategy:"combine", guidance}` 200; page shows "Combining the changes...", strategy line "Combining both versions with AI", "Running for Ns" ticking from `startedAt`, live progress lines from `StreamEvent`, "Safe to close this page…".
- Step 3 → a second `resolve-merge` queues (`queuedStrategy`), label "Queued next: …"; `DELETE …/resolve-merge/queued` clears it.
- Step 4 → "All merged!"; task Done.
**Notes**: `strategy:"task"` / `"current"` are the deterministic side picks; `"perFile"` needs `files[]`.

## PR-013 Show the blocked state when the merge cannot run on the runner
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]/merge
  - orkka-api:GET /api/tasks/{taskId}/merge-conflicts
  - i18n:mergePage
```
**Preconditions**: hybrid merge that failed for an environmental reason (dirty working tree in the user's checkout on the runner) — `status:"blocked"` with `failureReason`.
**Steps**:
1. Open the merge page.
2. Clean the tree; wait for the auto-retry.
**Expected**:
- "One quick fix before merging" with "Orkka can't add this task's changes to your app just yet…", the failure reason box, "Orkka keeps retrying automatically…" and "Back to task"; no strategy chooser.
- After the fix the page moves on by itself (polling).

## PR-014 Flag a failed merge on the board and the task page
```yaml
priority: P1
surface: [ui, api, hub]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - hub:TaskHub.TaskMergeFailed
  - orkka-api:GET /api/tasks/{id}
  - i18n:taskDetailEditable
  - i18n:boardFilters
```
**Preconditions**: board and task page open; a merge attempt fails (PR-012 setup before resolution, or PR-013).
**Steps**:
1. Observe both pages when `TaskMergeFailed` arrives; `GET /api/tasks/{id}`.
2. Click "Fix this" on the task header.
**Expected**:
- Board card gains "Merge failed" and the "Merge failed" chip counts 1; task `mergeFailureReason` non-null.
- Task header shows "Merge failed" with the hint "The platform keeps retrying automatically…" and a "Fix this →" link to `…/merge`.
- A later `TaskMergeFailed` with `reason:null` clears both.

## PR-015 Take over the review with "Review it myself now" after the agent sent it back
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]/review
  - orkka-ui:apps/local/app/[locale]/(dashboard)/p/[projectId]/tasks/[taskId]/review/RequestHumanReviewButton.tsx
  - orkka-api:PATCH /api/tasks/{id}/lane
  - i18n:reviewPage
```
**Preconditions**: task with a PR back in Developing after PR-007, blocked so no agent is active.
**Steps**:
1. Open the review page.
2. Click "Review it myself now".
3. Repeat on a task whose status is `Active`.
**Expected**:
- Page "Review isn't open right now" / "The last review asked for changes…" with the button and hint "Opens the current work for your approval…".
- Click → `PATCH /api/tasks/{id}/lane {targetLane:"CodeReview"}` 200; the page refreshes into the full review panel.
- Active task: text "Your team is working on this right now — you can take over the review once the current step finishes." and no button.
- A task without `prUrl` shows "Nothing to review yet".
**Notes**: TasksExecute.

## PR-016 Read the evidence panel above the diff
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]/review
  - orkka-ui:packages/ui/src/components/task/EvidencePanel.tsx
  - orkka-api:GET /api/tasks/{taskId}/evidence
  - i18n:evidence
```
**Preconditions**: task at WaitingCodeReview (with plan and Sentinel review).
**Steps**:
1. Read the "Evidence" card; `GET /api/tasks/{id}/evidence`.
**Expected**:
- Subtitle "What was promised, delivered and verified"; "QA did not run for this task." (qa off); "Code review: Approved by Sentinel" (or "Changes requested"); "Promised vs delivered" checklist with Delivered / Not touched / Extra rows; totals Tokens, Cost, Files touched, "N review · N QA".
- API returns `qaVerdict`, `codeReview`, `plan`, `fileChecklist[]`, `attachments[]`, `totals`.
- A task with no plan shows "This task ran without a plan, so there is nothing to compare against."
**Notes**: —

## PR-017 Drive repository pull requests through the repo-scoped endpoints
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/repos/{repoId}/pulls
  - orkka-api:GET /api/repos/{repoId}/pulls/{number}
  - orkka-api:POST /api/repos/{repoId}/pulls/{number}/comments
  - orkka-api:POST /api/repos/{repoId}/pulls/{number}/reviews
  - orkka-api:POST /api/repos/{repoId}/pulls/{number}/merge
  - orkka-api:POST /api/repos/{repoId}/pulls/{number}/close
```
**Preconditions**: fixture repo with one Orkka PR open (PR-001) and one hand-made PR.
**Steps**:
1. `GET /api/repos/{r}/pulls?state=open`; `GET …/pulls/{n}`.
2. `POST …/pulls/{n}/comments {path, line, body}`; `POST …/pulls/{n}/reviews {decision:"comment", body}`.
3. `POST …/pulls/{hand-made}/close`; `POST …/pulls/{orkka}/merge`.
**Expected**:
- 1 → 200 lists with `number, title, state, headBranch`; detail includes `files`, `reviews`, `comments`.
- 2 → 200/204 and the items appear on GitHub.
- 3 → close 204 (state closed on re-read); merge 204 and the linked task completes.
**Notes**: comments/reviews need CodeReviewReview; close/merge need CodeReviewMerge.

## PR-018 Return 403 for review and merge writes without permission
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/tasks/{taskId}/pull-request/reviews
  - orkka-api:POST /api/tasks/{taskId}/pull-request/comments
  - orkka-api:POST /api/tasks/{taskId}/pull-request/merge
  - orkka-api:POST /api/tasks/{taskId}/pull-request/sync
  - orkka-api:POST /api/tasks/{taskId}/resolve-merge
```
**Preconditions**: task at WaitingCodeReview; key K1 without CodeReviewReview; key K2 without CodeReviewMerge.
**Steps**:
1. K1: `POST …/pull-request/reviews {decision:"approve"}`; `POST …/pull-request/comments {…}`.
2. K2: `POST …/pull-request/merge`; `POST …/pull-request/sync`; `POST …/resolve-merge {strategy:"task"}`.
3. Both: `GET …/pull-request`; `GET …/merge-conflicts`.
**Expected**:
- Steps 1–2 → 403 each; the task state is unchanged.
- Step 3 → 200.
**Notes**: the UI hides the same controls via `RequirePermission`.

## PR-019 Create the pull request record by hand when the automatic one is missing
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/tasks/{id}/create-pr
  - orkka-ui:/p/[projectId]/tasks/[taskId]/review
since: 2026-09-15
```
**Preconditions**: Task in Code Review with a pushed branch but no PR record.
**Steps**:
1. Open the review page; click "Check again"; click "Create pull request".
2. `POST /api/tasks/{id}/create-pr` on a task that already has a PR; on a Drafting task.
**Expected**:
- Create → 200; an Orkka PR record (`orkka-local://pull/N`) titled exactly the task title (no prefix); the panel reloads into the full review.
- Step 2 → 400 "This task already has a pull request."; 400 for a task with no branch.
**Notes**: TasksExecute.

## PR-020 Validate resolve-merge requests and the per-file strategy
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]/merge
  - orkka-api:POST /api/tasks/{taskId}/resolve-merge
  - i18n:mergePage
```
**Preconditions**: task in `awaitingChoice` with two conflicting files (PR-012 setup); a merged task.
**Steps**:
1. Open "Advanced: decide file by file"; set one file to "This task", one to "Current app"; click "Apply my choices".
2. `POST …/resolve-merge {strategy:"perFile"}` (no files); `{strategy:"teleport"}`; `{strategy:"combine", guidance:"<2001 chars>"}`.
3. `POST …/resolve-merge {strategy:"task"}` on the merged task.
**Expected**:
- 1 → `{strategy:"perFile", files:[{path, choice:"task"},{path, choice:"current"}]}` 200; resolving line "Applying your file-by-file choices".
- 2 → 400 "Per-file resolution requires file choices."; 400 (invalid strategy); 400 "Guidance is limited to 2000 characters."
- 3 → 400 "This task is already merged."
**Notes**: submitting while another attempt runs queues it instead of failing (PR-012 step 3).
