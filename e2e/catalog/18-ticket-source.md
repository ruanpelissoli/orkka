---
area: TSRC
title: Ticket source — a project's Jira / Azure DevOps configuration
modes-default: [tracker]
---

## TSRC-001 GET the ticket source of a project that has none → 204
```yaml
priority: P0
surface: [api]
automation: automated
refs:
  - orkka-api:GET /api/projects/{projectId}/tracker
since: 2026-10-26
```
**Preconditions**: A fresh `e2e-*` project.
**Steps**:
1. `GET /api/projects/{id}/tracker`.
**Expected**:
- 204 with an empty body; an unknown project id → 404.

## TSRC-002 PUT with a connection that is not mine → 400 on trackerConnectionId
```yaml
priority: P0
surface: [api]
automation: automated
refs:
  - orkka-api:PUT /api/projects/{projectId}/tracker
since: 2026-10-26
```
**Preconditions**: An `e2e-*` project with one repo.
**Steps**:
1. `PUT …/tracker {trackerConnectionId:<random ULID>, scope:"SHOP", epics:[{epicKey:"SHOP-1", repoId:<repo>}]}`.
2. Same with the id of a connection that belongs to another member.
**Expected**:
- Both → 400 with the error on `trackerConnectionId`: "Pick one of your own tracker connections — add one in Settings → Trackers (or `./orkka tracker add`)."; `GET …/tracker` still 204.

## TSRC-003 PUT validation messages
```yaml
priority: P0
surface: [api]
automation: automated
refs:
  - orkka-api:PUT /api/projects/{projectId}/tracker
since: 2026-10-26
```
**Preconditions**: An `e2e-*` project with one repo; a valid connection of the caller (or a random id — validation runs first).
**Steps**:
1. Send, one at a time: `pollIntervalSeconds:59`; `pollIntervalSeconds:301`; `branchTemplate:"feature/x"`; `epics:[]`; two epics with the same key; `subpath:"../x"`; `subpath:"C:/x"`; `scope:""`; an epic whose `repoId` is not in the project.
**Expected**:
- 400 each: "Check for tickets every 60–300 seconds."; "The branch template must contain {key} — that is how the tracker links the branch to its ticket."; "Add at least one epic."; "Each epic can be mapped once."; "A subfolder is a relative path inside the repository, like packages/api — no '..', no drive letters."; "Pick the Jira project or Azure DevOps project the tickets live in."; "Epic SHOP-1 points at a repository that is not in this project."

## TSRC-004 PUT stores the configuration and makes the caller its owner
```yaml
priority: P0
surface: [api]
automation: automated
refs:
  - orkka-api:PUT /api/projects/{projectId}/tracker
  - orkka-api:GET /api/projects/{projectId}/tracker
  - orkka-api:GET /api/audit
since: 2026-10-26
```
**Preconditions**: Runner online with a fake-tracker Jira connection reported (`GET /api/trackers/connections` lists it); an `e2e-*` project with one repo.
**Steps**:
1. `PUT …/tracker {trackerConnectionId, scope:"SHOP", epics:[{epicKey:"SHOP-1", epicTitle:"Checkout", repoId, subpath:"packages\\api"}], extraClause:" labels = e2e ", autoSubmit:true, pollIntervalSeconds:60, statusMap:{started:"In Progress", prOpened:"In Review", merged:"Done"}, branchTemplate:"feature/{key}-{slug}"}`.
2. `GET …/tracker`; `GET /api/audit?projectId=…`.
**Expected**:
- 200 with `ownerUserId` = caller, `connection` populated, `scope:"SHOP"`, `extraClause:"labels = e2e"` (trimmed), `epics[0].subpath:"packages/api"` (normalised), the status map and template echoed, `enabled:true`, `lastPollError:null`.
- GET returns the same; the audit log has `TrackerConfigChanged` "Ticket source set: jira <site> / SHOP, 1 epic(s)".

## TSRC-005 DELETE removes the ticket source; tasks already picked up stay
```yaml
priority: P0
surface: [api]
automation: automated
refs:
  - orkka-api:DELETE /api/projects/{projectId}/tracker
  - orkka-api:GET /api/projects/{projectId}/tracker
since: 2026-10-26
```
**Preconditions**: TSRC-004 configuration; one ticket already ingested as a task.
**Steps**:
1. `DELETE …/tracker`; `GET …/tracker`; `GET /api/projects/{id}/tasks`.
**Expected**:
- 204, then 204 (none); the ingested task is still on the board; audit "Ticket source removed".

## TSRC-006 Tracker query while my runner is offline → supported:false
```yaml
priority: P0
surface: [api]
automation: automated
refs:
  - orkka-api:POST /api/trackers/connections/{connectionId}/query
since: 2026-10-26
```
**Preconditions**: A connection row of the caller (reported earlier); runner stopped.
**Steps**:
1. `POST /api/trackers/connections/{id}/query {kind:"scopes"}`.
**Expected**:
- 200 `{supported:false}` (no `result`), never a 5xx.

## TSRC-007 Tracker query with an unknown kind → 400
```yaml
priority: P1
surface: [api]
automation: automated
refs:
  - orkka-api:POST /api/trackers/connections/{connectionId}/query
since: 2026-10-26
```
**Preconditions**: Any connection id of the caller (or a random one — the kind is checked first).
**Steps**:
1. `POST …/query {kind:"drop-tables"}`.
**Expected**:
- 400 "Unknown query 'drop-tables'."

## TSRC-008 Tracker query on an unknown or foreign connection → 404
```yaml
priority: P1
surface: [api]
automation: automated
refs:
  - orkka-api:POST /api/trackers/connections/{connectionId}/query
since: 2026-10-26
```
**Preconditions**: A random ULID; the id of another member's connection.
**Steps**:
1. `POST /api/trackers/connections/<id>/query {kind:"test"}` for each.
**Expected**:
- 404 both — a query runs on the owner's machine with the owner's token.

## TSRC-009 GET /api/trackers/connections lists only my connections
```yaml
priority: P0
surface: [api]
automation: automated
refs:
  - orkka-api:GET /api/trackers/connections
since: 2026-10-26
```
**Preconditions**: The caller's runner reported one connection; another member's runner reported another (manual for the second half).
**Steps**:
1. `GET /api/trackers/connections`.
**Expected**:
- Only the caller's: `{id, key, kind, siteUrl, accountId, displayName, healthy, healthDetail, machineName, lastSeenAt, checkedAt}` — no email, no token.

## TSRC-010 Test, scopes, epics, statuses and preview through the runner
```yaml
priority: P0
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/trackers/connections/{connectionId}/query
since: 2026-10-26
```
**Preconditions**: Runner online; the fake Jira serves project SHOP with epics SHOP-1/SHOP-2, statuses To Do / In Progress / In Review / Done and 3 tickets under SHOP-1 assigned to the token's owner.
**Steps**:
1. `query {kind:"test"}`, `{kind:"scopes"}`, `{kind:"epics", scope:"SHOP", search:"check"}`, `{kind:"statuses", scope:"SHOP"}`, `{kind:"preview", scope:"SHOP", epicKeys:["SHOP-1"]}`.
**Expected**:
- Each 200 `{supported:true, result}`: test → the account's display name; scopes include `SHOP`; epics → SHOP-1 by summary search (a bare `1` finds SHOP-1 by key); statuses exclude the Epic type's own; preview → count 3, up to 5 samples and the effective JQL `parent in ("SHOP-1") AND assignee = currentUser() AND statusCategory != Done`.

## TSRC-011 Configure a ticket source from the project settings card
```yaml
priority: P0
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/settings
  - orkka-ui:packages/ui/src/components/project/TicketSourceCard.tsx
  - i18n:ticketSource
since: 2026-10-26
```
**Preconditions**: Runner online with a Jira connection; a project with two repos.
**Steps**:
1. Open Settings → "Ticket source"; pick the connection (`Jira · <site> · <machine>`); click "Test connection".
2. Pick "Jira project" SHOP; search epics "check"; add SHOP-1; pick a repository and type `packages/api` as subfolder.
3. Click "Preview"; map "Started" → In Progress, "Pull request opened" → In Review, "Merged" → Done; Save.
**Expected**:
- "Connected as <account>."; the epic row shows repo + subfolder; "3 tickets would be picked up right now" with the query; "Ticket source saved."; reload shows the same values.

## TSRC-012 The card says start your runner while it is offline
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:packages/ui/src/components/project/TicketSourceCard.tsx
  - i18n:ticketSource
since: 2026-10-26
```
**Preconditions**: Runner stopped; a connection exists.
**Steps**:
1. Open the card; click "Test connection".
**Expected**:
- "Your runner didn't answer, so the tracker can't be browsed. Start it with `./orkka start`, or update it with `git pull` + `./orkka start` if it is too old."; scope/epic pickers do not load; no error toast.

## TSRC-013 No connections yet: the card explains how to add one
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:packages/ui/src/components/project/TicketSourceCard.tsx
  - i18n:ticketSource
since: 2026-10-26
```
**Preconditions**: The caller has no tracker connections in this workspace.
**Steps**:
1. Open Settings → "Ticket source".
**Expected**:
- "You have no tracker connections yet. Add one on your machine, then reload this page."

## TSRC-014 Client-side validation mirrors the API's
```yaml
priority: P2
surface: [ui]
automation: planned
refs:
  - orkka-ui:packages/ui/src/components/project/TicketSourceCard.tsx
  - i18n:ticketSource
since: 2026-10-26
```
**Preconditions**: The card open with a connection and scope picked.
**Steps**:
1. Save with no epics; with an epic without a repository; with subfolder `../x`; with poll `30`; with branch template `feature/x`.
**Expected**:
- "Add at least one epic."; "Pick a repository for every epic."; the subfolder message; "Between 60 and 300 seconds."; "Must contain {key} — …" — no request is sent.

## TSRC-015 Status map dropdowns come from the statuses query; Don't change skips a step
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:packages/ui/src/components/project/TicketSourceCard.tsx
  - orkka-api:POST /api/trackers/connections/{connectionId}/query
  - i18n:ticketSource
since: 2026-10-26
```
**Preconditions**: Card with scope SHOP.
**Steps**:
1. Open each of the three dropdowns; leave "Merged" on "Don't change"; save.
**Expected**:
- Each lists exactly the statuses from `query {kind:"statuses"}` plus "Don't change"; the saved `statusMap.merged` is null (TWB-004).

## TSRC-016 Extra JQL / WIQL and include descendants are part of the preview
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:packages/ui/src/components/project/TicketSourceCard.tsx
  - orkka-api:POST /api/trackers/connections/{connectionId}/query
since: 2026-10-26
```
**Preconditions**: Card with SHOP-1 mapped; the fake tracker has a story under a child of SHOP-1.
**Steps**:
1. Type `labels = e2e` in "Extra JQL (optional)"; Preview.
2. Tick "Include descendants"; Preview.
**Expected**:
- The effective query gains `AND (labels = e2e)` and the count drops accordingly.
- With descendants the query is `parent in (<epic>, <its children>) …` and the grandchild is counted.

## TSRC-017 A member without ProjectsManage sees the card read-only
```yaml
priority: P2
surface: [ui, api]
automation: manual
refs:
  - orkka-ui:packages/ui/src/components/project/TicketSourceCard.tsx
  - orkka-api:PUT /api/projects/{projectId}/tracker
  - i18n:ticketSource
since: 2026-10-26
```
**Preconditions**: A member without `ProjectsManage`.
**Steps**:
1. Open the card; `PUT …/tracker` as that member.
**Expected**:
- "You need the Manage projects permission to change the ticket source."; PUT → 403.

## TSRC-018 Another member's ticket source shows its owner; saving takes it over
```yaml
priority: P2
surface: [ui]
automation: manual
refs:
  - orkka-ui:packages/ui/src/components/project/TicketSourceCard.tsx
  - orkka-api:PUT /api/projects/{projectId}/tracker
  - i18n:ticketSource
since: 2026-10-26
```
**Preconditions**: A source configured by member X; viewed by member Y with ProjectsManage and their own connection.
**Steps**:
1. Open the card as Y; save with Y's connection.
**Expected**:
- "This ticket source belongs to X — their runner polls it with their connection. Saving makes you the owner, with one of your connections."; after save `ownerUserId` = Y.

## TSRC-019 Disable the source without deleting it
```yaml
priority: P1
surface: [api, ui]
automation: planned
refs:
  - orkka-api:PUT /api/projects/{projectId}/tracker
  - i18n:ticketSource
  - i18n:ticketSourceStatus
since: 2026-10-26
```
**Preconditions**: TSRC-004 configuration.
**Steps**:
1. Untick "Enabled"; Save; wait two poll intervals.
**Expected**:
- `enabled:false`; no `SyncTrackers` assignment is handed out (no new tasks); the board status line reads "Tickets from <tracker> · paused".

## TSRC-020 Board status line: last checked, not polled yet, last error
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-ui:packages/ui/src/components/board/TicketSourceStatusLine.tsx
  - i18n:ticketSourceStatus
since: 2026-10-26
```
**Preconditions**: A configured source.
**Steps**:
1. Before the first poll; after a successful poll; after a failed poll (fake tracker answers 500).
**Expected**:
- "Tickets from Jira · not polled yet — start your runner (`./orkka start`)" → "Tickets from Jira · last checked <when> on <machine>" → "Tickets from Jira · last check failed: <error>" (TING-010).

## TSRC-021 Remove the ticket source from the card
```yaml
priority: P2
surface: [ui]
automation: planned
refs:
  - orkka-ui:packages/ui/src/components/project/TicketSourceCard.tsx
  - orkka-api:DELETE /api/projects/{projectId}/tracker
  - i18n:ticketSource
since: 2026-10-26
```
**Preconditions**: A configured source.
**Steps**:
1. Click "Remove"; read the confirm; confirm.
**Expected**:
- "Remove this ticket source? Tickets already picked up stay on the board." → "Removing…" → "Ticket source removed."

## TSRC-022 Tracker connections on the Trackers settings page
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:/settings/trackers
  - orkka-ui:packages/ui/src/components/settings/TrackerConnections.tsx
  - i18n:trackers
since: 2026-10-26
```
**Preconditions**: One healthy Jira connection and one failing ADO connection reported by the runner.
**Steps**:
1. Open `/settings/trackers` → "Ticket trackers".
**Expected**:
- Intro "Your Jira and Azure DevOps connections. Each one lives on the machine that added it — the token never leaves that machine, and only your runner talks to the tracker."; rows show kind, site, "as <account>", "on <machine>", "last seen <when>" and "Connected" / "Not working" with the health detail.
- With none: "No tracker connections yet. Add one below — your runner tests the token and keeps it on this machine.", the add form, and "Prefer the terminal? Add a connection with ./orkka" with the `./orkka tracker add …` commands and the token note.

## TSRC-023 Pick a board on the card; cleared when the project changes
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/settings
  - orkka-ui:packages/ui/src/components/project/TicketSourceCard.tsx
  - i18n:ticketSource
since: 2026-11-18
```
**Preconditions**: Runner online; the fake Jira has boards "Web board" (filter `project = SHOP AND component = Web ORDER BY Rank ASC`) and "Api board" on SHOP; the fake ADO has teams "Shop Team" and "Web" on Shop.
**Steps**:
1. Open Settings → "Ticket source" with the Jira connection; pick "Jira project" SHOP; open "Board".
2. Pick "Web board"; Preview; Save; reload.
3. Pick another project.
4. Switch to the ADO connection; pick Shop; open "Board".
**Expected**:
- "Board" is disabled until a project is picked; its options are "Whole project (every board)" (selected by default) then the project's boards by name; the hint reads "Only tickets on this board are picked up. Leave empty to use every board in the project."
- The preview's query gains `AND (project = SHOP AND component = Web)` (the filter's JQL without its ORDER BY); after reload "Web board" is still selected.
- Changing the project resets "Board" to "Whole project (every board)" and lists the new project's boards.
- ADO options are the teams, named "Shop Team board" and "Web board".

## TSRC-024 Save, read back and preview a board; clearing it means the whole project
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:PUT /api/projects/{projectId}/tracker
  - orkka-api:GET /api/projects/{projectId}/tracker
  - orkka-api:POST /api/trackers/connections/{connectionId}/query
since: 2026-11-18
```
**Preconditions**: Runner online with an ADO connection to the fake; team "Web" (area paths `Shop\Web` with children, `Shop\Shared` alone).
**Steps**:
1. `query {kind:"boards", scope:"Shop"}`.
2. `query {kind:"preview", scope:"Shop", boardId:<Web's id>, boardName:"Web"}`.
3. `PUT …/tracker {…, scope:"Shop", boardId:<Web's id>, boardName:"Web"}`; `GET …/tracker`.
4. `PUT …/tracker {…, boardId:null}`; `GET …/tracker`.
5. `query {kind:"preview", scope:"Shop", boardId:"no-such-team", boardName:"Gone"}`.
**Expected**:
- 1: `result.boards` lists the teams by name with their ids.
- 2: the effective WIQL contains `AND ([System.AreaPath] UNDER 'Shop\Web' OR [System.AreaPath] = 'Shop\Shared')`.
- 3: the source reads back `boardId` and `boardName:"Web"`; 4: both null.
- 5: `result.ok:false`, `error:"The board 'Gone' was not found in Shop — pick another board in the ticket source settings."`
**Notes**: a runner without the `tracker-boards` capability answers the boards query (and a preview with a board) with "Your runner does not know boards yet — update Orkka…". Covered at unit level by `tests/Orkka.IntegrationTests/TrackerBoardEndpointTests.cs`.

## TSRC-025 Connect GitHub Projects from Settings → Trackers: the gh login or a token
```yaml
priority: P0
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/settings/trackers
  - orkka-ui:packages/ui/src/components/settings/TrackerConnections.tsx
  - orkka-api:POST /api/trackers/connections
since: 2026-11-19
```
**Preconditions**: Runner online (capability `tracker-github`); the fake GitHub; the stub gh on the runner's PATH.
**Steps**:
1. Open Settings → Trackers → "Add GitHub Projects"; keep the host `github.com`, see the two options.
2. Type the fake's site as host, keep "Use my gh login"; Add.
3. Add it again with "Paste a token" and the fake's token.
4. Type `github.acme-corp.example` as host.
**Expected**:
- 1: the gh-login option is selected by default and explains it runs `gh auth token` and needs `gh auth refresh -h <host> -s project`; the token option shows the scopes (classic: project + repo; fine-grained: Projects + Issues read & write) and the SSO hint.
- 2: `POST /api/trackers/connections {kind:"github", site, token:"", authMode:"gh"}` → `{supported:true, ok:true, connection:{kind:"github", authMode:"gh"}}`; the row shows "uses your gh login"; nothing is stored on the machine but trackers.json metadata.
- 3: the same connection id, now `authMode:"token"`; the token is in the secret store only (never in the response, the audit log or the database).
- 4: the host is refused before anything is sent: GitHub Enterprise Server is not supported.

## TSRC-026 A GitHub source on the card: owner, project, status field, statuses, repositories
```yaml
priority: P0
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/settings
  - orkka-ui:packages/ui/src/components/project/TicketSourceCard.tsx
  - i18n:ticketSource
since: 2026-11-19
```
**Preconditions**: A GitHub connection to the fake; owner `e2e-local` with project "Shop roadmap" (Status: Todo / In Progress / In Review / Done; also a "Priority" single-select) and a closed project "Old board"; issues on it from `e2e-local/app` and `e2e-local/api`.
**Steps**:
1. Settings → "Ticket source" → pick the GitHub connection; pick the owner `e2e-local`.
2. Open the project picker; pick "Shop roadmap".
3. Open the status-field picker; keep "Status"; look at the pickup statuses and the six-step map.
4. In the repository filter pick `e2e-local/app`; Preview; Save; reload.
**Expected**:
- 1: owners are the developer (marked "(you)") then their organizations; epics, the Epics mode, the extra clause, include-descendants and component / area-path rules are not shown.
- 2: only open projects, by title; a project is required (no "whole owner" option) and saving without one is blocked.
- 3: the fields are the project's single-select fields; pickup defaults to "Todo"; Developing = "In Progress", In review = "In Review", Merged = "Done", the rest "Don't change".
- 4: the repositories are the ones whose issues are on the project; the preview counts only `e2e-local/app` issues assigned to me in Todo and shows `project:"Shop roadmap" is:issue is:open assignee:@me status:"Todo" repo:e2e-local/app`; after reload project, field and filter are still selected.

## TSRC-027 Save and read back a GitHub source; what does not apply is refused
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:PUT /api/projects/{projectId}/tracker
  - orkka-api:GET /api/projects/{projectId}/tracker
since: 2026-11-19
```
**Preconditions**: A GitHub connection reported by the runner.
**Steps**:
1. `PUT …/tracker {trackerConnectionId, scope:"e2e-local", boardId:<project id>, boardName:"Shop roadmap", statusField:"Status", repoFilter:["e2e-local/app"], intakeStatuses:["Todo"]}`; `GET …/tracker`.
2. The same without `boardId`.
3. With `scopeMode:"Epics"` and an epic; with a `component` routing rule; with `repoFilter:["not a repo"]`.
4. With `extraClause:"x"` and `includeDescendants:true`.
**Expected**:
- 1: reads back `boardId`, `boardName`, `statusField:"Status"`, `repoFilter:["e2e-local/app"]`.
- 2: 400 "Pick the GitHub project the issues are on."
- 3: 400 "GitHub Projects have no epics — an issue with sub-issues is worked as a Story…", 400 "GitHub issues are routed by label (or by their repository) — not by component.", 400 "The repository filter lists repositories as owner/name, at most 100 of them."
- 4: saved with `extraClause:null`, `includeDescendants:false`. A Jira/ADO source always reads back `statusField:null`, `repoFilter:[]`.
**Notes**: covered at unit level by `tests/Orkka.IntegrationTests/TrackerGitHubEndpointTests.cs` (save/read-back, project required, epics and non-label rules refused, repository filter shape, gh-login add needs no token).

## TSRC-028 GitHub queries via the runner: owners, projects, fields, statuses, repos, preview
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/trackers/connections/{connectionId}/query
since: 2026-11-19
```
**Preconditions**: As TSRC-026, through the API.
**Steps**:
1. `query {kind:"scopes"}`; `{kind:"boards", scope:"e2e-local"}`.
2. `{kind:"fields", scope:"e2e-local", boardId}`; `{kind:"statuses", scope:"e2e-local", boardId, statusField:"Status"}`; `{kind:"repos", scope:"e2e-local", boardId}`.
3. `{kind:"preview", scope:"e2e-local", boardId, statusField:"Status", intakeStatuses:["Todo"], repoFilter:["e2e-local/app"]}`.
4. `{kind:"statuses", …, statusField:"Nope"}`; `{kind:"boards", scope:"nobody"}`; `{kind:"preview", …, boardId:"PVT_gone", boardName:"Gone"}`.
**Expected**:
- 1: `scopes` = `e2e-dev` then `e2e-local`; `boards` = open projects with `id` (node id), `name`, `number`, `url`.
- 2: `fields` = Status and Priority with their options; `statuses` = Todo/In Progress/In Review/Done, `defaults.intakeStatuses:["Todo"]`, `defaults.statusMap {developing:"In Progress", inReview:"In Review", merged:"Done"}`; `repos` = `["e2e-local/api","e2e-local/app"]`.
- 3: `matchCount` = my open Todo issues from `e2e-local/app` (drafts and pull requests never count); `effectiveQuery` is the sentence form.
- 4: `ok:false` with "The project 'Shop roadmap' has no single-select field 'Nope' — pick the field that holds the status… (Status, Priority).", "GitHub has no user or organization 'nobody' that this account can see.", "The board 'Gone' was not found in e2e-local — pick another board in the ticket source settings."

## TSRC-029 A runner too old for GitHub is told to update
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/trackers/connections
  - orkka-api:POST /api/trackers/connections/{connectionId}/query
since: 2026-11-19
```
**Preconditions**: A runner whose Hello lacks the `tracker-github` capability.
**Steps**:
1. `POST /api/trackers/connections {kind:"github", …}`.
2. `query {kind:"fields", …}` / `{kind:"repos", …}` / a preview with `statusField` or `repoFilter`.
**Expected**:
- 1: `{supported:true, ok:false, error:"Your runner does not know GitHub Projects yet — update Orkka (`git pull`, then `./orkka start`)."}` — nothing is relayed.
- 2: `result {ok:false}` with the same message.
**Notes**: covered at unit level by `tests/Orkka.Tests/Features/Trackers/TrackerGitHubTests.cs` (RunnerRelay_RefusesGitHubWork_ToARunnerWithoutTheCapability) and `tests/Orkka.IntegrationTests/TrackerGitHubEndpointTests.cs` (Poll_HandsTheSourceOnlyToARunnerThatKnowsGitHub).
