---
area: TING
title: Tracker polling and ingestion — tickets become tasks
modes-default: [tracker]
---

## TING-001 A new ticket assigned to me under a mapped epic becomes a task
```yaml
priority: P0
surface: [api, hub]
automation: automated
refs:
  - orkka-api:GET /api/projects/{projectId}/tasks
  - orkka-api:GET /api/tasks/{taskId}
  - i18n:externalTicket
costUsd: 0
since: 2026-10-26
```
**Preconditions**: Runner online with the fake Jira connection; TSRC-004 configuration with `autoSubmit:true`; the fake tracker has no tickets yet.
**Steps**:
1. Create SHOP-101 "Add coupon field" under SHOP-1, assigned to the token's owner, status To Do, with a description and acceptance criteria.
2. Poll `GET /api/projects/{id}/tasks` (≤ 2 poll intervals).
**Expected**:
- One task titled `[SHOP-101] Add coupon field` with `externalKey:"SHOP-101"`, `externalUrl:"<site>/browse/SHOP-101"`, `externalEpic:"SHOP-1"`, `repoId` = SHOP-1's repo, working subpath `packages/api`.
- Description holds the ticket text as markdown plus the acceptance criteria; the task was submitted (left Drafting) because auto-submit is on.
- The fake tracker logged `POST /rest/api/3/search/jql` with the JQL `parent in ("SHOP-1") AND assignee = currentUser() AND statusCategory != Done ORDER BY key ASC`.

## TING-002 With auto-submit off the new task waits in Drafting
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/tasks
since: 2026-10-26
```
**Preconditions**: Configuration with `autoSubmit:false`.
**Steps**:
1. Create SHOP-102 in the fake tracker; wait two intervals.
**Expected**:
- Task `[SHOP-102] …` exists in Drafting, not submitted.

## TING-003 An unchanged ticket changes nothing
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/tasks
  - orkka-api:GET /api/tasks/{taskId}/messages
since: 2026-10-26
```
**Preconditions**: TING-001 done.
**Steps**:
1. Wait three more poll intervals without touching the ticket.
**Expected**:
- Still exactly one task for SHOP-101; no new messages; the report carried the ticket key with the same content hash and no body.

## TING-004 A changed ticket rewrites the task while nothing has started
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/tasks/{taskId}
since: 2026-10-26
```
**Preconditions**: A Drafting tracker task (auto-submit off).
**Steps**:
1. Change the ticket's summary and description in the fake tracker (bump `updated`).
**Expected**:
- Within two intervals the task's title becomes `[KEY] <new summary>` and its description the new text.

## TING-005 A changed ticket on a started task posts a note instead
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/tasks/{taskId}/messages
since: 2026-10-26
```
**Preconditions**: A tracker task already in Planning or later.
**Steps**:
1. Change the ticket's description.
**Expected**:
- Title/description of the task are untouched; a thread note carries the new ticket text (capped).

## TING-006 New ticket comments become ExternalComment messages
```yaml
priority: P0
surface: [api, ui]
automation: automated
refs:
  - orkka-api:GET /api/tasks/{taskId}/messages
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - i18n:externalTicket
since: 2026-10-26
```
**Preconditions**: TING-001 task.
**Steps**:
1. Add two comments to SHOP-101 in the fake tracker (author "Pat PM").
**Expected**:
- Two messages with `messageType:"ExternalComment"`, role `tracker`, author Pat PM, body as markdown, oldest first; the thread labels them "From Jira".
- A comment older than the last one already imported is not imported again (`GET /rest/api/3/issue/SHOP-101/comment?orderBy=-created&maxResults=30&expand=renderedBody`).

## TING-007 Attachments are cached on the runner; only metadata reaches the host
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/tasks/{taskId}
since: 2026-10-26
```
**Preconditions**: A ticket with a 2 KB `spec.txt` attachment served by the fake tracker on its own host.
**Steps**:
1. Let it be ingested; inspect `~/.orkka/workspaces/<ws>/tickets/<connection>/<KEY>/`; start the task's run.
**Expected**:
- The file is in the ticket cache; the task description lists the attachment by name/size only (no bytes in the API); the run's worktree has `.orkka/context/attachments/spec.txt`.
- An attachment URL on a foreign host is never fetched with the token.

## TING-008 A ticket that leaves the set is archived if nothing started
```yaml
priority: P0
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/tasks
  - orkka-api:GET /api/tasks/{taskId}
since: 2026-10-26
```
**Preconditions**: A Drafting tracker task.
**Steps**:
1. Unassign the ticket (or move it to Done) in the fake tracker.
**Expected**:
- Within two intervals the task is archived (`isArchived:true`).

## TING-009 A ticket that leaves after work started keeps running with a note; returning revives
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/tasks/{taskId}/messages
  - orkka-api:GET /api/tasks/{taskId}
since: 2026-10-26
```
**Preconditions**: One tracker task in Developing; one archived by TING-008.
**Steps**:
1. Unassign the developing ticket.
2. Re-assign the archived ticket.
**Expected**:
- The developing task is not killed — a thread note says the ticket left the source.
- The archived task is unarchived (no second task is created — `ExternalRef` is the idempotency key).

## TING-010 A failed poll records LastPollError and changes nothing
```yaml
priority: P0
surface: [api, ui]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/tracker
  - orkka-api:GET /api/projects/{projectId}/tasks
  - i18n:ticketSourceStatus
since: 2026-10-26
```
**Preconditions**: TING-001 state; the fake tracker switched to answer 500 on search.
**Steps**:
1. Wait two intervals; `GET …/tracker`; `GET …/tasks`.
2. Switch the fake back to normal; wait two intervals.
**Expected**:
- `lastPollError` = "Jira answered HTTP 500 for the ticket query."; no task archived or changed (a failed poll is never read as "every ticket left").
- After recovery `lastPollError` is null and `lastPolledAt` advances.

## TING-011 Poll lease: two machines with the same connection, one polls
```yaml
priority: P1
surface: [hub, api]
automation: manual
refs:
  - orkka-api:GET /api/projects/{projectId}/tracker
  - orkka-api:GET /api/projects/{projectId}/tasks
since: 2026-10-26
```
**Preconditions**: The same Jira connection added on two machines of the same user (two runners online).
**Steps**:
1. Create SHOP-110; watch both runners' logs and the fake tracker's request log for 3 intervals.
**Expected**:
- Exactly one task is created; only the lease holder's instance queries the tracker for that source while its lease is fresh; stopping it hands the lease to the other machine after expiry.

## TING-012 Done and unassigned tickets are never picked up
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/tasks
since: 2026-10-26
```
**Preconditions**: Fake tracker: SHOP-120 under SHOP-1 assigned to someone else; SHOP-121 assigned to me in a Done-category status.
**Steps**:
1. Wait two intervals.
**Expected**:
- No task for either (the JQL carries `assignee = currentUser() AND statusCategory != Done`).

## TING-013 Include descendants picks up grandchildren and names the configured epic
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/tasks
since: 2026-10-26
```
**Preconditions**: `includeDescendants:true`; SHOP-130 is a child of SHOP-5, which is a child of epic SHOP-1.
**Steps**:
1. Wait two intervals.
**Expected**:
- Task `[SHOP-130] …` with `externalEpic:"SHOP-1"` (the configured epic, not its direct parent).

## TING-014 The extra clause narrows what is picked up
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/tasks
since: 2026-10-26
```
**Preconditions**: `extraClause:"labels = e2e"`; two eligible tickets, one labelled `e2e`.
**Steps**:
1. Wait two intervals.
**Expected**:
- Only the labelled ticket becomes a task; the search JQL ends with `AND (labels = e2e) ORDER BY key ASC`.

## TING-015 Azure DevOps work items become tasks
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/tasks
since: 2026-10-26
```
**Preconditions**: An ADO connection to the fake server; scope `Shop`, epic 1; work item 42 (User Story) under it assigned to @Me.
**Steps**:
1. Wait two intervals.
**Expected**:
- Task `[42] <title>` with `externalUrl:"<site>/Shop/_workitems/edit/42"`, description + acceptance criteria + repro steps from the item; the fake logged the two WIQL calls and `POST /_apis/wit/workitemsbatch?api-version=7.1` with `$expand: Relations`.

## TING-016 Too many tickets: the poll fails instead of reporting a partial set
```yaml
priority: P2
modes: [offline]
surface: [api]
automation: planned
refs:
  - orkka-api:src/Orkka.LocalRunner/Trackers/JiraCloudClient.cs
since: 2026-10-26
```
**Preconditions**: The fake Jira configured to answer > 2,000 matches (20 pages of 100 with `nextPageToken`).
**Steps**:
1. Let the runner poll.
**Expected**:
- The poll fails with "More than 2000 tickets match — too many to poll. Narrow the epics or the extra condition."; nothing is archived.

## TING-017 Ticket markup is cleaned before it reaches the agent
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/tasks/{taskId}
  - orkka-api:src/Orkka.LocalRunner/Trackers/TicketNormalizer.cs
since: 2026-10-26
```
**Preconditions**: A ticket whose rendered description contains `<script>`, an `<iframe>`, a table and 30,000 characters.
**Steps**:
1. Let it be ingested.
**Expected**:
- Description is markdown with the script/iframe dropped, the table kept, and capped at 20,000 chars ending "…(truncated)".

## TING-018 The prompt names the ticket, epic, folder and attachments
```yaml
priority: P2
surface: [api]
automation: manual
refs:
  - orkka-api:GET /api/tasks/{taskId}/executions
since: 2026-10-26
```
**Preconditions**: A tracker task with subpath `packages/api` and one attachment.
**Steps**:
1. Run it; read the run's prompt (debug log).
**Expected**:
- The task context names `SHOP-101`, epic SHOP-1, "work in packages/api", and `.orkka/context/attachments/`.

## TING-019 The task card and page show the ticket badge and epic
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-ui:packages/ui/src/components/task/ExternalTicketBadge.tsx
  - i18n:externalTicket
since: 2026-10-26
```
**Preconditions**: TING-001 task.
**Steps**:
1. Read the board card; open the task page; filter the board by epic.
**Expected**:
- Card badge "SHOP-101" with title "Open SHOP-101 in Jira"; the page shows "SHOP-101 in Jira" linking to `externalUrl` and "Epic: SHOP-1"; the epic filter shows only that epic's tasks.

## TING-020 The branch follows the project's branch template
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/tasks/{taskId}/pull-request
since: 2026-10-26
```
**Preconditions**: `branchTemplate:"feature/{key}-{slug}"`; a tracker task run to review.
**Steps**:
1. `GET …/pull-request`.
**Expected**:
- `headBranch` = `feature/SHOP-101-add-coupon-field` — the tracker can link it to the ticket.

## TING-021 A board narrows intake only; tracked tickets keep syncing
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/tasks
  - orkka-api:src/Orkka.LocalRunner/Trackers/JiraCloudClient.cs
  - orkka-api:src/Orkka.LocalRunner/Trackers/AzureDevOpsClient.cs
since: 2026-11-18
```
**Preconditions**: Jira source on SHOP limited to "Web board" (filter `project = SHOP AND component = Web ORDER BY Rank ASC`); To Do tickets SHOP-10 (component Web) and SHOP-20 (component Api) assigned to the token's owner; SHOP-10 already a task.
**Steps**:
1. Wait two intervals.
2. In the fake, change SHOP-10's component to Api and add a comment; change the board's filter to `project = SHOP AND component = Api`.
3. Wait two intervals.
**Expected**:
- 1: only SHOP-10 is a task; the intake JQL is `… AND (project = SHOP AND component = Web) ORDER BY key ASC` and the fake logged `GET /rest/agile/1.0/board/{id}/configuration` and `GET /rest/api/3/filter/{id}` on every poll.
- 3: SHOP-10's comment reaches its task (tracking reads tracked keys whatever the board — intake only, like the extra clause), and SHOP-20 now becomes a task (the filter is read live). The ADO variant: the intake WIQL carries the team's `[System.AreaPath] UNDER/=` condition, read from `teamsettings/teamfieldvalues` on every poll.

## TING-022 A board that is gone fails the poll; it never widens to the whole project
```yaml
priority: P1
surface: [api, ui]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/tracker
  - orkka-ui:packages/ui/src/components/project/TicketSourceCard.tsx
since: 2026-11-18
```
**Preconditions**: An ADO source limited to team "Web"; work items in other area paths assigned to the token's owner.
**Steps**:
1. Delete the team in the fake (`removeTeam`); wait two intervals.
2. `GET …/tracker`; open Settings → "Ticket source".
**Expected**:
- No new tasks; nothing is retired (a failed poll changes nothing else).
- `lastPollError` = "The board 'Web' was not found in Shop — pick another board in the ticket source settings."; the card's status line shows it.
- The same for a Jira board that answers 404 on its configuration; a board whose filter is not shared fails with "The filter of the board '<name>' cannot be read by this Jira account…".

## TING-023 A runner too old to know boards is not given a board-limited source
```yaml
priority: P2
surface: [api, hub]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/tracker
  - orkka-api:src/Modules/Orkka.Modules.Trackers/PublicApi/TrackersApi.cs
since: 2026-11-18
```
**Preconditions**: A source limited to a board; a runner whose `SyncTrackers` request carries no `features: ["boards"]`.
**Steps**:
1. Let that runner call `SyncTrackers`.
2. `GET …/tracker`.
**Expected**:
- No assignment for the source (it would ignore the board and pick up the whole project); `lastPollError` says the polling Orkka is too old to know boards and to update it (`git pull`, then `./orkka start`). A current runner gets the assignment with `boardId`/`boardName`.
**Notes**: covered at unit level by `tests/Orkka.IntegrationTests/TrackerBoardEndpointTests.cs`.
