I have everything I need. Here is the full report.

---

# Orkka backend (`orkka-api`) — E2E test-design reference

> **2026-11-18 — R4, run from source (supersedes everything below about installing or running Orkka).** There is no `orkka-local` binary any more: Orkka is a git clone, `./orkka start` (`orkka.cmd` on Windows) builds it and runs ONE foreground process (`dotnet src/Orkka/bin/Release/net10.0/orkka.dll` — the `src/Orkka.Local` host with API, hubs, `/internal/runner/*` relays and the static Vite UI from `ui/apps/local/dist`, plus the in-process runner; SQLite at `~/.orkka/data/orkka.db`). Verbs (`src/Orkka/Program.cs`): `start`, `stop`, `status`, `open`, `connect` (advanced: a runner alone), `doctor`, `workspace(s)`, `commands`/`command`, `tracker(s)`, `update` (prints "Orkka runs from source: `git pull`, then `./orkka start`."), `uninstall` (prints how to delete the folder), `help`. GONE: release downloads and signature verification (`ReleaseVerification`), the newer-release check (`--no-update-check`, `ORKKA_NO_UPDATE_CHECK`), `path add|remove|status`, `--detach`, `start`/`up`/`down` as stack verbs, the default `connect` verb, the supervisor, portable Postgres, the Next.js standalone UI server, YARP, `Orkka.Local.Host`, `Orkka.AppHost`, `Orkka.Worker`, the `orkka-local-cli` distribution repo. UI routes are declared in `ui/apps/local/src/routes.tsx`. Sibling repo paths below (`orkka-api`, `orkka-ui`) are now `src/` and `ui/` of this monorepo; command lines below were renamed to `./orkka …`, but descriptions of the removed pieces are history only.
>
> **2026-10-26 pivot (tracker-driven, runner-only).** Removed: managed/hosted apps (brief, design brief, provisioning, trials, Vercel/Neon, custom domains, env vars, platform tokens, `/platform/v1`), the GitHub App (install, webhooks, issue/CI triggers, create-branch), Orkka Local (Docker stack, `/api/local/*`, `apps/local`, `./orkka start|stop|status`), milestone integration branches / ship sweep / retry-ship, cloud agent runs (dormant behind `Execution:CloudEnabled`) and the platform-key AI fallback. Sections about those were deleted below; what remains predates the pivot and may name them in passing.
>
> **2026-11-17 update (orkka-api e3a3a06 … 10707ed, orkka-ui 6c17c16 / 8ddcd6f).** Cloud execution is GONE, not dormant: no `ExecutionTarget`/`ExecutionTargetReason` on tasks, no execution preference (`GET|PUT /api/me/execution-preference`), no execution-target preview/switch (`POST /api/projects/{id}/tasks/execution-target`, `POST /api/tasks/{id}/execution-target`), no org `allowHybridExecution`, no project `cloudFallbackAfterMinutes`, no cloud fallback sweep / provider capacity gate / free-tier ceiling / cloud jobs, forecasts are time-only (cost always null, no `executionTarget*`), admin usage has no `cloudExecutions`/`localExecutions`. Every task runs on its owner's runner (`RunnerUserId`); `IRunnerCoverageResolver` only explains whether that runner has the model's provider. Orkka stores no code: code-change snapshots keep paths/counts/hash, `GET /api/tasks/{id}/code-changes/{snapshotId}` reads the diff live from the runner (`currentBranch`, `unavailable`); commit history carries no `authorEmail`. Runner tokens expire after 90 days (`expiresAt` in list/create) and renew while connected (hub `RotateToken`). The runner vets every repository command (`command-trust`: orkka.json on the default branch or `./orkka commands approve <id>`; refusals post a task-thread note), has no remote terminal, streams no tool results, always enforces the agent guard, treats Gemini/Grok/OpenCode as not ready unless `ORKKA_ALLOW_UNGUARDED_PROVIDERS` lists them in `~/.orkka/runner.env`, keeps its token in the OS secret store, and only `./orkka update` (signature-verified) replaces the binary — `connect` just reports a newer release (`--no-update-check` / `ORKKA_NO_UPDATE_CHECK`). Catalogue: RUN-030…042, AIR-007/008, GRD-007/008, HYB-047, NEG-031, MKT-024.
>
> **New surface** (catalogue areas in brackets):
> - Workspaces — one org per job: `GET /api/me/workspaces`, `POST /api/workspaces`, `POST /api/me/active-workspace`, header `X-Orkka-Workspace` (400 malformed / 403 not a member), cookie `orkka_ws`, top-bar WorkspaceSwitcher, `/onboarding` five-step wizard; CLI `./orkka connect --workspace <name>`, `./orkka workspace list|remove|set-identity` [WSP, ONB].
> - Repos by URL — any https/ssh/scp remote (`RepoUrl`), `provider` override + `cliAccount`, embedded credentials refused, `GET /api/projects/{id}/repos/access-check` through the runner [RURL].
> - Forge CLIs — runner Hello reports gh/az/glab (`GET /api/me/runner` → `forgeClis`); landing a remote's PR PUBLISHES it with the developer's CLI (idempotent per head branch); Orkka PRs are its own records (`orkka-local://pull/N`) [FRG].
> - Trackers — `./orkka tracker add jira|ado|list|test|remove` (token in the OS secret store, metadata in `~/.orkka/workspaces/<ws>/trackers.json`), `GET /api/trackers/connections`, `POST /api/trackers/connections/{id}/query` (test/scopes/epics/statuses/preview, `supported:false` when the runner is offline), `GET|PUT|DELETE /api/projects/{id}/tracker`; hub `SyncTrackers` / `ReportTickets` / `AckTransitions`; tickets → `[KEY] title` tasks with `externalKey/Url/Epic`, comments → `ExternalComment` messages, status write-back Started / PR opened / Merged [TSRC, TCLI, TING, TWB].
> - Readiness — no AI CLI signed in ⇒ `WaitingForRunner` + one note; Bash guard blocks `gh auth token`, `az account get-access-token`, `git credential fill`, `~/.ssh`, `.orkka/workspaces`, `trackers.json` [AIR, GRD].


Repo root: `C:\Projects\orkka\orkka-api` · .NET 10 modular monolith, FastEndpoints.
Sibling repos: `C:\Projects\orkka\orkka-infra` (Helm/AKS), `orkka-ui`, `orkka-admin-ui`, `orkka-local-cli`, `orkka-mobile`, `orkka-e2e-tests` (**currently empty — this is the repo the new catalogue will land in**).

---

## 0. Host topology

| Host | Project | What it serves |
|---|---|---|
| **API** | `src/Orkka.AppHost` | All `/api/*`, `/platform/v1/*`, `/mcp`, `/hubs/projects`, `/hubs/tasks`, `/health`, `/openapi/v1.json` + Scalar (non-prod only) |
| **Worker** | `src/Orkka.Worker` | `/hubs/runner` (hosted runner hub), `/internal/runner/*` relay, `/internal/agent-runs/*` cloud-job callbacks, `/health`. **No FastEndpoints, no auth middleware** (`src/Orkka.Worker/Program.cs:175-187`) |
| **Orkka Local host** | `src/Orkka.Local.Host` | Same module endpoints as API (minus Admin/Runners/Platform) **plus** `/api/local/*`, `/mcp`, `/hubs/runner`, `/hubs/terminal`, `/hubs/apprun` |
| Runner CLI | `src/Orkka.LocalRunner` (`./orkka`) | Client only — dials the hub |
| Agent runner | `src/Orkka.AgentRunner` | K8s Job image for cloud runs |
| Git engine | `src/Orkka.Git` | platform-git engine |

Modules under `src/Modules/`: Admin, Agents, Billing, Documents, Execution, Organizations, Platform, Projects, Runners, SourceControl, Tasks (each + `.Contracts`).

---

## 1. Project modes matrix

### 1.1 The four orthogonal axes

| Axis | Enum / field | File |
|---|---|---|
| **Type** | `ProjectType { Managed, Unmanaged }` | `src/Modules/Orkka.Modules.Projects.Contracts/Enums.cs:6` |
| **Status** | `ProjectStatus { Briefing, Provisioning, Ready, Failed }` | same file `:12` |
| **Whose runner (per task)** | `TaskEntity.RunnerUserId` — always the owner's runner (no execution target, no preference) | `src/Modules/Orkka.Modules.Tasks/Domain/TaskEntity.cs` |
| **Does that runner cover the model** | `RunnerCoverageDecision { RunnerUserId, Provider, Model, CoveredByRunner, Reason, … }` (explanation only) | `src/Modules/Orkka.Modules.Runners.Contracts/IRunnerCoverageResolver.cs` |
| **Repo source** | `GitProvider { GitHub, GitLab, Bitbucket, LocalPath }` | `src/Modules/Orkka.Modules.SourceControl.Contracts/GitProvider.cs:3` |
| **Integration mode (per milestone)** | `MilestoneIntegrationMode { Branch, Trunk }` | `src/Modules/Orkka.Modules.Tasks.Contracts/Enums.cs:116` |
| **Plan** | `BillingPlan { Free, Pro, Enterprise }` | `src/Modules/Orkka.Modules.Billing/Domain/BillingPlan.cs` |

Entities:
- `src/Modules/Orkka.Modules.Projects/Domain/Project.cs` — `Type`, `Status`, `AppSlug`, `TrialEndsAt`, `PaidFrom`, `SuspendedAt`, `TeardownNoticeStage`, `DatabaseSlug`, `VercelProjectId/Url`, `CustomDomain(+Status)`, `ManagedMonthlyFeeBase`, `MarkupRate`, `Requirements`.
- `src/Modules/Orkka.Modules.Projects/Domain/ProjectRepo.cs` — `GitUrl`, `Provider`, `DefaultBranch`, `Stack`, `SetupCommand`, `QaRunCommand`, `BuildCommand`, `CheckCommand`, `RepoCredentialId`, `WorkspaceStatus`.
- `src/Modules/Orkka.Modules.Tasks/Domain/TaskEntity.cs` — **`RunnerUserId`** (the Clerk user whose daemon runs it; `ExecutionTarget` was dropped in migration `RemoveExecutionTarget`).

### 1.2 Repo-source variants

| Variant | How it's created | Resulting row |
|---|---|---|
| **GitHub App-linked forge repo** | `POST /api/projects/{projectId}/repos` (`AddProjectRepoEndpoint.cs`) after installing the GitHub App (`GET /api/github/install-url` → `/api/github/callback`) | `Provider = GitHub`, `GitUrl` = https clone URL, `RepoCredentialId` → org installation |
| **Local folder linked through the runner** | `POST /api/projects/{projectId}/repos/local` (`src/Modules/Orkka.Modules.Runners/Features/LocalRepos/LocalRepoEndpoints.cs:264`). Relays to the caller's own runner via `POST /internal/runner/fs/prepare-repo`; git-inits the folder if needed | `Provider = LocalPath`, `GitUrl` = **absolute path on the user's machine**, no remote, no token |
| **"Let Orkka host it" (published folder)** | `POST /api/projects/{projectId}/repos/{repoId}/attach-remote` with `{ "hostedByOrkka": true }` (`LocalRepoEndpoints.cs:387`, DTO `AttachRemoteRequestDto(RemoteUrl, HostedByOrkka)` at `:44`). Creates a **private repo under `GitHub:ManagedOrgName` (default `orkka-repos`)** via `GitHub:ManagedInstallationId`, mints a repo-scoped write token, pushes. **Repo stays `Provider = LocalPath`** — folder is still the working copy, the remote is only a publish target | `Provider = LocalPath` + remote in `orkka-repos` |
| **Bring-your-own remote for a local folder** | same endpoint with `{ "remoteUrl": "https://github.com/you/repo" }` — requires the org's GitHub App installation to see that repo | `Provider = LocalPath` + user's remote |
| **orkka-repos managed repo** | `ManagedProjectProvisioner` on a Managed project. `orkka-repos/{slug}-{projectId[..8]}` (`SlugGenerator.GenerateManagedRepoName`) | `Provider = GitHub`, owned by Orkka's org; user has no credentials to it |

### 1.3 Routing — every task runs on its owner's runner

Cloud execution was removed (orkka-api e3a3a06). `RunnerUserId` is stamped at creation (task-create form, PM/MCP chat, interactive session start, accept-plan, tracker ingestion) as the creator; `IRunnerCoverageResolver` (`src/Modules/Orkka.Modules.Runners.Contracts/IRunnerCoverageResolver.cs`) only answers whether that runner has the model's provider signed in (`CoveredByRunner`, `Reason`) so the thread can say "waiting: sign in to {provider}". A task whose runner is offline or uncovered parks `WaitingForRunner`; nothing ever falls back to a billed run, and a model change before the first run re-checks coverage (`UpdateTaskEndpoint`).

### 1.6 Plan / tier limits

`src/Modules/Orkka.Modules.Billing/Infrastructure/PlanOptions.cs:89-101` + `src/Orkka.AppHost/appsettings.json` (`Plans:Limits`):

| Plan | MaxProjects | MaxSeats | MaxReposPerProject | TeamMembers |
|---|---|---|---|---|
| Free | 1 | 1 | 2 | false |
| Pro | ∞ | 1 | ∞ | false |
| Enterprise | ∞ | ∞ | ∞ | **true** |

- Unknown plan ⇒ `new PlanLimits()` = the *most restrictive* (1/1/2).
- **Managed projects are excluded from the project count** (`CreateProjectEndpoint.cs:44-46`) — they carry their own monthly fee.
- Over-limit responses are **HTTP 402** with `{ code: "plan_limit_projects", error, plan, limit }` — *not* 403.
- Credits: `BillingAccount.SignupFreeCreditsUsd = 10.00m` USD; `CreditUnit.PerUsd = 1000` ⇒ **10,000 free credits** at signup. `SubscribeGrantUsd = 5.00` one-time on first subscribe. `HostedAppMonthlyCreditsUsd = 2.00` (2,000 credits/paid app/month, reset not accumulated).
- `BillingDefaults`: `DefaultMode = Prepaid`, `DefaultMonthlyLimitUsd = 250`, `PauseOnLimit = true`, `DefaultPlan = Free`.

---

## 2. Full HTTP API surface

314 unique verb+path pairs from `Get(/Post(/Put(/Patch(/Delete(` in 252 `*Endpoint*.cs` files, plus `/api/org/invites/accept`, `/mcp`, 3 hub paths, and the Worker's minimal-API routes.

**Permission model** (`src/Orkka.SharedKernel/Security/Permission.cs`, `PermissionGuard.cs`): permissions are **not** declared in `Configure()`; they are checked as the first line of `HandleAsync` via `PermissionGuard.AuthorizeAsync(currentUser, this, Permission.X)` → 403. **Reads are open to every org member; Owners bypass all checks** (`ICurrentUser.HasPermission`). Enum members: `TasksCreate, TasksEdit, TasksDelete, TasksExecute, PlansReview, CodeReviewReview, CodeReviewMerge, DocumentsManage, AgentsChat, AgentsConfigure, DesignReview, ProjectsCreate, ProjectsManage, ProjectsDelete, ReposManage, BillingManage, MembersManage, AuditView`.
Below, **"—"** = authenticated org member, no extra permission.

### Route counts by module

| Module | routes |
|---|---:|
| Tasks | 80 |
| Agents | 76 |
| Organizations | 41 (+1 accept-invite) |
| Projects | 39 |
| Admin | 27 |
| Runners | 23 |
| Billing | 23 |
| Worker (`/internal/*`) | 21 |
| Local.Host (`/api/local/*`) | 18 |
| Documents | 14 |
| Platform (`/platform/v1/*`) | 10 |
| SourceControl | 6 |

---

### 2.1 Organizations — org, members, invites, settings, notifications, devices, API keys, Slack, webhooks, audit

`src/Modules/Orkka.Modules.Organizations/Features/…`

| Method | Path | Permission |
|---|---|---|
| GET | `/api/org/me` | — |
| PUT | `/api/org/me/preferences` | — |
| PUT | `/api/org` | **Owner only** (`OrgProfileEndpoints.cs:31`) |
| GET | `/api/org/settings` | — |
| PUT | `/api/org/settings` | **Owner only** (`OrgSettingsEndpoints.cs:122`) |
| GET | `/api/org/members` | — |
| DELETE | `/api/org/members/{memberId}` | MembersManage |
| PUT | `/api/org/members/{memberId}/permissions` | MembersManage |
| PATCH | `/api/org/members/{memberId}/role` | **Owner only** (`OwnerGuard`) |
| GET | `/api/org/invites` | MembersManage |
| POST | `/api/org/invites` | MembersManage |
| GET | `/api/org/invites/preview` | **AllowAnonymous** |
| POST | `/api/org/invites/accept` | authenticated (`OrgRoutes.AcceptInvite`) |
| DELETE | `/api/org/invites/{inviteId}` | MembersManage |
| GET | `/api/projects/{projectId}/members` | — |
| POST | `/api/projects/{projectId}/members` | MembersManage |
| DELETE | `/api/projects/{projectId}/members/{memberId}` | MembersManage |
| GET | `/api/notifications` | — |
| POST | `/api/notifications/{id}/read` | — |
| POST | `/api/notifications/read-all` | — |
| PUT | `/api/me/devices` | — |
| DELETE | `/api/me/devices/{deviceId}` | — |
| GET | `/api/me/api-keys` | — (own keys only) |
| POST | `/api/me/api-keys` | — (**refuses if caller is itself an API key**) |
| DELETE | `/api/me/api-keys/{keyId}` | own, or Owner |
| GET | `/api/audit` | AuditView |
| POST | `/api/audit/verify` | AuditView |
| GET | `/api/audit/export` | AuditView |
| GET | `/api/slack/status` | — |
| GET | `/api/slack/install-url` | — |
| POST | `/api/slack/callback` | ProjectsManage |
| DELETE | `/api/slack/installation` | ProjectsManage |
| GET | `/api/slack/channels` | — |
| GET · PUT · DELETE | `/api/projects/{projectId}/slack` | GET —, PUT/DELETE ProjectsManage |
| POST | `/api/slack/interactions` | **AllowAnonymous** (X-Slack-Signature); per-action: PlansReview / CodeReviewMerge / TasksExecute |
| POST | `/api/slack/commands` | **AllowAnonymous**; TasksCreate for `/orkka task` |
| POST | `/api/slack/events` | **AllowAnonymous** |
| GET | `/api/projects/{projectId}/webhooks` | — |
| POST | `/api/projects/{projectId}/webhooks` | ProjectsManage |
| PUT · DELETE | `/api/projects/{projectId}/webhooks/{webhookId}` | ProjectsManage |
| POST | `/api/projects/{projectId}/webhooks/{webhookId}/test` | ProjectsManage |
| GET | `/api/projects/{projectId}/webhooks/{webhookId}/deliveries` | — |

### 2.2 Projects — CRUD, brief, provisioning, repos, run-config, environment, domain, deployments, platform tokens

`src/Modules/Orkka.Modules.Projects/Features/…`

| Method | Path | Permission |
|---|---|---|
| GET | `/api/projects` | — |
| POST | `/api/projects` | ProjectsCreate (+ plan cap → 402) |
| GET | `/api/projects/{id}` | — |
| PATCH | `/api/projects/{id}` | ProjectsManage |
| DELETE | `/api/projects/{id}` | **ProjectsDelete** |
| GET | `/api/projects/app-slug/available` | — |
| POST | `/api/projects/brief` | ProjectsCreate (+ `MaxConcurrentTrials=1` → 402 `trial_limit_apps`) |
| POST | `/api/projects/{projectId}/brief` | ProjectsCreate |
| GET | `/api/projects/{projectId}/brief` | — |
| POST | `/api/projects/{projectId}/brief/messages` | ProjectsCreate (cap **60** messages) |
| POST | `/api/projects/{projectId}/brief/finalize` | ProjectsCreate |
| POST | `/api/projects/{projectId}/brief/skip` | ProjectsCreate |
| GET | `/api/projects/{projectId}/provisioning` | — |
| POST | `/api/projects/{projectId}/provisioning/retry` | ProjectsCreate |
| GET | `/api/projects/{projectId}/repos` | — |
| POST | `/api/projects/{projectId}/repos` | ReposManage |
| PATCH | `/api/projects/{projectId}/repos/{repoId}` | ReposManage |
| DELETE | `/api/repos/{repoId}` | ReposManage |
| GET | `/api/projects/{projectId}/repos/{repoId}/files` · `/files/content` · `/files/search` | — |
| GET | `/api/projects/{projectId}/repos/{repoId}/tree` · `/commits` · `/commits/{sha}/diff` · `/branches` | — |
| POST | `/api/projects/{projectId}/repos/{repoId}/branches` | ReposManage |
| POST | `/api/projects/{projectId}/repos/{repoId}/clone-access` | ReposManage |
| GET | `/api/projects/{projectId}/run-config` | — |
| POST | `/api/projects/{projectId}/run-config/detect` | ReposManage |
| PUT | `/api/projects/{projectId}/run-config` | ReposManage |
| GET | `/api/projects/{id}/environment` | — |
| PUT | `/api/projects/{id}/environment` | ProjectsManage |
| DELETE | `/api/projects/{id}/environment/{name}` | ProjectsManage |
| GET | `/api/projects/{id}/domain` | — |
| POST · DELETE | `/api/projects/{id}/domain` | ProjectsManage |
| GET | `/api/projects/{id}/deployments` | — |
| GET | `/api/projects/{projectId}/platform-tokens` | ProjectsManage |
| POST | `/api/projects/{projectId}/platform-tokens/rotate` | ProjectsManage |

### 2.3 Tasks — board, tasks, plans, PR review/merge, messages, attachments, milestones, autopilot, sessions, forecast

`src/Modules/Orkka.Modules.Tasks/Features/…`

| Method | Path | Permission |
|---|---|---|
| GET | `/api/projects/{projectId}/board` · `/board/search` · `/board-metrics` | — |
| GET | `/api/board` · `/api/board/search` (the workspace board: every project of the workspace, `?projectId=` narrows; 404 for another workspace's project) | — |
| GET | `/api/projects/{projectId}/tasks` · `/tasks/number/{number}` | — |
| POST | `/api/projects/{projectId}/tasks` | TasksCreate |
| POST | `/api/projects/{projectId}/tasks/forecast` | — |
| GET | `/api/tasks/{id}` | — |
| PATCH | `/api/tasks/{id}` | TasksEdit |
| DELETE | `/api/tasks/{id}` | TasksDelete |
| PATCH | `/api/tasks/{id}/lane` | **TasksExecute** |
| POST | `/api/tasks/{id}/submit` · `/retry` · `/reset` · `/create-pr` | TasksExecute |
| POST | `/api/tasks/{id}/model-recommendation` | TasksEdit |
| POST | `/api/tasks/{id}/archive` · `/unarchive` · `/block` · `/unblock` | TasksEdit |
| POST | `/api/tasks/{id}/dependencies` · DELETE `/dependencies/{dependsOnTaskId}` | TasksEdit |
| POST | `/api/tasks/{id}/spend/continue` · `/spend/stop` | TasksExecute |
| GET | `/api/tasks/{taskId}/messages` | — |
| POST | `/api/tasks/{taskId}/messages` | **TasksExecute** ← this is "answer questions" |
| GET | `/api/tasks/{taskId}/plans` · `/plans/latest` | — |
| POST | `/api/tasks/{taskId}/plans/{planId}/review` | **PlansReview** (approve/reject) |
| GET | `/api/tasks/{taskId}/pull-request` | — |
| POST | `/api/tasks/{taskId}/pull-request/reviews` · `/comments` | CodeReviewReview |
| POST | `/api/tasks/{taskId}/pull-request/merge` · `/sync` | CodeReviewMerge |
| GET | `/api/tasks/{taskId}/merge-conflicts` | — |
| POST | `/api/tasks/{taskId}/resolve-merge` | CodeReviewMerge |
| DELETE | `/api/tasks/{taskId}/resolve-merge/queued` | CodeReviewMerge |
| GET | `/api/repos/{repoId}/pulls` · `/pulls/{number}` | — |
| POST | `/api/repos/{repoId}/pulls/{number}/close` · `/merge` | CodeReviewMerge |
| POST | `/api/repos/{repoId}/pulls/{number}/reviews` · `/comments` | CodeReviewReview |
| POST | `/api/tasks/{taskId}/attachments/upload-url` · `/attachments` | TasksEdit |
| GET | `/api/tasks/{taskId}/attachments` | — |
| DELETE | `/api/attachments/{id}` | TasksEdit |
| GET | `/api/tasks/{taskId}/executions` · `/evidence` · `/forecast` · `/code-changes` · `/code-changes/{snapshotId}` | — |
| POST | `/api/tasks/{taskId}/verification` | TasksExecute |
| GET | `/api/projects/{projectId}/executions` | — |
| GET | `/api/projects/{projectId}/agent-stats` | — |
| GET | `/api/inbox` | — |
| GET | `/api/projects/{projectId}/milestones` · `/milestones/{id}` · `/milestones/{id}/forecast` | — |
| POST | `/api/projects/{projectId}/milestones` | TasksCreate |
| PATCH | `/api/projects/{projectId}/milestones/{id}` | TasksEdit |
| DELETE | `/api/projects/{projectId}/milestones/{id}` | TasksDelete |
| POST | `/api/projects/{projectId}/milestones/plan` · `/milestones/accept` | TasksCreate |
| POST | `/api/projects/{projectId}/milestones/{id}/retry-ship` | CodeReviewMerge |
| GET | `/api/projects/{projectId}/milestone-plans/{jobId}` · `/milestone-plans/active` | — |
| GET · POST · DELETE | `/api/projects/{projectId}/autopilot` | GET —; POST/DELETE TasksExecute (+ `uncapped`⇒BillingManage, `answerEveryQuestion`⇒ProjectsManage) |
| POST | `/api/projects/{projectId}/interactive-sessions` | TasksCreate **and** TasksExecute |
| POST | `/api/tasks/{taskId}/interactive-session/finish` | TasksExecute |
| GET | `/api/tasks/{taskId}/approvals` | — |
| POST | `/api/tasks/{taskId}/approvals/{approvalId}` | TasksExecute |
| GET | `/api/projects/{projectId}/task-groups/{id}` | — |
| PATCH | `/api/projects/{projectId}/task-groups/{id}` | TasksEdit |
| POST | `/api/projects/{projectId}/task-groups/{id}/repo-requests` | TasksCreate |
| GET | `/api/admin/forecast-calibration` | **PlatformAdmin** |

### 2.4 Agents — agents, chat, design assets/brief, diagrams, memory, skills, schedules, models

`src/Modules/Orkka.Modules.Agents/Features/…`

| Method | Path | Permission |
|---|---|---|
| GET | `/api/projects/{projectId}/agents` · `/agents/{agentId}` | — |
| PUT · DELETE | `/api/projects/{projectId}/agents/{agentId}` | AgentsConfigure |
| POST | `/api/projects/{projectId}/agents/switch-provider` | AgentsConfigure |
| PUT | `/api/projects/{projectId}/agents/{agentId}/skills` | AgentsConfigure |
| GET · POST | `/api/projects/{projectId}/agents/{agentId}/conversations` | GET —; POST AgentsChat |
| DELETE | `…/conversations/{conversationId}` | AgentsChat |
| GET | `…/conversations/{conversationId}/messages` | — |
| POST | `…/conversations/{conversationId}/messages` | AgentsChat (SSE stream) |
| POST | `…/conversations/{conversationId}/cancel-run` | AgentsChat |
| POST | `/api/projects/{projectId}/chat-attachments` | AgentsChat |
| GET | `/api/projects/{projectId}/design-assets` · `/design-assets/{assetId}` | — |
| POST | `/api/projects/{projectId}/design-assets/upload` | DesignReview |
| POST | `…/design-assets/{assetId}/approve` · `/reject` · `/restore` | DesignReview |
| PATCH | `…/design-assets/{assetId}/folder` | DesignReview |
| DELETE | `…/design-assets/{assetId}` | DesignReview |
| POST | `…/design-assets/folders/rename` · `/folders/delete` | DesignReview |
| GET · PUT | `/api/projects/{projectId}/brand` | GET —; PUT DesignReview |
| GET · POST | `/api/projects/{projectId}/design-brief` | GET —; POST ProjectsCreate |
| POST | `…/design-brief/generate` · `/regenerate` · `/select` · `/confirm` · `/skip` · `/preview` · `/options/{optionId}/refine` | ProjectsCreate |
| GET | `/api/projects/{projectId}/diagrams` · `/{diagramId}` · `/{diagramId}/versions` · `/versions/{version}` | — |
| PUT · DELETE | `/api/projects/{projectId}/diagrams/{diagramId}` | AgentsChat |
| PATCH | `…/diagrams/{diagramId}/folder` | AgentsChat |
| POST | `…/diagrams/folders/rename` · `…/versions/{version}/restore` | AgentsChat |
| GET | `/api/projects/{projectId}/memories` · `/memories/retrieve` | — |
| POST · PUT · DELETE | `/api/projects/{projectId}/memories[/{memoryId}]` | AgentsConfigure |
| GET | `/api/projects/{projectId}/skills` · `/skills/{skillId}` | — |
| POST · PUT · DELETE | `/api/projects/{projectId}/skills[/{skillId}]` | AgentsConfigure |
| GET | `/api/projects/{projectId}/schedules` · `/{scheduleId}` · `/{scheduleId}/runs` | — |
| POST · PUT · PATCH `/enabled` · DELETE · POST `/run` | `/api/projects/{projectId}/schedules…` | **TasksExecute** (+ BillingManage for uncapped) |
| GET | `/api/models` | — |

> **Automation**: "schedules" are the cron/automation surface (`ScheduleEndpoints.cs`); "autopilot" is in Tasks. There are **no** separate `reminders` or `triggers` endpoints in this codebase — triggers are Schedules + Webhooks + Slack events.

### 2.5 Documents

| Method | Path | Permission |
|---|---|---|
| GET | `/api/projects/{projectId}/documents` · `/{id}` · `/{id}/file` · `/{id}/pdf` · `/{id}/docx` · `/{id}/revisions` · `/{id}/revisions/{version}` · `/by-task/{taskId}` | — |
| POST | `/api/projects/{projectId}/documents` · `/documents/upload` · `/documents/folders/rename` · `/{id}/revisions/{version}/restore` | DocumentsManage |
| PATCH · DELETE | `/api/projects/{projectId}/documents/{id}` | DocumentsManage |

### 2.6 Billing

| Method | Path | Permission |
|---|---|---|
| GET | `/api/pricing` | **AllowAnonymous** |
| GET | `/api/billing/credits` · `/credits/balance` · `/credits/packs` | — |
| GET · PUT | `/api/billing/credits/auto-recharge` | GET —; PUT BillingManage |
| POST | `/api/billing/credits/topup` | BillingManage |
| PUT | `/api/billing/mode` | BillingManage |
| GET | `/api/billing/usage/summary` · `/usage/daily` · `/upcoming` · `/invoices` | — |
| GET | `/api/billing/usage/breakdown` | — for own scope; BillingManage for org-wide |
| GET · PUT | `/api/billing/budget` | GET —; PUT BillingManage |
| GET | `/api/billing/payment-methods` | — |
| POST | `/api/billing/payment-methods` · `/payment-methods/setup-intent` | BillingManage |
| DELETE | `/api/billing/payment-methods` | BillingManage |
| GET | `/api/billing/subscription` | — |
| POST | `/api/billing/subscription/checkout` · `/subscription/portal` | BillingManage |
| POST | `/api/billing/webhooks/stripe` | **AllowAnonymous** (Stripe signature) |

Hosted-app subscription is a **second Stripe subscription per org whose quantity = number of paid apps** (`IHostedAppBilling`, `HostedAppPriceId`/`HostedAppProductId`); it has no dedicated endpoint — it is driven by trial promotion and the Stripe webhook.

### 2.7 SourceControl

| Method | Path | Permission |
|---|---|---|
| GET | `/api/github/install-url` | ReposManage |
| GET | `/api/github/callback` | ReposManage |
| GET | `/api/github/installations` | — |
| GET | `/api/github/installations/{installationId}/repos` | — |
| DELETE | `/api/github/installations/{installationId}` | ReposManage |
| POST | `/api/github/webhooks` | **AllowAnonymous** (HMAC) |

### 2.8 Runners (hosted only — not present on Orkka Local)

| Method | Path | Permission |
|---|---|---|
| GET | `/api/me/runner` | — |
| GET | `/api/org/runners` | — |
| GET · POST | `/api/me/runner-tokens` | — |
| DELETE | `/api/me/runner-tokens/{tokenId}` | — |
| POST | `/api/runners/fs/browse` · `/runners/fs/init-repo` | — (relayed to caller's own runner) |
| POST | `/api/projects/{projectId}/repos/local` | ReposManage |
| POST | `/api/projects/{projectId}/repos/{repoId}/attach-remote` | ReposManage |
| GET | `/api/projects/{projectId}/repos/{repoId}/sync-status` | — |
| POST | `/api/projects/{projectId}/repos/{repoId}/push` | ReposManage |
| GET | `/api/projects/{projectId}/repos/access-check` | — |
| GET | `/api/projects/{projectId}/app-run` | — |
| POST | `/api/projects/{projectId}/app-run/start` · `/stop` · `/input` · `/resize` | — |
| GET | `/api/projects/{projectId}/repos/{repoId}/editors` | — |
| POST | `/api/projects/{projectId}/repos/{repoId}/open-in-editor` | — |
| GET | `/api/tasks/{taskId}/editors` | — |
| POST | `/api/tasks/{taskId}/open-in-editor` | — |

### 2.10 Admin — `/api/admin/*` (all `AdminGuard` / `PlatformAdminGuard`)

`GET /api/admin/me` · `GET /api/admin/platform/summary` · `GET /api/admin/git-operations/summary`
`GET /api/admin/orgs` · `GET /api/admin/orgs/{orgId}` · `GET|POST /api/admin/orgs/{orgId}/credits` · `POST /api/admin/orgs/{orgId}/plan` · `POST /api/admin/orgs/{orgId}/suspend` · `POST /api/admin/orgs/{orgId}/unsuspend`
`GET /api/admin/finance/summary` · `GET /api/admin/finance/orgs` · `GET /api/admin/finance/orgs/{orgId}/projects` · `POST /api/admin/finance/analysis`
`GET|POST /api/admin/infra-costs` · `PUT|DELETE /api/admin/infra-costs/{id}`
`GET|PUT /api/admin/settings/rates` · `PUT /api/admin/settings/managed-fee` · `PUT /api/admin/projects/{projectId}/markup-rate`
`GET|POST /api/admin/models` · `PUT|DELETE /api/admin/models/{id}` · `PATCH /api/admin/models/{id}/enabled` · `PUT /api/admin/models/tier-defaults` · `POST /api/admin/models/sync` · `GET /api/admin/models/sync/events` · `POST /api/admin/models/sync/events/{id}/approve` · `POST /api/admin/models/sync/events/{id}/dismiss` · `POST /api/admin/models/{id}/reset-rate`
`GET /api/admin/bug-reports` · `GET /api/admin/bug-reports/{id}` · `PATCH /api/admin/bug-reports/{id}/status`
`GET /api/admin/forecast-calibration`
Non-admin bug submission: `POST /api/bug-reports/upload-url` · `POST /api/bug-reports`.
Platform admins come from config `PlatformAdmins:ClerkUserIds` (prod: one Clerk id in `values-prod.yaml`).

### 2.11 MCP

`POST /mcp` — MCP Streamable HTTP, **stateless**, `RequireAuthorization()` (`src/Modules/Orkka.Modules.Agents/Features/Mcp/McpHosting.cs:43`). Mounted on **both** AppHost and Local.Host.

Tools (`OrkkaMcpToolServer.cs:62-88`) — `ListTools` **is** the authorization: a tool the caller can't use isn't listed.
Reads (no permission): `list_projects, get_project, list_tasks, get_task, get_task_history, list_documents, get_document, list_milestones, list_repos, list_agents, list_skills, list_schedules, get_inbox, get_autopilot_status`.
Writes: `create_tasks`(TasksCreate) · `submit_task`,`answer_task`(TasksExecute) · `approve_plan`,`reject_plan`(PlansReview) · `review_pull_request`(CodeReviewReview) · `merge_pull_request`(CodeReviewMerge) · `start_autopilot`,`stop_autopilot`(TasksExecute; uncapped⇒BillingManage, answer_every_question⇒ProjectsManage) · `chat_with_agent`(AgentsChat).
A **project-bound** API key forces `BoundProjectId` and ignores the `project` argument.

### 2.12 SignalR hubs

| Path | Hub | Auth |
|---|---|---|
| `/hubs/projects` | `ProjectHub` (`src/Modules/Orkka.Modules.Projects/Features/Hubs/ProjectHub.cs`) | `[Authorize(Policy="HubAccess")]` = JWT **or** WorkerApiKey. Token via `?access_token=` for `/hubs/*` |
| `/hubs/tasks` | `TaskHub` (`src/Modules/Orkka.Modules.Tasks/Features/Hubs/TaskHub.cs`) | same |
| `/hubs/runner` | `HostedRunnerHub` (Worker) / `RunnerHub` (Local) — `RunnerProtocol.HubPath` | runner bearer token, screened in the hub; hosted hub **refuses runners without the `hybrid` capability** (`HostedRunnerHub.cs:84`) |
| `/hubs/terminal` | `TerminalHub` | Local host only |
| `/hubs/apprun` | `AppRunHub` | Local host only |

**Client→server methods**: `JoinProject/LeaveProject`; `JoinTask/LeaveTask`, `JoinBoard/LeaveBoard`, `JoinAgentChat/LeaveAgentChat`, `JoinAppRun/LeaveAppRun`. Every join checks org ownership + project membership (HubException "Access denied."). Every connection auto-joins `user:{clerkUserId}`.
**Broadcast methods (service account only)**: `Broadcast*` — one per event.
**Server→client events** (25): `ProvisioningStep, ProvisioningStepError, ProjectReady, ProjectFailed, DeploymentStatus, DesignBriefUpdated` (ProjectHub); `StreamEvent, LaneChanged, StatusChanged, NewMessage, TaskLastMessageChanged, TaskCreated, TaskUpdated, TaskDeleted, TaskMergeFailed, TaskDependenciesChanged, MilestoneChanged, MilestonePlanChanged, AutopilotChanged, PlanReviewed, CodeReviewed, WorkspaceChanged, NotificationCreated, AgentChatMessage, AppRunOutput, AppRunState` (TaskHub).
Groups: `project:{id}`, `board:{projectId}`, `task:{id}`, `conversation:{id}`, `user:{userId}`, `apprun:{projectId}`.

### 2.13 Worker internal / relay endpoints (cluster-internal, **no auth middleware**, shared-secret per request)

`src/Orkka.Worker/Runner/RunnerRelayEndpoints.cs` — `POST /internal/runner/`: `chat-workspace, repo-access, chat, editors, open-in-editor, utility, apprun/attach, apprun/start, apprun/stop, apprun/input, apprun/resize`
`src/Orkka.Worker/Runner/LocalFolderRelayEndpoints.cs` — `POST /internal/runner/`: `fs/browse, fs/init-repo, fs/prepare-repo, repo/ensure-clone, repo/attach-remote, repo/sync-status, repo/push, pr/merge, pr/diff, pr/working-diff, pr/conflicts` (the code-change diff is read live through `pr/diff` / `pr/working-diff`)
(The cloud-job callbacks `/internal/agent-runs/*` were removed with cloud execution.)
Relay target resolution: `http://{pod}.<release>-worker:8080` (`runnersRelayUrlTemplate`). **Never routed by the ingress.**

## 3. Authentication for automated tests

### 3.1 Scheme composition (`src/Orkka.AppHost/Program.cs:113-180`)

Default scheme is a **policy scheme `OrkkaBearer`** that forwards on the bearer prefix:

```
Authorization: Bearer orkka_key_…  → OrgApiKey scheme
Authorization: Bearer <anything else> → Clerk JwtBearer
```
Opt-in per endpoint/hub: `WorkerApiKey` (`WorkerAuth:ApiKey`, service account), `ProjectToken` (`orkka_proj_…`, `/platform/v1/*` only).

Clerk validation: `Authority` from `Clerk:Authority`, `ValidateAudience=false`, `ValidateIssuer=true`, `NameClaimType=sub`, **plus `ClerkTokenPartyPolicy`** — web tokens must carry an `azp` in `Clerk:AuthorizedParties`; native tokens a `client` claim in `AuthorizedClients` (prod: `orkka-mobile`). Production **throws at startup** if either is empty. SignalR reads the token from `?access_token=` on `/hubs/*`.

`CurrentUserMiddleware` (`src/Orkka.AppHost/Auth/CurrentUserMiddleware.cs`) then resolves the org member (cached), **auto-provisioning a fresh org on first request** (`ResolveOrProvisionAsync`) — so a brand-new Clerk user hitting any endpoint gets an org with `Free` plan and 10,000 free credits. `OrgSuspensionMiddleware` then 403s `{"error":"account_suspended"}` for suspended orgs (platform admins bypass).

### 3.2 Org API keys — `orkka_key_…`

- Format: `orkka_key_` + 32 random bytes base64url (`ApiKeyValues.cs`). Only `SHA-256` hex hash is persisted; raw value shown once. `Prefix` = first 16 chars, for display.
- Create: `POST /api/me/api-keys` `{ name, permissions: ["TasksCreate", …], projectId?: "<slug or ulid>" }` → `{ key: {...}, rawKey }`.
  - **An API key cannot create an API key** (`OrgApiKeyAuthHandler.ClaimType` check → 403).
  - **Anti-escalation**: `MemberManagementRules.CanGrant` — you can only grant permissions you hold. Owners may grant any.
  - Max **10 active keys per member** → 409.
  - Unknown permission / empty set / unknown project → 400.
- Validate (`src/Modules/Orkka.Modules.Organizations/PublicApi/OrgApiKeyValidator.cs`):
  **effective permissions = key's snapshot ∩ member's live org-wide permissions** (org-wide only — `p.ProjectId == null`). Owners get exactly what the key requested. Revoked key, missing member, or **suspended org** ⇒ null ⇒ 401. `LastUsedAt` stamped fire-and-forget.
- Identity it assumes (`OrgApiKeyAuthHandler.cs:48-55`): the key's creator, `Role = Member` (**never Owner**), the intersected permission set, `IsPlatformAdmin = false`. Claims: `sub`, `org_id`, `api_key_id`, `org_api_key=true`, and `api_key_project` when bound.
- **`CurrentUserMiddleware` short-circuits for API-key requests** — no membership refresh, no `IsPlatformAdmin`, no activity touch, and **no `OrgSuspensionMiddleware` flag** (suspension is instead enforced inside the validator).

### 3.3 Which endpoints accept an API key? — **All of them.**

Because the key is wired as a target of the *default* policy scheme (not as an opt-in `AuthSchemes`), **any endpoint that requires a merely-authenticated caller accepts an `orkka_key_…` bearer** — the whole `/api/*` surface plus `/mcp`. It is *not* limited to MCP/board ops.

Consequences to encode in the test catalogue:
- **Reads**: every read works (reads are open to all org members).
- **Writes**: only where the key's effective permission set covers the endpoint's `Permission`.
- **Owner-only endpoints are always 403 for an API key**: `PUT /api/org`, `PUT /api/org/settings`, `PATCH /api/org/members/{id}/role`. `IsOwner` is false by construction.
- **Platform-admin endpoints (`/api/admin/*`) are always 403** for an API key (`IsPlatformAdmin = false`).
- `POST /api/me/api-keys` is 403 for an API key.
- **Project binding (`api_key_project`) is enforced ONLY in MCP** (`McpHosting.cs:83` → `McpCaller.BoundProjectId`, used at `OrkkaMcpToolServer.cs:171/190/375`). **Nothing reads that claim on the REST path** — a project-bound key can still hit any project in its org over REST. Worth an explicit test case (and possibly a bug report).
- SignalR hubs **do not** accept API keys: the `HubAccess` policy lists only `JwtBearer` + `WorkerApiKey`.
- `/platform/v1/*` does **not** accept API keys (`AuthSchemes("ProjectToken")`).

**Answer to the core question:** yes — you can drive essentially the whole REST API and MCP for e2e with a single `orkka_key_…` minted by an **Owner** with all 18 permissions. The gaps that still need a real Clerk session token are: (1) org profile/settings writes and role changes, (2) minting further API keys, (3) `/api/admin/*`, (4) SignalR real-time assertions, (5) the invite-accept flow (needs the invitee's own Clerk identity + verified email). Practical pattern: bootstrap once with a Clerk session token (mint the key, set org plan), then run the bulk of the catalogue on the key.

## 4. Existing test infrastructure

### `tests/Orkka.Tests` — 295 files, pure unit
Folders: `AgentRunner, Architecture (ModuleBoundaryTests, EntityConventionTests, CacheKeyConventionTests), Auth (ClerkTokenPartyPolicyTests, CurrentUserMiddlewareTests, WorkerApiKeyAuthHandlerTests), Billing, Caching, Composition, Constraints, Converters, Email, Execution, Features, Git, Hosting, Hubs, Middleware, Persistence, Projects, Services, Telemetry, ThirdParty, Utilities, Worker`.

### `tests/Orkka.IntegrationTests` — 120 files, in-process + Testcontainers
`OrkkaApiFactory : WebApplicationFactory<Program>` + **`Testcontainers.PostgreSql` (postgres:17-alpine)**, real EF migrations for all 10 module DbContexts, xUnit collection `"Integration"` shared across most classes.

Overrides in the factory (`Fixtures/OrkkaApiFactory.cs`):
- Auth replaced by `TestAuthHandler` — **every request is the fixed test owner** (`TestCurrentUser.TestOrgId/TestMemberId/TestClerkUserId`, `Role = Owner`, `IsOriginalRoot`). The API-key *handler* is therefore not exercised over HTTP; API-key tests call `IOrgApiKeyValidator` directly.
- `BillingDefaults:DefaultPlan = Enterprise`, `DefaultMode = Postpaid`, rate limits raised to 1,000,000/min, `Webhooks:AllowPrivateNetworks = true`, `App:BaseUrl = https://app.orkka.test`.
- Fakes/mocks: `MockGitHubAppService, MockVercelService, MockManagedDatabaseProvisioner, MockBlobStorageService, MockTaskNotifier, MockProjectNotifier, FakeChatAgentInvoker, FakeStripeGateway, FakeClerkHttpHandler, FakeWebhookReceiver, FakeSlackApi, FakeModelSyncSources, FakeBugReportClassifier, RecordingResendClient, StubGitWorkspaceManager, NullGitRepoReader`.

**There is no test anywhere that hits a live/deployed stack.** Everything is in-process. `EndpointDiscoveryTests` asserts `/openapi/v1.json` exposes ≥ 66 paths as a smoke test for missing FastEndpoints assemblies (that floor is badly stale vs the real ~314).

### Test-data seeding / cleanup
- No seeding endpoint. The only bootstrap is implicit: **first authenticated request auto-provisions an org**.
- **`DELETE /api/projects/{id}`** (`src/Modules/Orkka.Modules.Projects/Features/Projects/DeleteProjectEndpoint.cs`), requires `ProjectsDelete`:
  - **Unmanaged**: deletes the `Project` row, publishes `ProjectDeleted(id, orgId)`; other modules delete their project-scoped rows (tasks, agents, conversations, diagrams) in response. Nothing external is touched — the user's forge repos and local folders are untouched.
  - **Managed**: additionally enqueues a **`DeprovisioningJob`** in the *same* `SaveChanges` (`DeprovisioningJobFactory.CreateAsync`) snapshotting Vercel project id, GitHub repo, database slug, auth config, platform tokens and blob container — because after the delete the row that knew the ids is gone. The Worker's deprovisioner then removes the Vercel project, the GitHub repo, the Neon database, the platform tokens/files, idempotently, and marks the job `Completed`. Covered by `tests/Orkka.IntegrationTests/ManagedDeprovisioningTests.cs`.
  - **Note**: the HTTP call returns 204 immediately; external teardown is asynchronous — an e2e suite must poll or accept eventual consistency.
- **Org deletion: there is no endpoint.** Only `POST /api/admin/orgs/{orgId}/suspend` / `/unsuspend` (platform admin). E2E cleanup therefore has to be per-project, or direct DB.

---

## 5. Lifecycle constraints that matter for tests

| Constraint | Value | Where |
|---|---|---|
| Free plan project cap | **1** non-Managed project → **402** `plan_limit_projects` | `PlanOptions.cs:89`, `CreateProjectEndpoint.cs:38-70` |
| Free plan seats / repos | 1 seat, 2 repos per project | `PlanOptions.cs:89` |
| Team features gate | `TeamMembers` only on Enterprise — invites, roster, role/permission mgmt, per-project assignment | `PlanLimits.TeamMembers` |
| Signup credits | $10.00 USD = **10,000 credits** | `BillingAccount.SignupFreeCreditsUsd`, `CreditUnit.PerUsd = 1000` |
| First-subscribe grant | $5.00 (one-time, non-recurring) | `PlanOptions.SubscribeGrantUsd` |
| Hosted-app included credits | $2.00 / app / month, reset on renewal | `PlanOptions.HostedAppMonthlyCreditsUsd` |
| Monthly limit (Postpaid) | $250, `PauseOnLimit = true` | `BillingDefaults` |
| Provider capacity gate | `Worker:ProviderCapacityGate` (default **true**), re-probe every `Worker:ProviderCapacityProbeMinutes` (default 5). One row `PlatformSetting["ProviderCapacityPause"]`. **An unreadable row is treated as paused.** Pauses *all* platform-key execution | `ProviderCapacityGate.cs`, `DbProviderCapacityStore.cs` |
| Hosted-app trial | `ManagedApps:TrialDays` default **7**; `TrialEndsAt` set when the app first reaches Ready (not at brief start). Lapsed ⇒ `SuspendedAt` (Vercel deployment protection closes it to the public, data/repo/DB intact) ⇒ `GraceDays` ⇒ teardown. `TeardownNoticeStage` 0→1 (suspension notice) →2 (one week) →3 (last day); reset to 0 on promotion to paid | `GetPublicPricingEndpoint.cs:65`, `Project.cs:50-73`, `TrialLifecycleTests.cs`, `TrialLimitTests.cs` |
| Concurrent trials per org | **1** → 402 `trial_limit_apps` on `POST /api/projects/brief` | `StartBriefEndpoint.cs:25-58` |
| Max brief messages | **60** (not 30 — 30 *exchanges*) → 400 "Maximum message limit reached" | `SendBriefMessageEndpoint.cs:27,80` |
| Run concurrency (per project) | `ProjectSettings.MaxParallelTasks` default **3**. Four independent ceilings: `maxParallel` building, `maxParallel` reviewing, `planAheadDepth` planning ahead, and `(2*maxParallel)+planAhead` total active | `TaskQueueProcessor.cs:739-820`, `AutopilotProcessor.cs:238-246` |
| Rate limiting | `RateLimiting:GeneralPerMinute` **300**, `AiPerMinute` **30** (per authenticated org/user; applied after auth) | `src/Orkka.AppHost/RateLimiting.cs:50-81` |
| Runner presence window | 3 min (`RunnerPresenceRules.OnlineWindow`); daemon re-Hellos every 60s | `IRunnerFleet.cs` |
| Runner capabilities | `utility-provider, agent-resume, context-files, base-sync, merge-verify, working-tree-diff, cli-resume, hybrid, command-trust, app-run, app-run-prepare, editor, project-tools, chat-tools, local-folders, task-cancel, repo-access-check, developer-git, forge-cli, tracker-config, tracker-poll` (`setup-command`, `worktree-command` and `terminal` are retired) | `src/Orkka.LocalRunner.Contracts/RunnerProtocol.cs` (`RunnerCapabilities`) |
| Capability gates that change behaviour | `hybrid` → hosted hub **refuses** connect without it. `command-trust` → the host sends no setup/check/build/run-target command to a runner without it. `app-run` / `app-run-prepare` / `editor` / `local-folders` / `repo-access-check` → UI hides the action (`Supported:false`) rather than erroring | as above |
| Runner update requirement | `ILatestRunnerRelease` polls `https://api.github.com/repos/orkka-ai/orkka-local-cli/releases/latest` (tag `local-v0.3.21`), 1h success cache / 5min failure backoff; override `Runners:LatestDaemonVersion`. `RunnerVersions.IsNewer` drives an **advisory nudge** on `GET /api/me/runner` and `/api/org/runners`. Hard failures are per-operation: `Supported:false` → 422 *"Linking a folder needs a newer runner on that machine — run `./orkka update`"* | `src/Orkka.SharedKernel/Runners/LatestRunnerRelease.cs`, `RunnerProtocol.cs:545`, `RunnerPresenceEndpoints.cs:165,204`, `LocalRepoEndpoints.cs:305` |
| Org suspension | `403 {"error":"account_suspended"}` on every request; platform admins bypass; API keys die at validation | `OrgSuspensionMiddleware.cs` |
| Task lanes | `Drafting, Ready, Planning, PlanReady, PendingQuestions, Developing, Testing, CodeReview, Done` | `Tasks.Contracts/Enums.cs:128` |
| Autopilot stop reasons | incl. `Drained`, `ProjectUnavailable` | `Tasks.Contracts/Enums.cs` |
| API keys per member | 10 active → 409 | `ApiKeyEndpoints.cs` |
| Webhooks | auto-disabled after `Webhooks:DisableAfterFailures = 5`; private networks blocked unless `AllowPrivateNetworks` | `appsettings.json` |
| API graceful shutdown | `Host:ShutdownTimeoutSeconds = 90` (SSE turns held open) | `appsettings.json` |

---

## 7. Notes for the catalogue author

1. **Two credential tiers are unavoidable.** A Clerk session token (`await window.Clerk.session.getToken()` from `www.orkka.ai`, per `GO_LIVE.md:378`) is needed once per e2e account to mint an Owner API key and to cover org-settings/role/admin cases. Everything else runs on `orkka_key_…`.
2. **Two known asymmetries worth explicit test cases**: (a) project-bound API keys are not enforced on REST, only on MCP; (b) API-key requests skip `OrgSuspensionMiddleware` (suspension is caught in the validator instead — verify it actually 401s, not 200s).
3. **`EndpointDiscoveryTests` floor of 66 vs ~314 real paths** — a route-coverage assertion in the new suite would be more useful driven off `/openapi/v1.json` (non-prod only) or a generated route manifest.
4. **Async-completion endpoints** needing polling in e2e: project delete (deprovisioning job), provisioning (`GET …/provisioning` + `/hubs/projects`), milestone plan jobs (`GET …/milestone-plans/{jobId}`), design-brief generation, task pipeline (`/hubs/tasks`), webhook deliveries.
5. **SSE endpoints** (long-lived, 90s shutdown window): `POST …/conversations/{id}/messages`, brief messages, design-brief generation.
6. **Tests that need a live runner** (`hybrid` + `platform-git` + `managed-git`): everything under §1.4 rows 1, 2 and 5, `/api/runners/fs/*`, `/api/projects/{p}/repos/local`, `attach-remote`, app-run, editors, `repos/access-check`. `scripts/local-dev.ps1` + `./orkka connect --host https://runner.orkka.ai` is the way to stand one up against a hosted environment.