---
area: TWB
title: Tracker status write-back — Started, PR opened, Merged
modes-default: [tracker]
---

## TWB-001 Started is written when the task starts developing
```yaml
priority: P0
surface: [api]
automation: automated
refs:
  - orkka-api:GET /api/tasks/{taskId}/messages
since: 2026-10-26
```
**Preconditions**: TING-001 task; status map Started → "In Progress"; fake Jira workflow To Do → In Progress (transition id 21).
**Steps**:
1. Let the task reach Planning/Developing; wait ≤ 2 sync intervals.
**Expected**:
- The fake logged `GET /rest/api/3/issue/SHOP-101?fields=status`, `GET …/transitions`, `POST …/transitions {transition:{id:"21"}}`; SHOP-101 is In Progress.
- Thread message "Moved SHOP-101 to “In Progress” in Jira."

## TWB-002 PR opened is written after publish
```yaml
priority: P0
surface: [api]
automation: automated
refs:
  - orkka-api:GET /api/tasks/{taskId}/messages
  - orkka-api:POST /api/tasks/{taskId}/pull-request/merge
since: 2026-10-26
```
**Preconditions**: TWB-001 state; status map PR opened → "In Review"; the task published via the stub gh (FRG-001).
**Steps**:
1. Wait ≤ 2 sync intervals after publishing.
**Expected**:
- SHOP-101 is In Review in the fake tracker; "Moved SHOP-101 to “In Review” in Jira."

## TWB-003 Merged is written when the forge PR is merged per the developer's CLI
```yaml
priority: P0
surface: [api]
automation: automated
refs:
  - orkka-api:GET /api/tasks/{taskId}/messages
since: 2026-10-26
```
**Preconditions**: TWB-002 state; status map Merged → "Done"; then the stub gh's state for that PR set to MERGED.
**Steps**:
1. Wait for the next PR check (≤ 10 min, or shorten in the test environment) and two sync intervals.
**Expected**:
- The runner ran `gh pr view <url> --json state --jq .state` → MERGED; SHOP-101 moves to Done; "Moved SHOP-101 to “Done” in Jira."

## TWB-004 An unmapped step is skipped, not a reason to stop
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/tasks/{taskId}/messages
since: 2026-10-26
```
**Preconditions**: Status map with only Merged mapped; a tracker task.
**Steps**:
1. Run it through start and publish; merge the forge PR.
**Expected**:
- No transition at start or publish; the ticket moves straight to the Merged status when the PR merges.

## TWB-005 Moves never go backwards
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/tasks/{taskId}/messages
since: 2026-10-26
```
**Preconditions**: A task whose ticket was moved to In Review (PR opened); the task is then sent back to Developing (request changes).
**Steps**:
1. Wait two sync intervals.
**Expected**:
- No transition back to In Progress is requested.

## TWB-006 A ticket already in the target status counts as success
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/tasks/{taskId}/messages
since: 2026-10-26
```
**Preconditions**: Move SHOP-101 to In Progress by hand in the fake tracker before the task starts.
**Steps**:
1. Let the task start.
**Expected**:
- The runner reads the status, sends no transition POST, acks success; the move is recorded as synced.

## TWB-007 No transition into the mapped status: failure with the available ones
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/tasks/{taskId}/messages
since: 2026-10-26
```
**Preconditions**: Status map Started → "Blocked"; the fake workflow has no transition to Blocked.
**Steps**:
1. Let the task start; read the runner's ack.
**Expected**:
- The ack carries "No transition to 'Blocked' from 'To Do' in this workflow — available: In Progress, …"; it is retried in the next window.

## TWB-008 Five failures give up with a card warning and a thread note
```yaml
priority: P0
surface: [api, ui]
automation: planned
refs:
  - orkka-api:GET /api/tasks/{taskId}/messages
  - orkka-api:GET /api/tasks/{taskId}
  - i18n:externalTicket
since: 2026-10-26
```
**Preconditions**: TWB-007 setup (the move always fails).
**Steps**:
1. Wait for five attempts (2-minute pending window each).
**Expected**:
- After the 5th: `externalSyncError` = "Couldn't move SHOP-101 to “Blocked” in Jira" on the task; thread note "… after 5 tries: <error>. Move it by hand, or fix the status map in the project's ticket source settings."; no further attempts.
- Card and task page show the warning with the "ticket source settings" link.

## TWB-009 A move is handed out once per two-minute window
```yaml
priority: P2
surface: [hub]
automation: planned
refs:
  - orkka-api:GET /api/tasks/{taskId}/messages
since: 2026-10-26
```
**Preconditions**: A pending move whose runner never acks (fake tracker hangs > 30 s).
**Steps**:
1. Watch `SyncTrackers` answers for 3 minutes.
**Expected**:
- The same transition id is handed out again only after the 2-minute window; the runner's own 30-second timeout reports "The tracker did not answer within 30 seconds."

## TWB-010 Azure DevOps: the state is set with a JSON Patch
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/tasks/{taskId}/messages
since: 2026-10-26
```
**Preconditions**: An ADO tracker task; map Started → "Active".
**Steps**:
1. Let the task start.
**Expected**:
- The fake logged `GET /_apis/wit/workitems/42?fields=System.State&api-version=7.1` then `PATCH /_apis/wit/workitems/42?api-version=7.1` with `application/json-patch+json` body `[{op:"add", path:"/fields/System.State", value:"Active"}]`; "Moved 42 to “Active” in Azure DevOps."

## TWB-011 Merged on a local folder needs no forge check
```yaml
priority: P2
modes: [tracker, hybrid-folder]
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/tasks/{taskId}/messages
since: 2026-10-26
```
**Preconditions**: A tracker task on a linked folder; Merged mapped.
**Steps**:
1. Approve and merge (squash into the folder).
**Expected**:
- The ticket moves to the Merged status without any forge CLI call.

## TWB-012 PR state checks only when Merged is mapped, at most every 10 minutes
```yaml
priority: P2
surface: [hub]
automation: planned
refs:
  - orkka-api:GET /api/tasks/{taskId}/messages
since: 2026-10-26
```
**Preconditions**: A published tracker task; first with Merged unmapped, then mapped.
**Steps**:
1. Watch the stub gh call log for 25 minutes in each configuration.
**Expected**:
- Unmapped: no `gh pr view` calls. Mapped: one per PR at most every 10 minutes; none after the PR is merged or closed.

## TWB-013 A write-back failure caused by the token is scrubbed
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/tasks/{taskId}/messages
since: 2026-10-26
```
**Preconditions**: The fake tracker answers the transition POST with 403 and an error body that echoes the token.
**Steps**:
1. Let the task start; read the ack/thread.
**Expected**:
- "Your Jira account cannot move this ticket (SHOP-101). (…***…)" — the token is masked.

## TWB-014 GitHub: a move sets the Status option; an issue off the project is added first
```yaml
priority: P0
surface: [api]
automation: planned
refs:
  - orkka-api:src/Orkka.LocalRunner/Trackers/GitHubProjectsClient.cs
  - orkka-api:GET /api/tasks/{taskId}/messages
since: 2026-11-19
```
**Preconditions**: A GitHub source on "Shop roadmap" with Developing = "In Progress"; a task for `e2e-local/app#1` (Todo); a Story member for `e2e-local/app#41`, which has no item on the project.
**Steps**:
1. Approve the plan so development starts; wait for the next sync.
2. Let the member start developing.
3. Rename the option "In Progress" to "Doing" in the fake; let another mapped step come due.
**Expected**:
- 1: the fake's `statusChanges` gain `{key:"e2e-local/app#1", from:"Todo", to:"In Progress"}` (`updateProjectV2ItemFieldValue` with the Status field and the option's id); "Moved e2e-local/app#1 to “In Progress”"; an issue already there is not written again.
- 2: `addProjectV2ItemById` for `#41` (recorded in `addedItems`), then its status set.
- 3: the ack fails with "The project's Status field has no option 'In Progress' — available: Todo, Doing, In Review, Done."; five failures give up with a note, as for Jira.

## TWB-015 A GitHub issue's pull request says "Closes owner/repo#n"
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:src/Modules/Orkka.Modules.SourceControl/Infrastructure/Services/OrkkaPullRequestService.cs
  - orkka-api:src/Modules/Orkka.Modules.SourceControl/Infrastructure/Services/StoryLandingService.cs
since: 2026-11-19
```
**Preconditions**: A task from `e2e-local/app#1` worked in the repository `https://github.com/e2e-local/app` (a bare remote behind the alias); a Story `e2e-local/app#40` with member `#41`; a task from `e2e-local/api#3` worked in `e2e-local/app`.
**Steps**:
1. Approve and merge each; read the stub gh's PR bodies.
**Expected**:
- `#1`'s PR body ends with "Closes e2e-local/app#1" (GitHub links it and closes the issue on merge); the Story's draft/ready PR body ends with "Closes e2e-local/app#40" and "Closes e2e-local/app#41".
- `#3`'s PR (another repository) has no Closes line. Jira/ADO PRs never get one.
