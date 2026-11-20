# TRACKERS.md — Jira Cloud, Azure DevOps and GitHub Projects as the ticket source

Tickets assigned to the developer in their employer's tracker drive the board. **The tracker is
only ever reached from the developer's machine, with their own token** (or, for GitHub, their own
`gh` login); Orkka holds no tracker credential and installs nothing in the tracker. Supported:
Jira Cloud, Azure DevOps Services and GitHub Projects (v2) on github.com or GHE.com (not Jira
Data Center, ADO Server or GitHub Enterprise Server). GitHub specifics are in
[GitHub Projects](#github-projects-v2) below.

## Pieces

| Where | What |
|---|---|
| Runner `Orkka.LocalRunner/Trackers/` | `TrackerStore` (non-secret settings, `~/.orkka/workspaces/{ws}/trackers.json`), `WorkspaceSecretStore` (tokens), `TrackerConnectionManager` (test-and-store, shared by the CLI and the settings page), `TrackerCredentialResolver` (the stored token or the gh login), `JiraCloudClient`, `AzureDevOpsClient`, `GitHubProjectsClient`, `TicketNormalizer`, `TrackerAttachmentCache`, `TrackerPoller`, `TrackerWriteBack` |
| Runner CLI | `./orkka tracker add jira\|ado\|github`, `list`, `test`, `remove` (`Cli/TrackerCommand.cs`) |
| Host module `Orkka.Modules.Trackers` (schema `trackers`) | `connections` (metadata from the runner's Hello), `project_configs` + `epic_mappings` (a project's ticket source and its routing rules), `ticket_links` (ticket ↔ task, sync state), `RepoRouter` (the runner utility that picks a repository) |
| Runner hub `HostedRunnerHub` | `SyncTrackers`, `ReportTickets`, `AckTransitions` — each authorised against the connection's bound (org, user); project ids are never taken from the payload |
| UI | Settings → Trackers (add / re-test / remove connections), Project settings → Ticket source, the project setup's Ticket source step (inline connect, the same card, the dry run) |

## Connecting a tracker

Either way the token is tested against the tracker first and only a working one is stored, in
the OS secret store (Windows Credential Manager, macOS Keychain, libsecret; an owner-only file
only on a machine with none). Only the connection's metadata — id (`jira-{site}` / `ado-{org}`),
kind, site, account id and display name, health — goes into the Hello, which the hub upserts
into `trackers.connections` per machine (a connection removed on a machine is marked missing,
never deleted).

- **Settings → Trackers** (the usual way; the project setup's Ticket source step shows the same
  form inline when this machine has no connection of the chosen kind). "Add Jira Cloud" (site, email, API token) or "Add
  Azure DevOps" (organization, PAT) posts to `POST /api/trackers/connections`
  `{kind, site, email?, token}`. The host relays it over loopback
  (`/internal/runner/tracker-connections`) to the caller's own runner — `AddTrackerConnection`,
  capability `tracker-connect` — which tests and stores the token, writes trackers.json, sends a
  fresh Hello and answers with metadata only; the host upserts the row and answers
  `{supported, ok, error?, connection?}` (`supported=false` = runner offline or too old). This is
  the **one host → runner field that carries a credential**: write-only, at the developer's
  request, over loopback, and the host never stores, logs, echoes or audits it (SECURITY.md).
  `DELETE /api/trackers/connections/{id}` (`RemoveTrackerConnection`) deletes the token and
  entry on the machine; the row goes too unless a project still points at it (then it stays,
  marked removed). Re-test = the `test` query below.
- **Terminal.** `./orkka tracker add jira --site acme.atlassian.net --email dev@acme.com` or
  `tracker add ado --org acme` asks for the token with no echo (or reads stdin).

Tokens needed: a Jira API token (https://id.atlassian.com/manage-profile/security/api-tokens),
or an ADO PAT with *Work Items (Read & write)* and *Project and Team (Read)*. GitHub needs no
token by default — the connection can use the developer's `gh` login — see
[GitHub Projects](#github-projects-v2) for both options, the scopes and SSO.

## A project's ticket source

`PUT /api/projects/{id}/tracker` (ProjectsManage, audited `TrackerConfigChanged`) stores: one of
the **caller's own** connections (their runner polls with their token, so the tickets picked up
are the ones assigned to them — the caller becomes the source's owner), a scope (Jira project /
ADO project) and:

- An optional **board** (`boardId` + `boardName`, see Boards below) — intake limited to one board
  of the scope; empty = the whole project (every board).

- **Intake statuses** (`intakeStatuses`, default `["To Do"]`; ADO: the Proposed states) — where a
  ticket must be to become a task. Only intake: a ticket that moves on keeps being tracked.
- **Scope mode** — `All` (every ticket assigned to the owner in an intake status; no epic needed)
  or `Epics` (only tickets under the epics listed; at least one).
- **Rules** (`epic_mappings`, `Kind` epic | component | label | areaPath, the value, a repo and an
  optional monorepo subfolder — `TrackerSubpath`: relative, forward slashes, never `..`). Epics
  come first, then the others, each in order; the first match wins. An epic without a repo only
  scopes intake. In the API: `epics[]` and `routingRules[]` (kind component / label / areaPath).
- An optional **default repository**, the optional extra JQL/WIQL clause, include-descendants,
  auto-submit (default on), the poll interval (60–300 s), the status map and the branch template
  (must contain `{key}`; default `{type}/{key}-{slug}`, rendered by `TrackerBranchName`).
  `storyMode` (default on) turns Stories on — see Stories below.

`DELETE /api/projects/{id}/tracker` removes the source. Deleting the project removes it too, with
its rules and ticket links (`TrackerCleanupOnProjectDeleted`, on `ProjectDeleted`), so nothing
polls a tracker for a project that is gone.

**Status map** — six optional steps, each any tracker status name (`statusMap: { pickedUp?,
planning?, developing?, inReview?, prOpened?, merged? }`; stored JSON with the old `started` key
reads as `planning`): Picked up = the task was created; Planning = the first PM/planning run;
Developing = the plan approved / first dev run; In review = Orkka's Code Review lane; PR opened =
the forge pull request published, or a folder landing (a Story: its pull request marked ready);
Merged = the forge pull request merged. **Defaults** (`TrackerStatusDefaults`, returned as
`defaults.statusMap` by the `statuses` query and pre-filled by the form): Planning = the first
status like "In Progress" / "Doing" / "Active" / "Committed" (else the first in-progress one); PR
opened = the first like "Review" (else for ADO the first Resolved state); the rest unmapped;
intake = "To Do" (ADO: Proposed states).

Everything the settings page shows — test, scopes, boards, epics, statuses, "N tickets would be
picked up" (the intake query) — is asked of the owner's runner live: `POST /api/trackers/connections/{id}/query`
→ `/internal/runner/tracker-query` → runner `TrackerQuery`. Runner offline → `Supported: false`.

### Boards — one board per project

A scope (a Jira project, an ADO project) often holds several teams' work, and different boards
touch different repositories. A source may name ONE board of its scope
(`ProjectTrackerConfig.BoardId` / `BoardName`, migration `TrackerBoard`; `boardId`/`boardName` on
`PUT/GET /api/projects/{id}/tracker`, on the `preview` query and on `TrackerPollAssignment`); empty
is exactly the old behaviour, the whole project.

- **Jira Cloud** — a board is an agile board of the project. The `boards` query lists them
  (`GET /rest/agile/1.0/board?projectKeyOrId={key}&maxResults=50&startAt=…`, paged, by name);
  `BoardId` is the board id. On every preview and poll the runner reads the board's filter live
  (`GET /rest/agile/1.0/board/{id}/configuration` → `filter.id`, then `GET /rest/api/3/filter/{id}`
  → `jql`) and ANDs it onto the intake JQL as `(<filter JQL>)` — its trailing `ORDER BY …` cut off
  at the top level, never inside a quoted string (`JiraQueries.StripOrderBy` /
  `BoardFilterClause`; a filter that is only an ordering adds nothing).
- **Azure DevOps** — a board belongs to a team. The `boards` query lists the project's teams
  (`GET {org}/_apis/projects/{project}/teams?$top=500&api-version=7.1`, the PAT's *Project and
  Team (Read)* scope); `BoardId` is the team id and the UI shows "<Team> board". On every preview
  and poll the runner reads the team's field values live
  (`GET {org}/{project}/{team}/_apis/work/teamsettings/teamfieldvalues?api-version=7.1` →
  `field.referenceName` (normally `System.AreaPath`), `values[{value, includeChildren}]`,
  `defaultValue`) and ANDs `([field] UNDER 'x' OR [field] = 'y' …)` onto the intake WIQL —
  `UNDER` when a value includes its children, WIQL quotes doubled, the default value when the team
  lists none (`AdoQueries.TeamFieldCondition`).
- **Intake only**, exactly like the extra clause: the tracking read (tracked keys, children of
  intake tickets) is not narrowed, so a ticket that leaves the board keeps syncing. The board
  clause comes before the extra clause in the query.
- **Never widened.** A board that is gone (Jira configuration 404, ADO team 404) fails the preview
  and the poll — "The board 'X' was not found in <project> — pick another board in the ticket
  source settings." — recorded as `LastPollError`; a Jira filter the account cannot read and an
  ADO team with no area paths fail the same way. A runner too old to know boards never receives a
  board-limited source: the runner sends `TrackerSyncRequest.Features = ["boards"]`
  (`TrackerSyncFeatures.Boards`), and without it `SyncAsync` withholds the source and records
  "…too old to know boards — update Orkka…". The `boards` query and a preview with a board need
  the runner capability `tracker-boards`; the relay answers "Your runner does not know boards
  yet — update Orkka" otherwise.
- The board id and name are non-secret settings; the token stays on the runner as for every
  other tracker call.

### Nothing is pulled before Start; the dry run

A new project goes through the guided setup (`/p/{slug}/setup`, PROJECTS.md → Guided setup) and its
ticket source is **not polled until the developer presses Start** (`Project.SetupCompletedAt`,
`POST /api/projects/{id}/setup/start`). Saving a source before that only prepares it.

- **The gate is on the host.** `SyncAsync` asks `IProjectsApi.GetSetupPendingProjectIdsAsync` for
  the due sources' projects and hands none of a project not started to the runner (no lease, no
  `LastPolledAt`); `ReportAsync` refuses a report for one (`SetupPendingError`, nothing imported,
  the poll not recorded) in case a lease was taken earlier. Projects that predate the setup were
  migrated as started (migration `ProjectSetup`: `SetupCompletedAt = CreatedAt`), so nothing
  changed for them.
- **The dry run** — `POST /api/projects/{id}/tracker/preview` (the setup's "What would be pulled";
  owner only; works before Start, which is what it is for). The host builds the assignment the next
  poll would get (`ITrackersApi.BuildPreviewAssignmentAsync`: the same `AssignmentAsync` as
  `SyncAsync`, its tracked keys included, read-only — no lease, no link ended) and relays a
  `TrackerQuery` of kind `dry-run` carrying it (runner capability `tracker-dry-run`, else "update
  Orkka"). The runner polls it with **the real poller** (`TrackerTicketPoller.PollAsync(…, dryRun:
  true)`: the same reads and normalization, but attachments only listed, no comments fetched,
  nothing remembered as delivered) and returns the tickets (`TrackerQueryResult.Tickets`). The host
  then runs them through **the poll's own intake decision** (`TrackersApi.PlanIntake` — the same
  function `ReportAsync` imports from: Stories first with their member sub-tasks
  (`IsMemberCandidate`), then the other untracked intake tickets assigned to me) and the real
  routing (`RouteAsync` without the repo router — "picked at import"), and answers
  `{supported, ok, error?, preview: {newTaskCount, tasks[], stories[{story, branch, members[]}],
  alreadyTracked[], skipped[{key, reason: notAssignedToMe | notInIntake}]}}`. Nothing is created,
  leased or cached. `TrackerSetupGateAndPreviewTests` proves parity: for the same report the
  preview lists exactly the tasks, Stories and members (keys, order, repositories, branch) the
  real poll creates.

## Polling and ingestion

1. Every 30 s the runner calls `SyncTrackers` with the connections it holds. The host returns
   the owner's enabled sources that are due — of started projects only (above) — each under a **poll lease**
   (`ProjectTrackerConfig.LeaseHolder` = the runner's instance id, a conditional update — one
   machine polls a source; the owner's ACTIVE runner connection takes over a lease held by the
   instance it replaced, so a restart does not pause intake), plus the **tracked** tickets with content hash and last comment time,
   plus pending status moves (below). Tracked = links whose task is not archived, that have not
   synced Merged and are under 30 days past PR opened — whoever they are assigned to now. Links
   that drop out get `TrackingEndedAt`.
2. The runner (protocol v5) runs two reads:
   - **intake** — assigned to the developer, in an intake status (none set: Jira status category
     "To Do", ADO Proposed states), in the scope, on the board when one is set (resolved first,
     live — see Boards), under the epics in Epics mode, plus the extra clause; epics/features
     themselves are skipped;
   - **tracking** — the tracked keys whatever their status or assignee (Jira `key in (…)`, 100 a
     query; a key Jira says does not exist is dropped and the batch asked again; ADO work items
     by id, `errorPolicy=Omit`), plus the children of intake tickets (reported for the stories
     phase; `InIntake` only if they match intake themselves).

   Each ticket carries `Status`/`StatusCategory`, `AssignedToMe` (Jira: assignee vs
   `/rest/api/3/myself`; ADO: `System.AssignedTo` vs `connectionData`), `InIntake`, `ParentKey`/
   `ParentType` and ordered `ChildKeys` (Jira `subtasks`; ADO Hierarchy-Forward by StackRank /
   BacklogPriority, then id). Text is normalized (`TicketNormalizer`: description and acceptance
   criteria capped at 20,000 chars, comments at 4,000), attachments cached on the machine
   (`~/.orkka/workspaces/{ws}/tickets/…`, size and type caps, metadata only travels), and a body
   travels only when its hash changed.
3. The host (`ITrackersApi.ReportAsync`):
   - a new **intake** ticket assigned to me → `ITasksApi.CreateTrackerTaskAsync` (title `[KEY] …`,
     type and priority mapped, `ExternalRef` `jira:{site}:{key}` / `ado:{org}:{id}` as the
     idempotency key, runs on the owner's runner) + a `ticket_links` row (`ParentKey`, `Role`
     Standalone); submitted when auto-submit is on. Repository routing: the rules → the
     project's only repo → the default repo → the **repo router** (`IRepoRouter`, a utility call
     on the owner's runner with the ticket and the repos' name, stack and description, answering
     `{repoIds, confidence, reason}`: one repo with high confidence is set; two or more make a
     cross-repo group, split on submit like a hand-made spanning task) → otherwise no repository:
     the task posts a question listing the repos and waits in PendingQuestions
     (`AskTrackerRepoAsync`); the answer sets the repo (`BoardOperations`) and submits it when
     the source auto-submits. Nothing plans a task without a repository;
   - a tracked ticket → status, parent, text (rewritten while nothing has started, else a note)
     and new comments (`MessageType.ExternalComment`) always sync — moving To Do → In Progress
     changes nothing else;
   - **retired** (`RetireTrackerTaskAsync`: archived if nothing started, else left running with a
     note — never killed) only when it is reassigned, moved to a done status category before any
     work started, or missing from a successful poll (deleted, moved, no longer visible).
     Reassigned back, or back in the developer's intake after a retirement, it is revived; a
     task a person archived stays archived.

   A failed poll records `LastPollError` and changes nothing else.
4. When a run starts, the runner copies the ticket's cached attachments into the task's
   worktree under `.orkka/context/attachments/`, which the prompt names.

## Status write-back — the only thing Orkka writes to a tracker

No events. On each `SyncTrackers` the host compares how far every tracked task has got
(`ITasksApi.GetTrackerTaskStatesAsync` → `TrackerPhase` PickedUp / Planning / Developing /
InReview / PrOpened / Merged; Story links read `GetTrackerGroupStatesAsync`) with
`TicketLink.SyncedPhase` and hands the runner the furthest **mapped** move ahead (unmapped steps
are skipped; moves never go backwards). A folder repo has no forge PR: its landing counts as
Merged, so with Merged unmapped the ticket moves to the PR-opened status (Code Review). Tracked
tickets whose work carries on after a reassignment still move. A move is handed out once per
2-minute window (`PendingPhase` / `PendingSince`); the runner applies it with the developer's
token (Jira: the workflow transition into that status; ADO: `System.State`; already there =
success) and answers `AckTransitions` (`SyncedAt` is stamped when the phase advances). Success
posts "Moved SHOP-12 to “In Progress”"; five failures give up with a thread note and
`TaskEntity.ExternalSyncError` on the card. "Merged" is detected with the developer's forge CLI
(`TrackerPrCheck`, at most every 10 minutes, only when "Merged" is mapped; the repo is the task's
current one).

## Hierarchy levels — what is a Story

The unit Orkka works is the **Story**: one feature, split into small sub-tasks that build on each
other's code on one branch, with one pull request. Epics are too broad (one epic holds many
unrelated features), so they are never grouped, imported or branched. Every runner reports each
ticket's normalized `TicketLevel` (`Level`, `ParentLevel` on `TrackerTicket`; stored on
`TicketLink.Level`):

| Level | Jira Cloud | Azure DevOps | GitHub Projects |
|---|---|---|---|
| `Above` (never imported) | `issuetype.hierarchyLevel` ≥ 1 (Epic, Initiative) | Epic / Feature category (and hidden types) | issue type Epic/Feature/Initiative, or an issue whose sub-issue has sub-issues |
| `Story` (one branch, one PR) | `hierarchyLevel` 0 (Story, Task, Bug) | Requirement category (User Story, PBI, Issue, Requirement); a Bug not under a story | an issue whose sub-issues have none, or with none |
| `SubTask` (one Orkka task) | `hierarchyLevel` −1 (`subtask`) | Task category; a Bug under a story | a sub-issue of a story |
| children order (= build order) | `subtasks[]` | StackRank, then BacklogPriority, then id | `subIssues` (100 per parent) |
| dependency link | `issuelinks` type "Blocks", `inwardIssue` = "is blocked by" | `System.LinkTypes.Dependency-Reverse` (Predecessor) | `blockedBy` |

ADO levels come from `workitemtypecategories` (`AzureDevOpsClient.LevelsOf`), so they are the
same in every process. Bugs are the exception: whether a bug is a requirement or a task is a team
setting, so it is read from where the bug sits. GitHub levels go by depth, with the issue type as
a hint (`GitHubProjects.LevelOf`). `Unknown` is what a runner that predates levels sends, and it
keeps the old behaviour.

- `IsStoryCandidate` = in intake, mine, level `Story` (or `Unknown`), with children. A sub-task
  with children of its own is a plain task, and an `Above` ticket is never a task
  (`IsNewTaskCandidate`). Runners already keep containers out of intake, and the host drops them
  too.
- `WantsParentContext` follows `ParentLevel` (an `Above` parent is no story context, whatever its
  name). With `Unknown`, the old rule applies: type names containing epic / feature / initiative
  are not story context.
- The dry run lists each ticket's `level`, and an `Above` ticket assigned to me is skipped with
  reason `aboveStory`.

### Dependency links

The runner reports `BlockedByKeys`, which are stored as `TicketLink.BlockedByKeysJson`. After every
report, `TrackersApi.ReconcileDependenciesAsync` → `ITasksApi.ReconcileTrackerDependenciesAsync`
keeps the project's `DependencySource.Tracker` edges equal to the links whose two ends are both
worked in the project:
- **The blocked ticket is a Story:** the edge goes on its first live member.
- **The blocker is a Story:** the edge points at its last member, which merges with the Story's
  PR. Members are met by merge, like any edge.
- **Skipped:**
  - a link inside one story, because the story order already chains it;
  - a link that would close a cycle, which becomes a "KEY (circular)" note instead.
- **Removed:** edges whose link went away. AI and manual edges are never touched.
- **Blockers Orkka does not work** (another project, not mine) are written to
  `TaskEntity.ExternalBlockedBy`. They show as a "Blocked by KEY" note on the card and are never
  waited for.

## Importing a ticket by its key

For work the tracker does not hand Orkka on its own: assigned to someone else or nobody, not in
an intake status, or sub-tasks never linked the way intake expects. Entry points are the board's
**"Import ticket"** button (project board when the project has a ticket source; workspace board,
which asks for the project) and the MCP tools `preview_tracker_ticket` / `import_tracker_ticket`.
All of them go through `ITrackerImports` (`Features/Imports/TrackerImportService`); TasksCreate is
required, and only the source's owner can import.

- **The tracker is the project's ticket source.** `SourceKindAsync` picks the parser, and
  `NormalizeKey` accepts a key, an id or a link:
  - Jira: `SHOP-10` or `…/browse/SHOP-10`;
  - ADO: `4567`, `#4567` or `…/_workitems/edit/4567`;
  - GitHub: `owner/repo#12` or the issue URL.
- **Preview** (`POST /api/projects/{id}/tracker/import/preview {key}`, nothing created). Two
  dry runs go through the loopback relay to the owner's runner (`tracker-query` / `dry-run`) with
  a `TrackerPollAssignment` that has `KeysOnly = true`: no intake, board or epics, only the known
  keys (Jira `key in`, ADO by id, GitHub by key with `viewer` asked separately). The first run
  reads the ticket; the second reads the ticket plus up to 100 of its `ChildKeys`.
  `PreviewImportAsync` answers with `TrackerImportPreview`:
  - the ticket's level, assignee-is-me, description, attachment metadata and route;
  - each sub-task with its status and assignee, ticked when it is not done and not on the board;
  - `existingTaskId` when the ticket is already linked.

  An `Above` ticket is refused. A runner that is offline or too old → `supported: false`.
- **Request** (`POST /api/projects/{id}/tracker/import {key, subTaskKeys}`). It writes a
  `trackers.tracker_imports` row (pending) and sets `LastPolledAt = null`, so the runner's next
  30-second cycle polls this source; `ExtraKnownKeysAsync` adds the key and the picked sub-tasks
  to that poll's known keys. The answer is 202 `pending`. A ticket already linked and tracked is
  pinned and answered 200 `done` at once.
- **The poll** (`ProcessImportsAsync`, before intake in `ReportAsync`). Because it is a real
  poll, the attachments are cached on the machine and the comments are fetched as for intake.
  - With picked sub-tasks: `ImportStoryAsync` makes a Story with exactly those members. The rest
    of the children go to `TicketLink.ExcludedChildKeysJson`: never members, never counted as
    pending, never read again.
  - Without picked sub-tasks: `ImportAsync` makes a task.
  - Whatever its status or assignee, every link made is **pinned** (`TicketLink.Pinned`):
    `ApplyToTrackedTicketAsync` never retires it for a reassignment, and
    `ReleaseNotMineMembersAsync` never releases a pinned member. Closing it in the tracker before
    work started still retires it.
  - A linked ticket that Orkka had let go is revived and pinned.
  - An `Above` ticket fails. A ticket absent from `MaxImportAttempts` (3) successful reports
    fails with "not found".
- **Status.** `GET /api/projects/{id}/tracker/import/{importId}` returns
  `pending | done (taskId) | failed (error)`. The dialog polls it every 3 s and then opens the task.
- **Cleanup.** A deleted project's import rows go with its ticket source
  (`TrackerCleanupOnProjectDeleted`).

## Stories — one branch, one pull request

With `storyMode` on, a ticket **assigned to me, in intake, at the story level (above), with at least
one open sub-task assigned to me that has not been started** is a Story (`TrackersApi.Stories`, `TasksApi.Stories`):

- **Members are mine and not started — USER RULE: Orkka only ever works tickets assigned to the
  developer.** The tracking read carries the children of every intake ticket whoever they are
  assigned to, each with its own `AssignedToMe` (Jira: the sub-task's assignee accountId vs
  `/myself`; ADO: `System.AssignedTo` id or uniqueName vs `connectionData`) and its normalized
  `StatusCategory` (`todo` = Jira "new" / ADO Proposed; `inprogress` = Jira "indeterminate" / ADO
  InProgress and Resolved; `done`; null = ADO Removed). A child is a member candidate
  (`TrackersApi.IsMemberCandidate`) only when it is assigned to me AND not under way: its category
  is `todo` or its status is one of the source's intake statuses. A teammate's sub-task, an
  unassigned one and one already in progress / resolved / in test (work done long ago, now with
  QA) is never a member and never a task. A ticket with children but none of them mine to do is
  not a Story: the main pass imports it as a plain task. The pending count
  (`group.PendingChildCount`, which holds the Story's last landing back) counts only children not
  read yet and candidates without a member — a child read and not mine is never waited for.

- **Intake.** `CreateTrackerStoryAsync` makes a `TaskGroup` with `Kind = Story` (`branch_name` =
  `TrackerBranchName.Render` of the Story's key, `external_key/url`; merge policy Independent) and
  one member task per candidate sub-task in tracker order (`story_order`, `BranchName` = the group's
  branch, a Manual `TaskDependency` on the previous member). The repo router places each
  sub-task; one in several repos is one member per repo in strict global order, and each repo
  gets a `tasks.group_branches` row (same branch, its own pull request). A member nobody could
  place asks the repository question; an answer naming several repositories is handled the same
  way (`StoryChain.SpanReposAsync`): the member takes the first, one new member per further
  repository is inserted right after it (`{ExternalRef}@{repo}`, the Story's branch, the later
  members moved down and the chain re-linked through the new ones), each repository gets its row,
  and — when the source auto-submits — every new member is submitted too. Links: role **Story** (→
  the group; its `TaskId` is the first member, where the Story's comments land; `ChildKeysJson`)
  and **SubTask** (→ its member; its assignee and status are judged by the cleanup below, not by
  the plain-ticket retire rules). A Story with no candidate sub-task is a
  plain task; a sub-task assigned to me whose Story is not mine is a plain task whose Story is
  read by key on the next poll and carried as **"Parent story context"** (`ParentTitle`/
  `ParentDescription`, `TaskTicketContext.Parent*`). Story members are neither split nor
  dependency-analysed.
- **Order.** A same-group edge is met at Completed (= passed Orkka's review, commits already
  pushed to the shared branch); an **archived blocker counts as met**, and archiving/retiring a
  member rewires its dependents to its predecessor (`StoryChain`). Members never plan ahead of a
  milestone gate. The next member's clone continues `origin/{branch}` (for a linked folder the
  folder is origin).
- **Review base.** At worktree creation a member records `review_base_sha` once (head of
  `origin/{branch}`, else the merge-base with the default branch); its Orkka pull request
  (`CreateForTaskAsync` with the explicit task id and `base_sha`), snapshots, QA and review diffs
  show its own change. Normally that is `review_base_sha..HEAD`. When a base sync merged the
  default branch into the shared branch during the member, the diff starts instead from the tree
  of `review_base_sha` merged with the newest synced default-branch commit (`git merge-tree
  --write-tree`, built on the fly by `GitWorkspaceManager.ReviewDiffBaseAsync`; nothing stored,
  no ref written) — otherwise the default branch's commits would show as the member's. Moving the
  stored base to an existing commit is not enough: with member commits before the sync, the sync
  merge's first parent lacks the default branch's changes and its second parent lacks the earlier
  members' work (`GitWorkspaceStoryTests` proves both on real repositories). A conflicted sync
  keeps its markers in that tree, so the member's resolution shows as its change.
- **Reset.** A reset member keeps the Story's branch (`FinalizeTaskResetAsync` leaves `BranchName`
  for story members) and clears `review_base_sha`; it re-plans on top of the shared branch as it
  stands, its earlier commits included — they were reviewed and pushed, and later members may
  build on them. After the reset its thread starts with a note giving the previous attempt's
  commit range (`TaskQueueProcessor.StoryResetNote`: its review base .. the last head its clone
  pushed) and how to drop it. There is deliberately no "discard my previous commits" button:
  Orkka never rewrites a shared branch (no force-push), and reverting is not a safe one-click
  action — the range can include base-sync merges (a revert needs a mainline and can conflict),
  a later member may already sit on top of it, and a revert is new code that deserves review.
  So the developer says so in the task ("revert the previous attempt's commits first") and the
  agent undoes it with ordinary, reviewed commits. Its open Orkka pull request follows the new
  base.
- **Prompts.** PM, planning, developing, review and interactive prompts — and the task's
  Discuss chat (`ITaskStoryContextSource` → `TaskStoryContextSource`) — carry a **Story
  context** section: the Story's text (fenced), the sub-tasks with ✓ done / ▶ this one /
  ○ pending, and for each done one its plan summary, commit range and changed files (from its
  snapshot — no code), plus "The previous sub-tasks' code is already in your working copy".
- **Landing** (`StoryLandingService`, from `MergeAsync`): the member's local pull request is
  Merged and the task Completed (the ticket stays open). The first landing opens the forge pull
  request as a **draft** (`gh pr create --draft`, `az repos pr create --draft true`, `glab mr
  create --draft`; runner capability `stories`, else "update Orkka"); later ones tick
  its checklist (`gh pr edit --body-file` / `az repos pr update --description` / `glab mr
  update --description`). The **last** one (no member left that is neither landed nor archived,
  no pending sub-task) checks EVERY repository of the story — its `group_branches` rows and the
  members' repositories, except a folder already squash-merged — refreshing each mirror and
  dry-run merging the shared branch into that repository's own default branch. Only when every
  one is clean does it run `MarkPullRequestReady` on every repo row (`gh pr ready`, `az repos pr
  update --draft false`, `glab mr update --ready`) and set `ready_at`. A conflict anywhere fails
  the landing with `StoryLandingErrors` (every pull request stays a draft); the merge sweep treats
  it as a conflict even when the landing member's own repository is clean, and the resolver
  (`TaskOrchestrator.ResolveStoryRepositoriesAsync`) posts a thread note naming the repositories,
  runs the existing conflict resolver (base sync, resolver agent, verification, build check,
  push) for each conflicting one in a clone of that repository's last member, then the landing
  member's own conflict if any, and lands again. A repository nobody could check (runner offline)
  leaves the drafts and retries every cycle as environment-blocked. Spent automatic attempts on
  another repository's conflict park the task on the merge page for the developer instead of a
  dev pass that cannot reach that repository. A linked folder has no draft; the whole shared
  branch is squash-merged into it at the end.
- **Write-back.** Story and SubTask links follow the group: the furthest step their members
  reached (all members for the Story, its own for a sub-task); **PR opened only at `ready_at`**,
  so every sub-task and the Story move to Code Review together; **Merged** when every story pull
  request merged (checked per PR from the Story's link, check id `g:{group}:{repo}`, stored on
  `group_branches.forge_state`).
- **Cleanup of members that are not mine** (`ReleaseNotMineMembersAsync`, every poll, before the
  late-sub-task pass). A live SubTask link whose child is not assigned to me — or, while Orkka has
  never moved that ticket itself (`SyncedPhase` None, nothing pending; a write-back move to
  In Progress is Orkka's own), is under way in the tracker — is judged by its members: none
  started (`Planning` false: still Drafting / Ready, no run) → `ReleaseStoryMemberAsync` archives
  each, rewires its dependents to its predecessor, drops its own story-order edges and takes it
  out of the group (`GroupId`, `StoryOrder`, `BranchName` cleared), posting the reason; the link
  ends (`TrackingEndedAt`, `AssignedToOwner` false, role Standalone, `ParentTitle` ""), and the
  Story link's `TaskId` moves to the first remaining member if it pointed at it. Started → left
  running; when it is not mine one note is posted ("… is not assigned to you in …; Orkka will not
  pick up more of it — archive this task if it isn't yours"), once (`AssignedToOwner` false marks
  it; it resets when the child is mine again). When no member is left and nothing landed, was
  readied or published, the group is cancelled (`CancelStoryAsync`; `CreateTrackerStoryAsync`
  ignores cancelled groups) and the Story link is deleted, so the main pass of the same poll
  imports the Story ticket as a plain task when it is still in intake and assigned to me (if the
  report carried it without its text, on the next poll). A child Orkka let go of is still read by
  key every poll (`ExtraKnownKeysAsync` skips only links that are live or that a person ended);
  when it becomes mine and not started again, its task is revived and appended at the end
  (`AppendStoryMemberAsync` adopts it, its other repositories' members too) and submitted.
- **Late sub-tasks** (a new child key on a tracked Story is read by key next poll, pending until
  then; a child that is not mine is not pending, and joins only once it is assigned to me and not
  started): before ready it joins the end; ready and still open → it joins, the pull requests go
  back to draft (`gh pr ready --undo`, `az repos pr update --draft true`, `glab mr update
  --draft`) and are readied again at the end (the tracker status is not moved back); after the
  story's pull request merged or closed → a plain task with the Story as parent context. In a
  mixed story (a linked folder plus a forge repository), a late sub-task in the folder reopens
  that folder's row (its "merged" state is cleared), so the end of the story squash-merges the
  shared branch into the folder again — it had never received the late work; a late sub-task in
  the forge repository leaves the squashed folder alone.

## GitHub Projects (v2)

A third tracker kind, `github` (`TrackerKinds.GitHub`), next to Jira and Azure DevOps and run by
the same machinery (poll lease, intake, tracking, retirement, Stories, write-back). The runner
side is `GitHubProjectsClient` (GraphQL only, plain `HttpClient` + System.Text.Json) and
`GitHubProjects` (keys, status categories, the intake rule).

### Hosts

- **github.com** (the default) — API `https://api.github.com/graphql`. GitHub Enterprise Cloud
  and **Enterprise Managed Users** on github.com work the same way.
- **GHE.com data residency** (`acme.ghe.com`, the usual home of EMU enterprises) — API
  `https://api.acme.ghe.com/graphql`. `TrackerSites.NormalizeGitHubHost` accepts `acme.ghe.com`,
  `api.acme.ghe.com` or any URL on them; the connection id is `github` / `github-acme`.
- **GitHub Enterprise Server (a self-hosted host) is not supported** and is refused when
  connecting: its GraphQL schema lags github.com (sub-issues, issue types), and Orkka would have
  to guess per version. `RepoUrl.DetectForge` reads `*.ghe.com` as GitHub too, so `gh pr create`
  publishes there.

### Authentication — chosen per connection

1. **The developer's `gh` login** (default, recommended; `AddTrackerConnectionRequest.AuthMode`
   `"gh"`, `TrackerConnectionEntry.Auth`). Nothing is stored: for every call
   (`TrackerCredentialResolver`) the runner runs `gh auth token --hostname <host>`
   (`GhCliCredentials.TokenAsync`, read-only, 15 s cap; gh's stderr is never shown) and keeps the
   token in memory for that call. The login needs the **`project`** scope (and `repo` for private
   issues — gh's default login has it): `gh auth refresh -h <host> -s project`. A test whose
   `X-OAuth-Scopes` lacks a scope fails with exactly that command; gh missing or signed out says
   to install it / run `gh auth login -h <host>`. `gh` is optional and never installed by Orkka.
   `./orkka doctor` lists each gh-login connection and whether gh answers for its host.
2. **A token**, stored in the OS secret store exactly like a Jira/ADO token: a classic PAT with
   `project` + `repo`, or a fine-grained token with Projects (organization) and Issues read &
   write. A fine-grained token has no `X-OAuth-Scopes`; missing permissions show as refusals.

Either way the token is sent only as `Authorization: Bearer` to that host's API, scrubbed from
every error (`TrackerCredential.Scrub`), never logged, and never reaches an agent (the
environment drops `GH_*`/`GITHUB*`/`*TOKEN*`; the command guard refuses `gh`). Switching a
connection from a token to the gh login deletes the stored token. Connect from Settings →
Trackers ("Add GitHub Projects": host + the two options) or `./orkka tracker add github
[--host acme.ghe.com] [--token]`. The relay refuses a GitHub connection or a `fields` / `repos`
query (or a query with a status field / repository filter) for a runner without the capability
`tracker-github` ("Your runner does not know GitHub Projects yet — update Orkka").

**SSO.** An organization that enforces SAML SSO refuses an unauthorized token
(`X-GitHub-SSO`, or a GraphQL FORBIDDEN mentioning SAML): a token connection is told to
authorize the token for the organization (Personal access tokens → Configure SSO); a gh-login
connection to run `gh auth login -h <host>` again and authorize the organization.

**Rate limits.** A secondary limit with a `Retry-After` of at most 10 s is waited out once;
anything longer (or a spent primary limit, `x-ratelimit-reset`) fails the call with the wait in
the message — a poll records it as `LastPollError` and the next poll tries again.

### The source: one project

- **Scope = an owner**: the `scopes` query lists the viewer's login and their organizations.
- **Board = one GitHub Project of that owner, required**: the `boards` query lists the owner's
  open projects (`TrackerBoard` with `Number` and `Url`); `BoardId` is the project's node id
  (`PVT_…`), `BoardName` its title. A project that is gone fails the poll like a missing board.
- **Status field** (`ProjectTrackerConfig.StatusField`, null = "Status"): any single-select field
  of the project (the `fields` query lists them). Its options are the status list (`statuses`
  query with `boardId` + `statusField`).
- **Repository filter** (`RepoFilterJson`, `owner/name`, empty = every repository): intake only
  takes issues from these (the `repos` query lists the repositories with issues on the project).
  Two Orkka projects can point at the same GitHub project with different filters.
- **Statuses**: pickup statuses and the six-step map hold option NAMES, as for Jira/ADO; the
  runner resolves a name to its option id when it moves an issue (case and spaces ignored, so
  "To Do" matches "Todo"). Defaults (`TrackerStatusDefaults`, GitHub branch): intake = Todo /
  Ready; Developing = In Progress; In review = In Review; Merged = Done; the rest unmapped.
- Not applicable and refused or dropped on save: epics / Epics mode (no epics on GitHub —
  sub-issues make Stories), component and area-path rules (label rules and the issue's own
  repository route instead), the extra JQL/WIQL clause, include-descendants.

### Intake, tracking and Stories

- The runner reads the whole project (`items`, 50 a page, at most 100 pages = 5,000 items; more
  fails the poll rather than report part of it). Only **ISSUE** items count: **draft issues** are
  skipped (no repository to branch from, no assignee, no sub-issues) and so are **pull requests**
  (already code), archived and redacted items.
- **Intake** = an open issue assigned to the viewer (`viewer.login`), in a pickup status (none
  set: a to-do-like one), in the repository filter. Key `owner/repo#n`, ticket id = the issue's
  node id, `ExternalRef` `github:github.com:owner/repo#n`.
- **Tracking** reads tracked keys and the intake issues' sub-issues by key, 25 per GraphQL
  query (aliased `repository(owner, name) { issue(number) }`), whatever their status or project;
  a key GitHub does not know (NOT_FOUND) is absent, so it retires as for the other trackers.
  Status = the issue's value of the status field **on this project** (none when it is not on it).
- **Status category** (`GitHubProjects.Category`): a closed issue is `done`; no status, an intake
  status or a to-do-like name (Todo, Ready, Backlog, New, Triage) is `todo`; a done-like name
  (Done, Closed, Complete, Shipped, Released) is `done`; anything else `inprogress`.
- **Levels** (see Hierarchy levels): an issue typed Epic/Feature/Initiative, or one whose sub-issue
  has sub-issues (`subIssuesSummary.total`), is above a story and never in intake; `blockedBy`
  (50) gives its dependency links.
- **Stories**: an issue with **sub-issues** reports them as `ChildKeys` in GitHub's own order;
  the host's Story rules apply unchanged — members are sub-issues assigned to me and not started
  (`IsMemberCandidate`), none qualifying = a plain task, and the cleanup / late sub-task rules
  are the same.
- **Text**: title, body (markdown, kept as written; HTML comments from issue templates dropped),
  labels (a `bug` label stands in for the issue type when the organization has no issue types),
  comments (markdown). Images in issue bodies are not cached as attachments: GitHub serves them
  from `user-attachments` URLs that need a browser session (or short-lived signed URLs that
  change every read), so the image links stay in the text.
- **Repository routing**: after the rules and the project's only repository, an issue goes to the
  project repository whose remote is the issue's repository (`TrackerTicketBody.Repository`,
  `TrackersApi.MatchRepository`: same owner and name, same host or an ssh host alias), before the
  default repository and the repo router.
- Polling a GitHub source needs `TrackerSyncRequest.Features` "github"; an older runner is not
  handed one and the source shows "…too old to know GitHub — update Orkka…".

### Write-back and pull requests

- A status move (`TrackerTransition` + `BoardId` + `StatusField`) sets the issue's single-select
  value on the source's project (`updateProjectV2ItemFieldValue`). An issue not on the project
  yet (a sub-issue nobody added) is added first (`addProjectV2ItemById`) — that is where its
  status lives. Already there = success, nothing written. As for Jira/ADO, the status is the only
  thing Orkka writes to the tracker: no comments.
- The link to the pull request comes from GitHub itself: a task imported from an issue in the
  same repository publishes its pull request with `Closes owner/repo#n` appended
  (`GitHubIssueLinks`, in `OrkkaPullRequestService.PublishAsync`), and a Story's pull request
  lists `Closes …` for the Story and each sub-issue in that repository
  (`StoryLandingService.Body`), so GitHub links them and closes them on merge. An issue in
  another repository is not referenced.
- The branch template works as everywhere: `{key}` renders `owner/repo#12` as `owner/repo-12`
  (`feature/acme/shop-12-fix-cart` by default).

## Security notes

- Tokens never leave the machine: not in the Hello, not in errors (`TrackerClient` scrubs them),
  not in agent environments; agents cannot read the secret store or `~/.orkka` (SECURITY.md). A
  GitHub gh-login connection stores nothing; the token gh returns lives for one call.
  A token typed into Settings → Trackers crosses the local host once, in memory, write-only, to
  the developer's own runner — never logged (request logging is method/path/status), never in a
  400, never in audit metadata, never sent back.
- Ticket text is untrusted input: it reaches agents (and the repo router) only fenced
  (`PromptSafety.Wrap`), and an injected instruction still runs into the command guard.
- Ticket text and comments are stored by Orkka as task content; attachments are not.

## Test opt-ins (runner)

`ORKKA_TRACKER_ALLOW_INSECURE_SITE=1` lets e2e fixtures point a connection at an `http://`
fake tracker (a loopback GitHub site serves its GraphQL at `{site}/graphql`); `ORKKA_SECRET_STORE=file` keeps a sandbox out of the real keychain (any
`ORKKA_HOME` other than the machine's own Orkka home gets the file store anyway).
