# CLAUDE.md — Orkka

## Product Vision

Orkka is for **developers who work several jobs at once**. The tickets assigned to them in each employer's tracker (Jira, Azure DevOps) are worked by a persistent, named AI dev team — PM, devs, QA, reviewer — that runs on **the developer's own machine**, with their own git credentials and their own AI CLI subscription (Claude Code, Codex, Gemini). Orkka is the harness around it: the board, the pipeline (plan → code → test → review → merge), approvals, memory and history.

**Local-only (2026-11 pivot), run from source.** Everything runs on one machine and there is no account and no cloud. `git clone` + `./orkka start` (`orkka.cmd` on Windows) builds the clone with the developer's own .NET SDK and runs **one process** (`src/Orkka`, `dotnet orkka.dll`): the **Orkka.Local** host on 127.0.0.1:6750 (the API, the background engine, both SignalR hubs and the built static UI; its database is one SQLite file, `~/.orkka/data/orkka.db`) and the runner, which dials the host's `/hubs/runner` over loopback. Nothing is installed, supervised or added to PATH — see Hosts below and **LOCAL.md**. Orkka holds no forge credential: repos are any git remote (or a linked local folder) cloned and pushed with the developer's own git sign-in, reviewed work is published as a forge pull request by the developer's own CLI, and every task runs on its owner's runner (`TaskEntity.RunnerUserId`). Tracker tickets drive the board: the runner holds the Jira/ADO tokens, polls each project's ticket source and reports the tickets; new ones become tasks, changed ones update theirs, comments join the conversation, tickets that leave the source are retired, and ticket statuses follow the work. A workspace per job keeps employers apart — see Workspaces below. The cloud product (accounts, billing, members, Slack, webhooks, admin) is deleted; it survives in git history (tag `last-cloud`).

---

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Frontend | Vite + React Router single-page app (`ui/`), Tailwind, SignalR client — built to static files (`ui/apps/local/dist`) |
| Backend | C# / .NET 10, FastEndpoints, SignalR — one process: `src/Orkka` (host `src/Orkka.Local` + runner `src/Orkka.LocalRunner`) |
| ORM | Entity Framework Core (Microsoft.EntityFrameworkCore.Sqlite) |
| Agent SDK | Claude Agent SDK (.NET) — agents run inside repo worktrees |
| QA Testing | Playwright MCP — QA agent tests live apps in headless browser |
| Database | SQLite — one file, `{DataDir}/orkka.db` (WAL), opened only by the host; nothing to install |
| Infra | None — run from a git clone (`./orkka start`); NuGet/npm packages pinned by lock files; GitHub Actions runs CI only |
| Blob Storage | A directory (`{DataDir}/blobs`), HMAC-signed `/blobs/…` URLs |

---

## Workspaces & the local user

There is one implicit user, `local_user` (`LocalAuthHandler.LocalUserId`), and it is the **Owner** of every workspace. There is no sign-up, no invite and no other member.

- **Roles and permissions remain** (`OrgRole`, the `Permission` enum in SharedKernel/Security — stored as strings, never rename; `PermissionGuard`/`OwnerGuard`), and every guard passes for the Owner. They matter for **API keys**: an `orkka_key_…` carries a subset of permissions (never Owner) and an optional project binding.
- **Workspaces (one org per job).** Each employer or client gets an org and nothing is shared between them: projects, runner tokens, ticket sources. `org_members` holds one row per workspace for `local_user` (unique on `(OrgId, UserId)`); `OrgMember.ActivatedAt` marks the workspace last switched to.
  - A request names its workspace with the `X-Orkka-Workspace` header (`CurrentUserMiddleware.WorkspaceHeader`; the web UI sends the `orkka_ws` cookie's value, SignalR passes `?workspace=` and both `HubSecurity` files honour it): malformed → 400, not a member → 403. Without it the request lands in the workspace with the newest `ActivatedAt`. `CacheKeys.Membership(userId, workspace)` caches both.
  - `GET /api/me/workspaces`, `POST /api/workspaces` (creates one and makes it active), `POST /api/me/active-workspace` (404 when not a member), `PUT /api/org` (rename), `GET /api/org/me`, `PUT /api/org/me/preferences`.
  - The runner keeps one profile per workspace (`~/.orkka/workspaces/{name}/profile.env`): a running `./orkka start` writes one for every workspace (`RunnerProfileSync`, from the host's `/local/bootstrap`, re-polled so a workspace created in the UI gets its profile within a minute) and runs the runner in the same process (`RunnerHost`), which serves every profile (`RunnerSupervisor`, one `RunnerSession` per profile); `./orkka connect --workspace <name> --token …` remains the advanced, by-hand way to add one (a runner alone, e.g. on a second machine), and `ORKKA_MAX_CONCURRENT_RUNS` (default 2) caps runs across all of them. `ORKKA_HOME` relocates `~/.orkka` (tests must set it — USERPROFILE does not sandbox on Windows).
- **Notifications** are in-app only: a row in `organizations.notifications` plus a realtime push to the UI (`NotificationPublisher`).

---

## AI Agent Team System

Every project gets a persistent AI development team seeded on creation. Agents are named, specialized, and configurable. See **AGENTS.md** for full details.

| Agent | Default Name | Role | Repo Scope |
|-------|-------------|------|------------|
| PM | Luna | Refine tasks, ensure clarity | Cross-repo |
| Designer | Muse | Logos, icons, illustrations, visual assets | Cross-repo |
| Frontend Dev | Pixel | Next.js, React, Tailwind | Assigned to frontend repo |
| Backend Dev | Atlas | .NET, Go, Node — detects from repo | Assigned to backend repo |
| Full Stack Dev | Nova | Cross-stack tasks | Assigned to repo |
| QA | Scout | Write/run tests, validate criteria | Cross-repo |
| Code Reviewer | Sentinel | Review PRs, enforce standards | Cross-repo |
| DevOps | Forge | CI/CD, Docker, Helm, Terraform | Cross-repo |
| Solution Architect | Archie | System design, diagrams, scalability analysis | Cross-repo |
| Game Dev | Rune | Game programming — gameplay systems, engines, performance | Cross-repo (dev pipeline) |
| Game Designer | Sprite | Game assets — sprites, pixel art, tilesets, game UI (via ```design blocks) | Cross-repo |
| DBA | Sage | Database expertise — SQL/T-SQL, indexing, query performance (advisory, chat only) | Cross-repo |
| Legal | Lex | Project law advisor — legality questions, terms of use, privacy policy (advisory, chat only) | Cross-repo |
| Mobile Dev | Nomad | Android (Kotlin/Compose), iOS (Swift/SwiftUI), React Native/Flutter/MAUI/KMP | Cross-repo (dev pipeline) |

**All available agents are seeded with every project — always** (`AgentSeeder.CreateDefaultPod`; the singleton specialists GameDev/GameDesigner/DBA/Legal/MobileDev come from `AgentSeeder.Specialists`, one instance per project via a filtered unique index on `(ProjectId, Role)`). There is no add/enable/toggle flow — seeded agents are permanent (`IsDefault`) and always active; users choose which agents to actually use. `AgentRosterBackfillService` tops existing projects up at startup when a new specialist role ships. The DBA and Legal personas are advisory-only; the DBA persona itself states it has no database access and no credentials, and works from code and schema files.

**Key rules:**
- Users can rename agents, change models, and update instructions — but not add or disable them
- Dev agents are assigned to specific repos — no frontend agent for a Go backend repo
- Cross-repo agents (PM, QA, Reviewer, Architect) work across all repos
- Agents are only created if the pipeline has their corresponding lane (no Reviewer if code review is skipped)
- Designer (Muse) creates assets from Team chat via ```design blocks or the `create_design_assets` tool — always SVG the agent authors itself on the runner. Orkka has no server-side image generation (no raster images or GIFs; the image providers, their keys and the `ImagesConfigured` flag are gone). Assets land on the project's Design page for approve/reject/iterate; approved assets are injected into Frontend/FullStack dev-agent task context as signed download URLs. Muse never commits to repos
- Solution Architect (Archie) is NOT part of the pipeline — available anytime via Agent Chat for architecture analysis and Excalidraw diagrams
- There is one kind of project, so one persona per role (`DefaultPrompts.ForRole(role)`) — no per-project-type variants

**Multi-agent flow:** PM refines → Designer creates assets (if needed) → Dev plans & codes → QA tests → Reviewer reviews → Done.
**Non-pipeline agents:** Solution Architect (Archie) — accessible anytime via Agent Chat on the teams page.

---

## Pipeline Lanes

| Lane | Agent | Description |
|------|-------|-------------|
| **Drafting** | — | Human writes the task |
| **Ready** | PM | PM reviews for completeness, asks questions |
| **Planning** | Dev | Dev agent creates execution plan |
| **Plan Ready** | — | Human approves plan |
| **Pending Questions** | Current | Agent needs clarification |
| **Developing** | Dev | Dev agent writes code in worktree |
| **Testing** | QA | QA tests live app via Playwright MCP browser, writes E2E tests |
| **Code Review** | Reviewer | Reviews diff, leaves line comments |
| **Done** | — | Landed: squash-merged into a linked folder, or published as a forge PR |

Lanes are configurable per project. Tasks can move backward.

---

## Automation & Audit

- **Project memory** (`/p/{id}/memory`) — `agents.project_memories`: project-scoped (optional agent = private note), typed (Decision/Convention/Preference = standing rules; Learning/Mistake/Pattern = experience), pinned/archived, use-counted. Retrieved by **relevance** for every run and chat turn (`MemoryRetriever`: pinned + rules → SQLite FTS5 (bm25) rank on the task/message → agent's recent learnings, under a char budget; rendered by `MemoryPromptSection`), never "newest N". Written by agents (`remember`/`recall` tools in chat + pipeline; deduped per-task outcome note after each run), by humans (Memory page: add/edit/pin/archive/delete + "What would an agent see?" preview), audited as `MemoryChanged`.
- **Task chat ("Discuss")** — the task detail's Discuss tab: an immediate chat with the task's agent about the ticket, repo and plan (`GET/POST /api/tasks/{id}/chat`, a conversation per (task, agent) via `agent_conversations.TaskId`), read-only on the task branch in the task's own runner workspace; "Apply to task" (`POST …/chat/apply`) posts a reply or summary as the user's "From discussion:" message — the answer in PendingQuestions, a plan change request in PlanReady. See AGENTS.md → Task chat.
- **Skills** — per-project `SKILL.md` library (`/p/{id}/skills`), assigned to agents on the Team page, injected on disk with a short prompt index; `/<slug>` in chat runs one as a playbook. See AGENTS.md → Skills.
- **Automation** (`/p/{id}/automation`) — cron playbooks, autopilot windows, agent reminders, and the project **Autonomy** switch (`AutomationEnabled`). Runs in Orkka.Local (`ScheduleProcessor`; one process, no leader election).
- **Ops views** — Team page per-agent stats (`GET /api/projects/{id}/agent-stats?window=`) and the Activity timeline (`GET /api/projects/{id}/executions`).
- **MCP + API keys** — `/mcp` (Streamable HTTP, `ModelContextProtocol.AspNetCore`) exposes board operations to Claude Code / Cursor / scripts; auth is a personal `orkka_key_…` (`OrgApiKeyAuthHandler`, the key's permission subset, never Owner, optional project binding). Board ops live on `ITasksApi` (`BoardOperations`) so HTTP and MCP share one implementation.
- **Audit log** — `agents.agent_audit_events` is a per-org SHA-256 hash chain (`AuditHash`, `PreviousHash`/`EntryHash`, per-org in-process lock in `AgentAuditLog`), with `ActorKind`/`ActorUserId`/`MetadataJson`; management endpoints (API keys, settings, agents, skills, schedules, memory, tracker config, MCP writes) emit events. `GET /api/audit` (filters + cursor, `AuditView`); the UI has no audit page.

---

## Usage

Runs use the developer's own AI subscription; Orkka records token usage for visibility and bills nothing. Every agent run, chat turn and utility completion writes a row to `usage.usage_records` (`IUsageRecorder`, Usage module): tokens per bucket (input, output, cache read, cache write), `CostTotal` (list price from the model catalog via `CostCalculator`, or 0 — every run on the runner records `FlatCostBase: 0`), `VendorReportedCostUsd` when the CLI reports one, and `UserId` (who asked for the work). Reads: `GET /api/usage/summary`, `/api/usage/daily`, `/api/usage/breakdown?groupBy=project|agent|model|task`; per-task and per-execution figures come through `IUsageQuery` in the Tasks endpoints. The model catalog (`agents.ai_models`, seeded at startup by `AiModelSeeder` from `ModelCatalogSeed`) feeds the model pickers; `CostCalculator.BaseRates` is its fallback.

**Spend limits are in US dollars of ESTIMATED LIST-PRICE spend** (`IUsageQuery.GetTaskEstimatedSpendAsync`: each row at its `VendorReportedCostUsd`, else the model's list price for its tokens — never `CostTotal`, which is 0 for runner work). There are no credits anywhere. The opt-in per-task limit (`ProjectSettingsDto.TaskSpendLimitEnabled` + `TaskSpendLimitUsd`, default $5, $0.10–$10,000) is checked before every pipeline phase starts (`TaskOrchestrator.PauseIfSpendLimitReachedAsync`): at or above the allowance (`TaskEntity.SpendApprovedUsd` after a Continue) the task parks at a spend checkpoint (`spendCheckpoint {spentUsd, limitUsd, allotmentUsd, phase}`; `POST /api/tasks/{id}/spend/continue|stop`). The autopilot session cap (`CapUsd`) measures the same estimate. Real money for an API-key or Bedrock user, an API-equivalent estimate (moot, off by default) on a subscription. Not mid-run: the runner protocol carries no per-run ceiling (`PhaseBudgets:*:MaxCostUsd` only reaches in-process runs).

**Customer AI never runs on an Orkka key.** Agent runs, every chat turn (`HybridChatAgentInvoker` relays each one to the requester's runner — an offline runner or a turn with no requester gets an error chunk naming `./orkka start`), AND every one-shot utility completion around them — task decomposition, dependency analysis, cross-repo split, run-config detection, documentation sync, chat titles/summaries, interactive-session titles, the prose-question check, API-surface extraction, merge-conflict summaries, the design SVG fallback — run on the developer's runner through `IUserRunnerUtility` (`RunnerLinkUtility`, in-process over the runner link). A runner miss degrades the feature with an actionable reason — `RunnerUtilityUnavailableException.MessageFor(purpose)`, "Your runner is offline or unavailable — run `./orkka start` to …" (plan jobs fail with `MilestonePlanFailureCodes.RunnerOffline`, run-config detection with a `RunConfigDetectionException`, a cross-repo split abandons with a thread note, doc sync is recorded Failed and retried; titles, summaries and other housekeeping are skipped). An architecture test fails if any module takes an Anthropic HTTP client. There is no server-side image generation.

---

## Projects

There is one kind of project: a board plus the developer's own repositories — any git remote (https, ssh, scp-style, ssh host aliases; `ProjectRepo.ForgeKind` + `CliAccount` say which forge CLI and account publish its PRs) or a folder on their machine linked through their runner, all reached only through the developer's runner with their own git credentials. Orkka never creates, scaffolds, hosts or deploys the code; any stack works, each repo's toolchain is detected from its manifests (or declared in its `orkka.json`), every project is seeded with the full agent roster, and the repo's own CLAUDE.md is read by agents but never generated. See **PROJECTS.md**.

**Base folders (P5).** A project is normally created from ONE folder on the developer's machine (`POST /api/projects/from-folder`): a monorepo (the folder is the repo) or a folder of repos (found to depth 2 by the runner op `DiscoverRepos`, read-only, capability `repo-discovery`). `Project.BaseFolder`/`BaseFolderShape`; `ProjectRepo.FolderPath` (relative, `""` for a monorepo), `Description` (routing hint from `orkka.json`/README, editable) and `MissingSince`. A repo with an origin is registered as a remote (`GitUrl` = origin → branch push + forge PR), one without as a local folder (squash merge). `POST /api/projects/{id}/rescan` adds, marks missing, restores and follows a changed origin (never while a task on it is in flight); the UI fires `?auto=true` on open (10-minute debounce). The mirror is seeded from the developer's checkout, task folders stay under `~/.orkka`, and agents on a folder-of-repos project get the base folder as `ReadOnlyDirs` (Claude/Copilot `--add-dir`) plus a "Sibling repositories (read-only reference)" prompt section — never their own repo's checkout, never a `.env` outside the worktree.

### Ticket source (Jira Cloud / Azure DevOps Services)

- **Tokens never leave the developer's machine.** A connection is added in Settings → Trackers or with `./orkka tracker add jira|ado` (hidden prompt or stdin); either way `TrackerConnectionManager` on the runner tests the token and stores it in the OS secret store (Windows Credential Manager, macOS Keychain, libsecret; an owner-only file as the fallback); non-secret settings live in `~/.orkka/workspaces/{ws}/trackers.json`. From the UI the token travels write-only, browser → `POST /api/trackers/connections` → loopback relay → the caller's own runner (`AddTrackerConnection`, capability `tracker-connect`) — the one host→runner field carrying a credential, never stored, logged, echoed or audited by the host (SECURITY.md); `DELETE /api/trackers/connections/{id}` removes it there. The runner reports connection **metadata** in every Hello (`RunnerHello.Trackers`, plus a fresh Hello right after a UI add/remove), upserted into `trackers.connections` (`ITrackersApi.UpsertRunnerConnectionsAsync`, per machine — one removed on a machine is marked missing, never deleted).
- **A project's ticket source** (`trackers.project_configs` + `epic_mappings`, one per project): one of the *owner's* connections, a scope (Jira project / ADO project), an optional **board** of that scope (`BoardId`/`BoardName`: a Jira agile board — its filter's JQL, `ORDER BY` stripped, is AND-ed onto intake — or an ADO team — its `teamfieldvalues` area paths become an `UNDER`/`=` WIQL condition; resolved live on every poll and preview, intake only like the extra clause; a board that is gone fails the poll with `LastPollError`, never widens to the whole project; a runner without `TrackerSyncRequest.Features` "boards" is not given a board-limited source; TRACKERS.md → Boards), the **intake statuses** (default `["To Do"]`; ADO: the Proposed states), the **scope mode** (`All` = every ticket assigned to the owner in an intake status, or `Epics` = only under the epic rules), the rules (`TrackerEpicMapping.Kind` epic | component | label | areaPath → repo + optional monorepo subfolder `TrackerSubpath`; epics first, then the rest, first match wins), an optional default repository, an optional extra JQL/WIQL clause, include-descendants, auto-submit (default on), poll interval (60–300s), the status map (six optional steps — Picked up / Planning / Developing / In review / PR opened / Merged, any tracker status each; old `started` JSON reads as Planning — pre-filled from the scope's statuses, `TrackerStatusDefaults`: Planning = In Progress-ish, PR opened = Review-ish / ADO Resolved, the rest unmapped) and the branch template (must contain `{key}`). `story_mode` (default on) turns on Stories (below). `PUT /api/projects/{id}/tracker` (ProjectsManage, audited `TrackerConfigChanged`) makes the caller the owner and requires the connection to be theirs.
- **Polling is the runner's; ingestion is the host's.** Every 30 s the runner calls `SyncTrackers`; the host returns the owner's due sources under a **poll lease** (`LeaseHolder` = the runner's InstanceId, a conditional update; the owner's active connection takes over a lease its replaced predecessor held — `supersedesOtherInstances`) with the **tracked** tickets (`TrackedLinksAsync`: links whose task is not archived, not synced Merged and under 30 days past PR opened — whoever they are assigned to now; the rest get `TicketLink.TrackingEndedAt`). Each poll runs two reads (runner protocol v5): **intake** — assigned to me, in an intake status, in scope (+ the board, + epics in Epics mode, + the extra clause; containers excluded) — and **tracking** — the tracked keys whatever their status (Jira `key in (…)` 100 at a time, a key Jira says does not exist is dropped and the batch retried; ADO work items by id, `errorPolicy=Omit`) plus the children of intake tickets. Every `TrackerTicket` carries `Status`/`StatusCategory`, `AssignedToMe`, `InIntake`, `ParentKey`/`ParentType` and ordered `ChildKeys` (Jira `subtasks`; ADO Hierarchy-Forward ordered by StackRank/BacklogPriority, then id). `ReportAsync`: only an **intake** ticket assigned to me becomes a task (`CreateTrackerTaskAsync`, idempotent on `ExternalRef`) and a `ticket_links` row (`ParentKey`, `Role` Standalone / Story / SubTask); routing without epics: rules → the project's only repo → the default repo → the **repo router** (`IRepoRouter`, a utility on the owner's runner: `{repoIds, confidence, reason}` — one sure repo is set, two or more make a cross-repo group split on submit) → otherwise no repo and the task asks (`AskTrackerRepoAsync`: a questions block listing the repos, PendingQuestions with source lane Drafting; the answer — `BoardOperations.AnswerRepoQuestionAsync` — sets the repo and submits when the source auto-submits). A tracked ticket keeps syncing text, status and comments (moving To Do → In Progress changes nothing else); it is retired (`RetireTrackerTaskAsync`) only when reassigned (archived if nothing started, else a note and tracking continues), moved to a done category before work started, or missing from a successful poll; a retired ticket that returns to intake is revived, a task a person archived is left alone. A failed poll records `LastPollError` and changes nothing else. The prompt names the ticket, epic, folder and `.orkka/context/attachments/`. The Bash guard refuses `gh auth token`, `az account get-access-token`, `git credential fill`, `~/.ssh` and `~/.orkka/workspaces` on every run.
- **Status write-back is the only thing Orkka writes to a tracker** (`TrackersApi.WriteBack`). On each `SyncTrackers` the host compares how far every tracked task has got (`GetTrackerTaskStatesAsync` → PickedUp / Planning / Developing / InReview / PrOpened / Merged — PR opened = published or landed in a linked folder, merged = the forge PR merged per the developer's CLI; Story links read `GetTrackerGroupStatesAsync`) with `TicketLink.SyncedPhase` and hands the runner the furthest *mapped* move ahead (unmapped steps are skipped, so a folder landing with Merged unmapped moves the ticket to the PR-opened status, i.e. Code Review; moves never go backwards; reassigned tickets whose work continues still move). Once per 2-minute window, applied with the developer's token, acknowledged with `AckTransitions` (`SyncedAt` stamped); five failures give up with a note and `ExternalSyncError`. Published forge PRs are looked up with the developer's CLI (`TrackerPrCheck`, ≤ every 10 minutes, only when "Merged" is mapped; the repo is the task's current one).
- **Stories — one branch, one pull request** (TRACKERS.md → Stories). A ticket assigned to me, in intake, with open sub-tasks **assigned to me that have not been started** (`TrackersApi.IsMemberCandidate`: `AssignedToMe` and status category `todo` or an intake status — Orkka only works tickets assigned to the developer; teammates' sub-tasks, unassigned ones and ones in progress / resolved / in test are never members, never waited for, and a ticket with none of mine is a plain task) becomes a `TaskGroup` with `Kind = Story` (`branch_name`, `external_key/url`, `ready_at`; `tasks.group_branches` per repo: branch, forge PR, draft, ready, forge state) whose members are those sub-tasks in tracker order (`story_order`, `BranchName` = the story's branch, each depending on the previous; met at Completed, an archived blocker counts as met and an archived member's dependents are rewired to its predecessor; never planned ahead of a milestone gate). Each member records `review_base_sha` at worktree creation and is reviewed on its own change: `review_base_sha..HEAD`, or — once a base sync merged the default branch into the shared branch — the `git merge-tree --write-tree` of `review_base_sha` and the synced default-branch commit vs HEAD (`GitWorkspaceManager.ReviewDiffBaseAsync`), so the default branch's commits never show as the member's (`CreateForTaskAsync` with an explicit task id and `base_sha` — branch → task lookups prefer the in-flight member). A reset member keeps the shared branch, drops its review base and re-plans on top of the branch as it is (its earlier commits stay; the thread note gives their range; nothing is ever force-pushed). A routing answer naming several repositories makes one member per repository in story order, like intake. Its prompts (and its Discuss chat, via `ITaskStoryContextSource`) carry the Story context section. Landing (`StoryLandingService`): the member's local PR is Merged on the shared branch; the first opens the forge PR as a **draft**, the last (nothing left, nothing pending) checks the shared branch against the default branch of EVERY repository of the story (any conflict: `StoryLandingErrors`, all PRs stay drafts, and the resolver runs per conflicting repository in the clone of its last member), and only when all are clean marks every repo's PR **ready** and sets `ready_at` — the Story and every sub-task then move to PR opened together; a linked folder is squash-merged at the end (again, if a late sub-task joined it). Late sub-tasks — and ones assigned to me later — join the end (a ready PR goes back to draft), or after the PR merged/closed become plain tasks with the Story as parent context. A member whose sub-task stops being mine (reassigned, or under way in the tracker while Orkka never moved it) is cleaned up every poll: not started → archived, taken out of the group (`ReleaseStoryMemberAsync`, dependents rewired), its link ends and it rejoins if it becomes mine again; started → left running with one note; no member left → the group is cancelled and the Story ticket becomes a plain task. Runner capability `stories` (draft/ready/body ops, `ResolveReviewBase`, `GetDiffRequest.BaseSha` and `GetWorkingTreeDiffRequest.BaseSha` — the live Code View diff of a running member is its own change too).
- **Everything the settings page shows comes from the owner's runner.** `POST /api/trackers/connections/{id}/query` (owner only) relays a `TrackerQuery` — test, scopes, boards (Jira boards / ADO teams, capability `tracker-boards`), epics, statuses (+ `defaults`: the status map and intake to pre-fill), preview of the intake query — through the loopback relay (`/internal/runner/tracker-query`, capability `tracker-config`). Runner offline → `Supported: false`, never an error.

---

## Repository Management

All git happens on the **developer's runner** (run by `./orkka start`): a mirror per repository plus a STANDALONE clone per task, under `~/.orkka/repos` on their machine. Agents run via Claude Agent SDK with `Cwd` set to the task clone and a fully custom system prompt (`--setting-sources user` on every run — vendored SDK patch 4 — so nothing under `.claude/` in the customer repo is loaded: no committed hooks, env, MCP servers or permissions — the prompts tell the agent to read CLAUDE.md, and Orkka's own guidance arrives as the system prompt plus `.orkka/context/` files).

```
~/.orkka/repos/{org_id}/{repo_id}/     (on the developer's machine)
  bare/              ← bare mirror: object cache for clones + read-only ops (ls-remote, merge-tree)
  worktrees/
    {KEY-n}/         ← standalone clone per task (own .git and refs; origin = the remote or folder)
  merge-scratch/     ← throwaway clones for local-folder squash merges
```

- **Why clones, not linked worktrees:** every worktree of a repo shared the mirror's ref store, so N tasks, chat worktrees, GC and the agent's own git all raced on one set of refs ("cannot lock ref", "invalid reference origin/…", locked worktrees). A clone owns its refs (`--reference-if-able bare --dissociate` makes it near-instant; `--filter=blob:none` against a remote), fetches on its own, and can be deleted and rebuilt from the remote at any moment. Legacy linked worktrees (a `.git` FILE) are still reused and removed correctly.
- **Developer git, no platform credential.** Orkka holds no forge credential: no GitHub App, no installation, no webhooks, no stored token. Every clone, fetch and push runs on the runner with the developer's own sign-in (credential manager / `gh` login for https, keys and `~/.ssh/config` aliases for ssh). A clone that fails tells them to sign git in on that machine (`RepoAccessDiagnostics`); `GET /api/projects/{id}/repos/access-check` asks the runner up front.
- **Every task forks from, and opens its pull request against, the repo's `DefaultBranch`.** `TaskOrchestrator` records it as the task's base (`SetTaskBaseBranchAsync`) when the worktree is created and `WorkspaceResult.BaseBranch` carries it to every diff, base-sync, PR and review in the phase, so a later change of the default branch cannot retarget open work. Orkka creates no branches of its own on anyone's forge beyond the task branch: `orkka/{KEY}-{n}`, or for a tracker ticket its source's branch template (`TrackerBranchName.Render`, default `{type}/{key}-{slug}` → `feature/SHOP-12-add-invoice-export`, sanitized to a valid ref) so Jira's development panel and ADO link the branch and its PR to the ticket. Branch → task lookups go through the persisted `TaskEntity.BranchName`, never a prefix. A base that cannot be fetched or a merge that cannot be verified stops the phase with a `GitErrorCode` — never a quiet fallback.
- **The platform pushes, never the agent.** After every run the runner pushes the task branch (`EnsureBranchPushedAfterRunAsync`, every route); for a local folder the worktree's origin IS the folder, so the push is a ref update into it. A phase never completes on a failed push.
- **Pull requests are Orkka's own** (`IPullRequestService` / `OrkkaPullRequestService`, `sourcecontrol.local_pull_requests` + reviews/comments, `orkka-local://pull/N` URLs) for EVERY repo, keyed by repo id. Diff, line comments and approve all run against that row plus the runner. Title = the task title, no prefix or footer. **"Merge"** (`MergeAsync`) means:
  - **Local folder:** squash-merge into the developer's folder (below).
  - **Remote: PUBLISH.** The runner pushes and opens a forge pull request with the developer's own CLI (`gh pr create` / `az repos pr create` / `glab mr create`, picked by `ProjectRepo.ForgeKind`; idempotent per head branch; `GH_TOKEN` scoped to `ProjectRepo.CliAccount` when set). Orkka never merges on a forge — the developer's team reviews and merges that PR. Its URL lands on `LocalPullRequest.ForgeUrl` (+ `ForgeState`/`ForgeCheckedAt`) and on the task as `TaskEntity.PublishedPrUrl`. Needs the runner capability `forge-cli`.
- **The merge sweep** (`TaskQueueProcessor.MergeUnmergedPullRequestsAsync`) lands approved work through `IPullRequestService.MergeAsync` (merge or publish) and re-pushes a missing head branch from the worktree once before giving up.
- **Local folders (linked through the user's runner):** Orkka never merges inside the user's working copy. A task's merge lands through a scratch clone and a push into the user's repository with `receive.denyCurrentBranch=updateInstead` passed for that one push (`--receive-pack`, never written to the user's `.git/config`): a ref update when the base is not checked out, a fast-forward of the working tree when it is checked out and clean, a refusal with the reason when it is dirty. One merge at a time per folder, and a task's own run owns its merge: the runner serializes squash merges per folder, and while the conflict resolver is in flight for a task the merge sweep leaves that task and its repository alone for the cycle — the resolver's push racing the sweep's is how a merged task ended up carrying a NonFastForward "failure" (prod, 2026-09-16). A local pull request whose mergeability the runner cannot answer reports it as unknown (null), never clean: a clean reading clears conflict notices. A resolver run the runner cut off is recorded EnvironmentBlocked and does not spend the task's one automatic resolution attempt (prod, 2026-09-17). A folder can be given a remote the user created on their own forge (`attach-remote`) and pushed to it — on the runner, with their credentials.
- **Browsing a repo (Repo page: tree, files, commits, branches, commit diff):** `RepoReaders.Current` relays every read to the CURRENT user's runner through `ILocalRepoReader` (`RelayLocalRepoReader` → the loopback relay `/internal/runner/repo/read` → `RepoReadRelay` → the runner's browse ops) whenever a relay is configured and a user is signed in; branch writes refuse ("managed in your own git"). There is no platform reader and no platform copy of anyone's code: with no runner to ask, every read fails with "your runner is offline". Prompt context built from a repo (`IRepoContextBuilder`, for planning and the cross-repo split) reads the same way, on the requester's runner, and is simply absent without one. Pipeline runs read through their route (`ExecutionRoute.Repo` = `RunnerGitRepoReader`).
- **Cross-repo groups land all-or-nothing — in the cycle, too.** A Gated `TaskGroup` holds every member's PR until `ITasksApi.GetGroupMergeStatesAsync` says the group is ready (every half finished, none stuck on a terminal failure or a closed PR), then the sweep lands them provider-first in one cycle. Readiness reads the siblings' STATE; it cannot see that the half ahead is failing to land right now on the retry path (a dirty working copy in the user's folder, prod 2026-09-16, StoreFront #15), so the sweep also holds a member until every sibling ahead of it in the merge order landed this cycle. A half-landed group is otherwise exactly the state the gate exists to prevent.
- **One git engine (`src/Orkka.Git`).** Every process spawns git through `GitProcess` only, so prompts are off, there is a deadline, logs are redacted and every operation lands in the `sourcecontrol.git_operations` ledger. No credential-helper clearing and no forced identity — commits are the developer's (the workspace's identity, `./orkka workspace set-identity <name> --name … --email …` → `ORKKA_GIT_AUTHOR_NAME/EMAIL` in its `profile.env` → `Git:CommitAuthor*`, for clones under `~/.orkka/repos` that an `includeIf "gitdir:…"` rule never matches; else their git config). Git only runs on the runner — the host clones, fetches and commits nothing itself — so there is no platform commit identity. No git, forge or AI credential travels host → runner (the one tracker exception, a token the developer typed into Settings → Trackers, is write-only — see Ticket source): the runner protocol carries no git token (`EnsureBareClone`, `SyncBaseBranch`, `CommitAndPush`, `EnsurePushed`, `PushToRemote`, `AttachRemote`, `CheckRepoAccess` have no token field, `GitCommand` has no credential), and agent/chat/utility run requests carry no provider API key — every CLI uses the developer's own sign-in, and `AgentEnvironment` still sanitizes the agent's environment. Runner protocol v5 (v4 added `developer-git`; v5 is tracker intake v2): runners must advertise `developer-git` or the host refuses git work with "update Orkka"; the Hello also reports `ForgeClis` (gh/az/glab installed, signed in, accounts → `RunnerPresence.ForgeClisJson`, shown on `GET /api/me/runner`) and the repository commands it refused and nobody approved yet (`PendingCommandApprovals`, newest 20 → `RunnerPresence.PendingCommandApprovalsJson` → `pendingCommandApprovals` on `GET /api/me/runner`; approving stays `./orkka commands approve <id>` on that machine).

**Milestones ship as they complete.** There are no integration branches, no ship sweep, no ship-fix tasks and no retry-ship endpoint. Every milestone is `IntegrationMode = Trunk`: its tasks land one by one, and `MilestoneProgressUpdater.TransitionAsync` writes `Status = Completed` and `ShipState = Shipped` in the same update, so a milestone dependency never stalls on a ship. (`MilestoneIntegrationMode.Branch` and the `MilestoneShipped`/`MilestoneShipFailed` notification types remain only so old rows load.)

- **The planner sizes the first milestone small.** On-demand task generation for a milestone
  (`TaskDecomposerService.AppendRoadmapContextAsync`) shows the planner the whole roadmap, tells
  it to plan only the marked milestone, and bounds milestone 1 to two or three tasks — except
  when the roadmap is a single milestone, which is the whole product and is planned in full.
- **Plan ahead across a milestone gate, never build.** Once every milestone a milestone waits on is past planning (all its tasks building, in review or merging), autopilot submits the waiting milestone's tasks to be PLANNED (`MilestoneDependencyQueries.PlanAheadAcrossGate`). The queue runs those plans in the plan-ahead slots and holds the approved plan at Developing until the gate opens (`MayBePickedUp`); held plans count as banked plans, so the depth bounds how many are written early, and with a depth of 0 nothing plans ahead. A plan written slightly ahead of its base is reconciled by the base sync at Developing entry. An autopilot session whose Drafting tasks wait on a milestone still landing holds instead of draining (`ITasksApi.HasMilestoneWorkInFlightAsync`).

**In-app features:** File tree browser via `git ls-tree`, file viewer via `git show`, PR diff viewer, line-level comments, approve/merge — all inside Orkka against Orkka's own PR record, read through the runner; the forge only sees the published PR.

**Open in editor** (`GET/POST /api/tasks/{taskId}/editors|open-in-editor`): hands a task's worktree to a desktop editor on the machine that ran it. The endpoint forwards through the loopback relay (`/internal/runner/editors`, `/internal/runner/open-in-editor`, behind the per-process `X-Worker-Key`), located via `IRunnerRelayLocator`. One rule, in `TaskEditorAccess.ResolveAsync`: a task opens only for its runner's own owner — the runner is resolved from the *task's* `RunnerUserId` and checked against the caller, never taken from the request, so nobody can launch a process on a colleague's machine. Every unsupported case collapses to `supported: false` and the button simply doesn't render.

**Worktree setup command** (`ProjectRepo.SetupCommand`, per repo — worktrees are per repo, so a Next.js frontend and a .NET backend in one project need different commands): a shell command run once per task worktree at creation, before any agent, so the app can actually start. Without it a fresh worktree has no dependencies and no env, which is why the QA lane's `QaRunCommand` fails and why a developer opening the worktree can't run what the agent wrote.

- Its sibling `ProjectRepo.QaRunCommand` is how the QA agent starts *that repo's* app. The QA lane reads repo-first and falls back to `ProjectSettings.QaRunCommand`, so projects configured before the per-repo field keep working with no data migration — but a multi-repo project can now describe how to start each of its apps, which the project-level setting never could.
- `ProjectRepo.BuildCommand` proves a merge still compiles (run only on the conflict-resolution path). `ProjectRepo.CheckCommand` is the **quality gate**: lint + typecheck + tests (e.g. `npm run lint && npx tsc --noEmit && npm test`), run by the platform in the task worktree after every Developing run, before the PR exists. A non-zero exit posts the output to the task thread and sends the task back to the dev agent (`Worker:MaxCheckCycles`, default 2, then `WaitingUserVerification`); "could not run" never blocks. Without it, "the tests pass" is the agent's own word.
- Runs once: a `.orkka/setup` marker records the command hash + exit code, so re-entered phases skip. Re-runs when the command text changed or the last attempt failed — a failure is never cached as done.
- Failure is non-fatal except in **Testing**, where it escalates to human verification *before* starting the QA agent — the same reasoning already applied to a missing run command ("cheaper to escalate before spending tokens").
- **Where the commands come from.** The repo's settings, or — when a setting is empty — the repository's own `orkka.json` on its default branch (`setup` / `check` / `build`, read through the runner by `ReadManifestCommandsAsync`; `OrkkaManifest.TryReadCommands`).
- **The developer's machine decides whether it runs** (`CommandTrust` on the runner, capability `command-trust`; the host sends no command to a runner without it). A command runs only when the repository's `orkka.json` on its default branch declares it (a task branch never counts — an agent wrote it) or the developer approved that exact command for that repository on that machine (`./orkka commands approve <id>`, stored in `~/.orkka/command-approvals.json`, which nothing on the wire can write). Otherwise the runner refuses with a `CommandApproval`, the orchestrator posts its message to the task thread (`NoteCommandAwaitingApprovalAsync`), and the phase carries on as if the command could not run. Not metered: it runs on the developer's machine.
- **Never a place for secrets.** On their own machine a user writes `cp ../../.env .` and nothing leaves their disk. A platform-managed env-var bag for worktrees would recreate exactly the leak `GeminiDotenvGuard` was built to stop, and would put customer secrets in Orkka's database besides.

**Cloning a repo locally** (`POST /api/projects/{projectId}/repos/{repoId}/clone-access`, gated on `ReposManage`): returns the plain `git clone <url>`. Every repo lives on the customer's own forge, so their developers' existing git credentials already work — Orkka never mints or hands out a credential here. A linked local folder is refused ("open it directly").

**Run the project locally** (the Run button in the floating dock, relayed to the caller's own runner): starts the project's own apps on the user's machine, in their real working copy, streams the output back as a terminal, and hands them the address as a **"Live app" link** (`Ready` once the port answers — "starting…" until then).

- **The repo's `orkka.json` is the contract; detection is the fallback.** `orkka.json` is THE Orkka file in a repo (`OrkkaManifest`): dev agents add it to a repo that lacks one on their next task. Its `stack` (a `RepoStacks` name) beats manifest sniffing (`RepoStackClassifier`); it is descriptive only — every task runs on the developer's machine with whatever toolchain it has. A root `orkka.json` with a `run.targets` list (`RunManifest`) is read as-is — no model, no tokens, `RunConfigSource.Manifest`, re-read for free whenever it changes (`GetAsync` does it inline; such projects never show a Detect button). Without one, `IRunConfigDetector` reads every repo's file tree plus its manifests, asks the utility model, and stores the answer in `projects.project_run_configs` as **run targets** (key, label, kind, repo, command, `installCommand`, `stopCommand`, working directory, port, `dependsOn`, `autoSelect`, `restartOnMerge`, `previewPath`). A mixed project keeps manifest repos authoritative and lets the model fill the rest.
- **Kept runnable.** Dev agents get `RunnableContract.DevAgentSection` in every developing prompt so `orkka.json`, the compose file and `.env.example` stay true as the app grows. Infra in compose, apps native — a native dev server hot-reloads, which is what makes merges visible live.
- **Staleness is a hint, never an action** (for AI-sourced configs). The stored config carries a SHA-256 fingerprint of the manifest *contents* it was derived from. A mismatch marks it stale and offers a re-detect; it never triggers one. `PUT /api/projects/{id}/run-config` (gated on `ReposManage`) lets the user correct a command by hand and re-fingerprints.
- **Targets are per project, not per repo** — unlike `ProjectRepo.SetupCommand`. Running an app is a cross-repo act; `dependsOn` orders them, and dependents wait (bounded) for a dependency's declared **port** to accept connections before they spawn. When the repo has no `SetupCommand`/`QaRunCommand`, the worktree setup and the QA lane derive theirs from these targets (`QaRunCommandDerivation`).
- **Never a place for secrets.** A target says how to *start* the app; the `.env` it needs is already in the working copy — and when it is missing, the runner's **prepare step** (`AppRunPreparer`) writes it from `.env.example` (local defaults the repo's own author wrote), runs `installCommand` when dependencies are evidently absent (a JS app with no `node_modules`, or one whose `package.json` declares a package that is not installed — a merged task added it and nobody ran the install in the user's folder, prod 2026-09-16), adds a dependency's native build for this machine with `npm install --no-save` when a lockfile written on another OS left it out (next dev's "Turbopack is not supported on this platform … native bindings are not available", prod 2026-09-16), and refuses `infra`/`docker …` targets up front with "Docker is not running" instead of a compose pipe error. Everything it does is announced in the target's own log.
- **Processes outlive the browser.** `AppRunManager` (runner) always drains its own PTYs and streams are mere subscribers. `AppRunOrchestrator` (host, singleton) keeps the run state and a bounded scrollback across hub connections, `ReconcileAsync` adopts processes still running after a host restart, and a non-zero exit within 15 s of starting is classified **failed** with the log tail as the reason (not merely "exited").
- **Kept running, kept current.** `ProjectSettings.AutoRunOnOpen` (the "Keep running" toggle in the dock; null = undecided, the dock picks the default) starts the auto-select targets whenever the project is opened and nothing runs. `AppRunOnTaskMerged` restarts running targets flagged `restartOnMerge` and posts "Merged — the app is running with this change at …" on the task; hot-reloading targets need nothing.
- **Starting again shuts the old one down.** The runner keys sessions by (project, target) and kills the previous process — after its `stopCommand`, when it has one (`docker compose stop …`, or the containers outlive the PTY) — before spawning a replacement. A port held by a process Orkka did *not* start fails that target with a `PortConflict` instead; "free the port and run" is explicit, never a default.
- Gated on the `app-run` and `command-trust` runner capabilities (`app-run-prepare` marks the prepare-step runner), so an older runner hides the button rather than failing the call. Hub: `/hubs/apprun` (`AppRunHub`). The host sends the stored config's commands, and the runner vets each target's start, install and stop command against `CommandTrust` before starting it (`RunnerOperations.VetAppRunAsync`); a refused target — and anything depending on it — comes back unstarted with the approval it needs. Windows runs commands under `pwsh -NoLogo -NoProfile -Command` or `cmd /c` — never Windows PowerShell 5.1, which has no `&&` — and never bypasses the execution policy: a `.ps1` the machine's policy refuses fails the run with an explanation (`ShellResolver.ExecutionPolicyHint`).

---

## Agent Security & Isolation

Every agent runs on the developer's own machine, as the developer, next to everything they are signed in to. What keeps it to the task is enforced there, by the runner, whatever the host sends — see **SECURITY.md** (and the public summary at `/security` in orkka-ui).

- **Command guard** (`AgentBashGuard`, every phase, `Bash|PowerShell`): no credential reads, no `git push` / new remotes / registry publishing, no forge CLIs (`gh`, `glab`), no other hosts (`ssh`, scanners), no cloud or cluster CLIs, no host control, nothing in the Orkka home outside this task's worktree, no dotenv files, no commit-identity change. The project's own toolchain — including local DB clients and `docker compose` — is untouched. Not approvable by anyone, interactive sessions included (`InteractiveCommandPolicy` only asks before commands that destroy unlanded work).
- **Read guard** (`AgentSensitivePathGuard`, Read/Glob/Grep/LS/NotebookRead) and **write boundary** (`AgentWorktreeBoundaryGuard`): reads never reach `DeveloperSecrets` (keys, sign-ins, registry tokens, AI CLI logins, keychains, browser profiles, the Orkka home outside the worktree, dotenv files outside the worktree); writes stay in the worktree. Sibling repos of a base-folder project are readable for reference only (SECURITY.md → Sibling repositories).
- **Per provider:** Claude through those hooks; Codex through execpolicy rules written into the worktree (`CodexExecPolicy`, git-excluded) plus its `workspace-write` sandbox; Copilot through `--deny-tool shell(…)` (`AgentBashGuard.CommandPrefixRules`) and its path confinement. Gemini, Grok and OpenCode cannot be guarded and are not ready unless the developer sets `ORKKA_ALLOW_UNGUARDED_PROVIDERS` in `~/.orkka/runner.env` (`ProviderDetection.ApplyGuardPolicy`); the runner refuses them otherwise.
- **Sanitized environment** (`AgentEnvironment`): every `*TOKEN*`, `*SECRET*`, `*PASSWORD*`, `*API_KEY*`, `GITHUB*`/`GH_*`, cloud and connection-string variable is blanked. Then each CLI gets back only its OWN provider's allowlisted sign-in variables (`ProviderAuthEnvironment`: API key, gateway, Bedrock, Vertex, Foundry, Azure OpenAI, GitHub token/Enterprise host) from the run's workspace (`./orkka workspace set-env`, secrets in the OS secret store) or the machine (`~/.orkka/runner.env`, the start-up environment), and is told to keep them out of its own commands (Claude `CLAUDE_CODE_SUBPROCESS_ENV_SCRUB`, Codex `shell_environment_policy`, Copilot `--secret-env-vars`); the command guard refuses environment dumps and expanding those names. The Hello reports each CLI's version and sign-in mode, never a value — AGENTS.md → Sign-in modes.
- **What leaves the machine** (`RunnerChunkPrivacy`): tool results never; tool calls without file bodies. Orkka stores no code — diffs are read live from the runner when someone opens one (`TaskCodeChangeSnapshot` keeps paths and counts only).
- **Repository commands** run only when vouched for (`CommandTrust`, above). There is no remote terminal.
- **Credentials:** Orkka holds no git, forge, tracker or AI credential, and no host→runner field carries one (an env-based AI sign-in is resolved on the runner and handed only to that vendor's CLI). The runner token lives in the OS secret store, expires after 90 days and renews itself while connected (`RotateToken`).

**Key rule:** Agents write code. Orkka pushes the task branch and opens the pull request with the developer's own CLI after review; the agent never pushes.

---

## Database Architecture

One SQLite file holds the platform database. Orkka holds no customer-app databases — the code it works on, and any database that code uses, belong to the customer. See **DATABASE.md** for full details.

```
{DataDir}/orkka.db  (SQLite, WAL; default ~/.orkka/data)  → Platform data (EF Core, org-isolated via global query filter)
    tables of agents, documents, organizations, projects, runners,
    sourcecontrol, tasks, trackers, usage   (no schemas; table names unique across modules)
```

**Key rules:**
- Modular monolith: each module owns its tables in the one file, with its own `__EFMigrationsHistory_{module}`; Orkka.Local applies them one module after another at startup (`ApplyModuleMigrationsAsync`); each module starts from one `Initial` migration (regenerated for SQLite 2026-11; no database predates it), later changes are migrations on top of it. Cross-module references are plain ULID columns — no cross-module FKs.
- Every platform entity has `OrgId`, enforced by EF Core global query filter (in `ModuleDbContext`)
- One writer at a time: never write through another context (another module's API, the audit log) inside an open transaction; task numbers are reserved before a Tasks transaction opens (DATABASE.md → Concurrency)
- `DateTimeOffset` is stored as UTC ticks, ids as ULID-ordered text, JSON as text — conventions in `ModuleDbContext`
- The database is a file only the host opens — no listener, no credentials; agents never reach it (see Agent Security above)

### Core Platform Entities

```
Organizations, OrgMembers, Projects, ProjectRepos, LocalPullRequests,
ProjectAgents, Tasks, TaskAttachments, TaskPlans, TaskMessages,
TaskExecutions, DesignAssets, UsageRecords, Notifications, OrgApiKeys
```

ULIDs for PKs. JSON (as text) for flexible shapes. All entities include `OrgId`.

---

## Project Structure

```
orkka/
├── orkka, orkka.cmd               # the launcher scripts: check SDK/Node, build the UI if stale, build, run
├── global.json                    # pins the .NET 10 SDK band
├── README.md, SECURITY-REVIEW.md  # quick start; the datasheet for security reviewers
├── .claude/
│   ├── CLAUDE.md                  # This file — main project context
│   └── docs/                      # Companion docs (table below)
├── src/
│   ├── Orkka/                     # THE entry point (assembly `orkka`): start/open/status/stop + the runner's CLI; `start` runs host + runner in one process
│   ├── Orkka.Local/               # the host (library): OrkkaHost — API + engine + hubs + static UI (127.0.0.1)
│   ├── Orkka.SharedKernel/        # ModuleDbContext, in-process event bus, ULID, crypto, cache, AI model defaults, blob + user-directory seams
│   ├── Orkka.Git/                 # the one git engine (GitProcess, operation ledger) + GitUrls, GitUrlRedactor, RepoRelativePath
│   ├── Orkka.AgentRuntime/        # agent invokers, Bash guard, git workspace manager
│   ├── Orkka.LocalRunner(.Contracts)/  # the runner (library): RunnerHost, RunnerSession, guards, CommandTrust, trackers + its CLI commands (workspace/tracker/commands/doctor/connect)
│   ├── Orkka.Runners.Server/      # host side of the runner link (proxies, app-run; Hosting/: runner hub, /internal relays, router)
│   ├── ThirdParty/ih0.Claude.Agent.SDK/  # vendored Claude Agent SDK (MIT) + Orkka patches — see its VENDORED.md
│   └── Modules/                   # 10 modules + tiny .Contracts assemblies; own tables in the one SQLite file, except Execution
│       ├── Orkka.Modules.Agents          ├── Orkka.Modules.Runners
│       ├── Orkka.Modules.Documents       ├── Orkka.Modules.SourceControl
│       ├── Orkka.Modules.Execution (no tables)  ├── Orkka.Modules.Tasks
│       ├── Orkka.Modules.Organizations   ├── Orkka.Modules.Trackers
│       ├── Orkka.Modules.Projects        └── Orkka.Modules.Usage
├── ui/                            # the web UI (npm workspaces: apps/local + packages/*)
├── e2e/                           # end-to-end tests
├── tests/
└── .github/workflows/ci.yml       # build/test, ui, `./orkka start` smoke on Windows/Linux/macOS
```

### Hosts

**One process.** `src/Orkka` (assembly `orkka`, run by `./orkka`) is the entry point. `start` builds the host with `OrkkaHost.BuildAsync(OrkkaHostOptions)` (`src/Orkka.Local`, a library; it replaced `Orkka.AppHost` + `Orkka.Worker`), scrubs the environment (`RunnerProcess.PrepareEnvironment`), starts Kestrel, bootstraps, then starts the runner as a second generic host in the same process (`RunnerHost.Build`, `src/Orkka.LocalRunner`) dialing `http://127.0.0.1:{port}/hubs/runner` with its `orkr_` tokens — the same protocol, guards and token rotation as a runner on another machine. Ctrl+C / `stop` stops the runner, then the host. One instance per Orkka home (`run/orkka.lock`, `run/instance.json`). See LOCAL.md.

Host composition lives in `LocalComposition.AddOrkkaLocal`:
- **Modules:** all of them. `AddExecutionModuleCore` (queue, autopilot, orchestrator); `ITaskNotifier` is the direct `TaskNotifier` (the hub is in this process). Host services: `FileSystemBlobStorageService` (`IBlobStorageService`), `LocalUserDirectory` (`IUserDirectory`, the OS user name). In-process cache only.
- **Maps:** `/api/*` (FastEndpoints), `/hubs/tasks`, `/hubs/runner` (`HostedRunnerHub`, `orkr_` tokens), `/internal/runner/*` relays, `/mcp`, `/blobs/*`, `/launch`, `/local/*`, `/health` (names the service, `"service":"orkka"`); every other path is the **static UI** (`StaticUi`, from `Local:UiDir`, default `<repo>/ui/apps/local/dist`): to the UI's contract (`ui/apps/local/README.md` → How it is served): build files public (`/assets/*`, `/excalidraw/fonts/*` immutable, a missing one 404), unprefixed paths 302 to `/{locale}…` (`NEXT_LOCALE` cookie → Accept-Language → `en`), other GETs under a locale → `index.html` behind the session gate (no session → 401 signed-out page), missing build → 503; HTML gets a CSP without inline script (`StaticUi.ContentSecurityPolicy`), `nosniff`, `Referrer-Policy: no-referrer`, `X-Frame-Options: DENY`. No YARP, no Node at runtime.
- **Relays loop back:** `Runners:RelayUrlTemplate` = the host's own `http://127.0.0.1:{port}`, and a random per-process `WorkerAuth:ApiKey` (memory only) guards `/internal/*`, which also requires a loopback remote address.
- **Auth** (`Orkka.Local/Auth`): `LocalAuthHandler` (scheme `OrkkaLocal`) makes a request the single `local_user` — Owner of every workspace — from the `orkka_s` session cookie (httpOnly, SameSite=Strict, 30-day sliding; SHA-256 hashes in `{DataDir}/sessions.json`) or the install-secret bearer (also `access_token` on hubs). `orkka_key_` bearers go to `OrgApiKeyAuthHandler`. `LocalRequestGuard` runs first: Host allowlist (`127.0.0.1|localhost|[::1]:{port}`, else 421); cookie-authenticated writes need `Origin` = own origin + `X-Orkka-Csrf: 1`; cookie WebSockets need a matching Origin. `CurrentUserMiddleware` keeps `X-Orkka-Workspace` (400/403).
- **CLI endpoints** (install-secret bearer): `POST /local/launch-codes` → `{code}` (one-time, 60 s) for `GET /launch?code=` (sets the cookie, 302 to `/`); `POST /local/bootstrap` (ensures the user + default workspace, mints an `orkr_` token per workspace lacking a live one for this machine); `POST /local/shutdown`.
- **Config:** `Local:Port` (6750, `start --port`), `Local:DataDir` (`%ORKKA_HOME%/data` or `~/.orkka/data` — holds `orkka.db`, blobs, sessions), `Local:UiDir`; `ConnectionStrings__DefaultConnection` only to point the database at another SQLite file. **Secrets never come from the environment:** `Local:InstallSecret` and `Encryption:Key` live in the OS secret store (`orkka/local/*`, `LocalSecrets`) and reach the host as in-memory settings (`OrkkaHostOptions.Settings`); a host without `Encryption:Key` refuses to start.

The web UI (`ui/`) and the e2e suite (`e2e/`) live in this repository.

---

## Coding Conventions

### C# / .NET 10
- Latest C# features: primary constructors, collection expressions, pattern matching
- FastEndpoints (no controllers), vertical slice architecture, sealed by default
- EF Core code-first, global query filter for OrgId, async everywhere
- Result pattern for errors, Serilog for logging
- Claude Agent SDK for agent execution with `Cwd` set to worktree

### TypeScript / React
- A static SPA: routes in `ui/apps/local/src/routes.tsx`; pages load data in the browser (`useApiQuery`) and refresh after a write with `refreshData()` (FRONTEND.md → Core Rules, Routing)
- Tailwind, local session auth (launch code → `orkka_s` cookie), SignalR for realtime
- Mutations in `ui/apps/local/lib/actions.ts` (browser), Playwright MCP for E2E testing

### Database
- EF Core migrations (SQLite). Every entity: OrgId, CreatedAt, UpdatedAt. ULIDs. JSON as text. See DATABASE.md.

### Git
- `main` = deployable. Branches: `feat/`, `fix/`, `chore/`, `docs/`. Conventional commits.

---

## Companion Docs (`.claude/docs/`)

All companion docs live in `.claude/docs/` alongside this file at `.claude/CLAUDE.md`.

| File | Contents |
|------|----------|
| **PROJECTS.md** | The single project type: creation, repos (any git remote / linked local folder, runner-only access), stack detection, agent seeding, run config + `orkka.json` |
| **DATABASE.md** | The SQLite file, module tables, conventions (ticks, ids, JSON), one-writer concurrency, FTS5 memory search, multi-tenancy, migrations |
| **LOCAL.md** | How Orkka runs from source: the `./orkka` scripts, the one process (`start`: host + runner on 127.0.0.1), the static UI, data dirs, secrets, troubleshooting |
| **SECURITY.md** | What leaves the developer's machine, what the host can make it do, agent guards per provider, command trust, credentials, run-from-source distribution, known limits |
| **TRACKERS.md** | Jira Cloud / Azure DevOps ticket source: connections, project config, polling, ingestion, status write-back |
| **RUNNER_GIT.md** | Git on the developer's machine: layout, credentials and identity, branches, who pushes, pull requests |
| **AGENTS.md** | Agent roles, default prompts, repo assignment, system prompt assembly, multi-agent flow, Playwright MCP QA, Designer pipeline |
| **BACKEND.md** | Vertical slice structure, FastEndpoints patterns, EF Core config, SignalR events, auth, background services |
| **FRONTEND.md** | Design system (colors, typography), page structure, component rules, local session auth, Playwright |
| **INTERACTIVE.md** | Interactive development: session-is-a-task model, session affinity, approval round-trip, live diff, build order |
