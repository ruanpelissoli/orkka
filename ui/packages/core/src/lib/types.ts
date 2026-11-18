import type {
  LaneKey,
  PriorityKey,
  MessageType,
  TaskTypeKey,
  TaskStatusKey,
  PlanStatusKey,
  AgentRoleKey,
  DiagramTypeKey,
  Permission,
} from "./constants";

export interface Organization {
  id: string;
  name: string;
  slug: string;
  settings: Record<string, unknown>;
  createdAt: string;
}

export type OrgRole = "Owner" | "Member";

export interface MemberPreferences {
  showTechnicalDetails: boolean;
  /** Set once the product tour is finished or skipped — it introduces the app once, on the
   *  first project, and never returns unless the user replays it from Preferences. */
  onboardingTourSeen: boolean;
  /** The interface language the member picked ("pt", "es", ...), or null when they never
   *  did. Unlike the flags above this is read on the server: the API prefers it over the
   *  request's Accept-Language when deciding which language the agents answer in, so a
   *  client that cannot send a reliable header still gets replies in the right language. */
  locale?: string | null;
}

export interface OrgMe {
  orgId: string;
  /** Display name of the org. Owner-editable; also what invite pages and emails show. */
  orgName: string;
  memberId: string;
  role: OrgRole;
  permissions: Permission[];
  preferences: MemberPreferences;
}

/**
 * GET /api/me/workspaces — one workspace (an Orkka org) the caller belongs to. A developer
 * who works several jobs has one per job; `isActive` is where a request without a
 * workspace lands.
 */
export interface Workspace {
  orgId: string;
  name: string;
  slug: string;
  /** "Owner" | "Member" */
  role: string;
  isActive: boolean;
}

/** PUT /api/org — the workspace's own identity (its name). */
export interface OrgProfile {
  orgId: string;
  name: string;
  slug: string;
}

// ── Runners (per-user ./orkka daemons connected to the hosted backend) ──

export interface RunnerToken {
  id: string;
  name: string;
  createdAt: string;
  lastUsedAt: string | null;
  lastMachineName: string | null;
  revoked: boolean;
  /** When the token stops working. A connected runner renews its own token before then. */
  expiresAt: string;
}

// ── Project memory ──────────────────────────────────────────────────

export type MemoryType = "Learning" | "Mistake" | "Pattern" | "Preference" | "Decision" | "Convention";
export const MEMORY_TYPES: MemoryType[] = ["Decision", "Convention", "Preference", "Learning", "Mistake", "Pattern"];

export interface ProjectMemory {
  id: string;
  projectId: string;
  /** Null = shared with the whole team. */
  agentId: string | null;
  agentName: string | null;
  type: MemoryType;
  title: string;
  content: string;
  source: "Agent" | "User" | "System";
  createdByUserId: string | null;
  taskId: string | null;
  isPinned: boolean;
  isArchived: boolean;
  useCount: number;
  lastUsedAt: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface MemoryPage {
  items: ProjectMemory[];
  nextCursor: string | null;
  total: number;
}

export interface MemoryListQuery {
  q?: string;
  type?: string;
  /** "team" or an agent id. */
  scope?: string;
  pinned?: boolean;
  archived?: boolean;
  cursor?: string;
  limit?: number;
}

export interface UpsertMemoryRequest {
  title: string;
  content: string;
  type: MemoryType;
  agentId?: string | null;
  isPinned?: boolean;
  isArchived?: boolean;
}

// ── Org API keys (public REST + MCP) ────────────────────────────────

export interface ApiKey {
  id: string;
  name: string;
  /** First 16 chars of the raw key. */
  prefix: string;
  permissions: Permission[];
  projectId: string | null;
  createdAt: string;
  lastUsedAt: string | null;
  revoked: boolean;
}

/** POST /api/me/api-keys — `rawKey` is returned exactly once. */
export interface CreateApiKeyResponse {
  key: ApiKey;
  rawKey: string;
}

export interface RunnerProviderStatus {
  provider: string;
  cliFound: boolean;
  authenticated: boolean;
  models: string[] | null;
  detail: string | null;
  /** How the CLI signs in for this workspace — see RunnerAuthMode. Null/absent from older runners. */
  authMode?: RunnerAuthMode | null;
  /** Non-secret qualifier: a region, a gateway/enterprise host, a variable name. Never a credential. */
  authDetail?: string | null;
  /** The installed CLI's version, as `<cli> --version` reports it. */
  cliVersion?: string | null;
}

/** RunnerProviderStatus.authMode values (Orkka.LocalRunner.Contracts.ProviderAuthModes). */
export type RunnerAuthMode =
  | "login"
  | "api-key"
  | "bedrock"
  | "vertex"
  | "foundry"
  | "azure-openai"
  | "gateway"
  | "token"
  | "enterprise-host"
  | "none";

/** GET /api/me/runner — all fields nullable when no runner has ever connected. */
export interface MyRunnerStatus {
  online: boolean;
  machineName: string | null;
  daemonVersion: string | null;
  connectedAt: string | null;
  lastSeenAt: string | null;
  providers: RunnerProviderStatus[] | null;
  /**
   * Provider rows from the runner's last Hello before it went offline — what the model
   * picker annotates from while the runner is down. Null when it has never connected.
   */
  lastKnownProviders: RunnerProviderStatus[] | null;
  /** When that last Hello was received (ISO). */
  lastKnownAt: string | null;
  lastKnownMachineName: string | null;
  hasActiveTokens: boolean;
  /**
   * Which GitHub account this machine pushes as. Absent from runners older than the field.
   * Agent pushes on a runner use the machine's own credential helper, so a wrong identity
   * only surfaces as a 403 after the agent has done all its work.
   */
  gitIdentity?: RunnerGitIdentity | null;
  /** What the connected daemon can do ("app-run", "editor", …). Absent from older runners —
   * treat missing as capable rather than hiding features from every existing user. */
  capabilities?: string[] | null;
  /**
   * gh / az / glab on this machine — what opens the developer's pull requests on their forge.
   * Null from runners older than the field.
   */
  forgeClis?: RunnerForgeCli[] | null;
  /**
   * Repository commands this machine was asked to run and has not approved (the runner's
   * CommandTrust). Read-only: approving happens at the terminal with
   * `./orkka commands approve <id>`. Absent until the host reports it.
   */
  pendingCommandApprovals?: PendingCommandApproval[] | null;
}

/** One repository command waiting for the developer's approval on this machine. */
export interface PendingCommandApproval {
  id: string;
  repo: string;
  command: string;
  /** "Setup" | "Check" | "Run" … — the runner's CommandKind. */
  kind: string;
  machine: string;
}

/** The forge CLIs a runner reports. */
export type ForgeCliName = "gh" | "az" | "glab";

/** One forge CLI as the runner's last Hello saw it. */
export interface RunnerForgeCli {
  cli: ForgeCliName | string;
  found: boolean;
  authenticated: boolean;
  /** Accounts the CLI is signed in as (gh can hold several). */
  accounts: string[] | null;
  /** The remedy, already worded for a human, when it is not ready. */
  detail: string | null;
}

// ── App runs (the hosted Run button) ────────────────────────────────────────

/** One started (or failed-to-start) target, as the Run panel renders it. */
export interface AppRunSnapshot {
  runId: string;
  key: string;
  label: string;
  kind: string;
  repoId: string;
  port: number | null;
  url: string | null;
  status: "running" | "exited" | "failed";
  exitCode: number | null;
  error: string | null;
  /** The start failed because something else holds the port — the one failure the panel
   * can offer to fix. */
  portConflict: boolean;
  startedAt: string;
  /** Base64 VT bytes printed before this tab attached. */
  scrollback: string | null;
  /** The target's port accepts connections (always true for a portless target). Until
   * then the "Live app" link reads "starting…" so nobody opens a dead tab. Absent from
   * older hosts — treat as ready. */
  ready?: boolean;
  /** Where to open the running app (URL + preview path); null when the target serves
   * nothing a browser can open, or the run is not running. */
  previewUrl?: string | null;
}

/** supported=false covers runner-offline and runner-too-old alike; error carries the
 * actionable sentence when the caller pressed a button rather than opened a panel. */
export interface AppRunState {
  supported: boolean;
  snapshots: AppRunSnapshot[];
  error?: string | null;
}

export interface RunnerGitIdentity {
  /** Account the machine's `gh` login holds; used when no per-URL username is configured. */
  defaultAccount: string | null;
  /** credential.useHttpPath — when false, ONE credential is shared across all of github.com. */
  scopedPerRepoPath: boolean;
  repos?: RunnerRepoGitIdentity[] | null;
  /** Actionable reason the setup will misbehave, or null when healthy. */
  detail: string | null;
}

export interface RunnerRepoGitIdentity {
  repoUrl: string | null;
  /** Null = falls back to `defaultAccount`. */
  account: string | null;
}

export interface Project {
  id: string;
  orgId: string;
  name: string;
  slug: string;
  /** Short uppercase key used in task codes, e.g. "ORK" in ORK-142 */
  key: string;
  description: string | null;
  settings: ProjectSettings;
  createdAt: string;
  updatedAt: string;
  taskCount?: number | null;
  /** The folder on the developer's machine the project was created from; null otherwise. */
  baseFolder?: string | null;
  /** What that folder is: one repository, or a folder of repositories. */
  baseFolderShape?: BaseFolderShape | null;
}

/** "monorepo": the base folder is one repository. "multirepo": it holds several. */
export type BaseFolderShape = "monorepo" | "multirepo";

/** A repository found in a base folder by the developer's runner (preview only). */
export interface DiscoveredRepo {
  /** Path inside the base folder, "/"-separated; "" when the folder itself is the repo. */
  relativePath: string;
  name: string;
  /** Credential-stripped origin URL, null when the repository has no origin. */
  originUrl: string | null;
  /** "remote": branch push + forge PR. "local": squash-merged into the folder. */
  location: "remote" | "local";
  forge: string | null;
  host: string | null;
  defaultBranch: string | null;
  currentBranch: string | null;
  isDirty: boolean;
  hasUnpushedCommits: boolean;
  /** A repository without commits cannot be registered yet. */
  hasCommits: boolean;
  description: string | null;
  /** A short stack hint, e.g. "Next.js" or ".NET". */
  stack: string | null;
}

/** supported=false: no runner could answer (offline or too old); `error` says which. */
export interface DiscoverReposResponse {
  supported: boolean;
  path: string | null;
  shape: BaseFolderShape | null;
  repos: DiscoveredRepo[];
  areas: string[];
  error: string | null;
}

export interface CreateProjectFromFolderRequest {
  path: string;
  name?: string;
}

export interface CreateProjectFromFolderResponse {
  project: {
    id: string;
    name: string;
    slug: string;
    key: string;
    baseFolder: string | null;
    baseFolderShape: BaseFolderShape | null;
  };
  repos: {
    id: string;
    name: string;
    gitUrl: string;
    defaultBranch: string;
    folderPath: string | null;
    location: "remote" | "local";
    workspaceStatus: string;
    description: string | null;
  }[];
  warnings: string[];
}

/** A repository's working-copy state, as the last rescan read it. */
export interface RescanRepoStatus {
  repoId: string;
  isDirty: boolean;
  hasUnpushedCommits: boolean;
  currentBranch: string | null;
}

/** skipped: an automatic rescan that did not run (one ran recently, or the runner is offline). */
export interface RescanProjectResponse {
  skipped: boolean;
  offline: boolean;
  added: string[];
  missing: string[];
  restored: string[];
  originUpdated: string[];
  warnings: string[];
  repos: RescanRepoStatus[];
}

export interface ProjectSettings {
  maxParallelTasks: number;
  sameRepoParallelism: boolean;
  defaultTokenLimit?: number;
  defaultModel?: string;
  /** When true, an approving agent code review completes and merges the task automatically. */
  autoApproveCodeReview: boolean;
  /** Plan review off: the dev agent's plan is approved on its own and work starts at once. */
  autoApprovePlan?: boolean;
  /** Push the branch to its origin remote after a task merge. Not editable here; carried
   * because the settings PATCH replaces the whole object. */
  autoPushOnMerge: boolean;
  /** When true, finished dev work goes through a Testing lane where the QA agent tests it in a browser. Off by default (token cost). */
  qaEnabled: boolean;
  /** Command the QA agent runs to start the app under test (e.g. "npm run dev"). */
  qaRunCommand?: string | null;
  /** When true, finished dev work parks in Testing for the requester to verify manually before code review. Composable with qaEnabled (QA agent first, then the human). */
  manualVerificationEnabled: boolean;
  /** Show raw tool-call events in the task Agent Activity feed. Off by default — noise for non-technical users. */
  showAgentToolCalls: boolean;
  /** When true, a task whose work has settled is checked against the project's markdown documents, writing a new one or revising an outdated one. Off by default (token cost). */
  documentationSyncEnabled: boolean;
  /** Automation master switch: schedules and reminders. Off pauses everything without deleting it. Older APIs omit it (= on). */
  automationEnabled?: boolean;
  /** Keep the app running: start the project's auto-select run targets when the project is
   * opened and nothing is running. null = not decided (on for Orkka-bootstrapped projects,
   * off for brought repos). */
  autoRunOnOpen?: boolean | null;
  /** When true, a task that has stood in Done for two weeks is archived automatically. Off by default. */
  autoArchiveDoneTasks?: boolean;
  /** When true, each agent run in this project is stopped after taskRuntimeLimitMinutes.
   * Off by default — a task runs for as long as the work takes. */
  taskRuntimeLimitEnabled?: boolean;
  /** Minutes an agent run may take while taskRuntimeLimitEnabled is on. Ignored when it is off. */
  taskRuntimeLimitMinutes?: number;
  /** When true, a task pauses and asks the requester to continue or stop once its estimated
   * list-price spend across all of its agent runs reaches taskSpendLimitUsd — mid-run, or before
   * a phase starts when the estimate already sits at the limit. Each run is priced at the
   * vendor-reported cost or the list price of its tokens; on a subscription nothing is actually
   * charged. Off by default. Continue grants one more allotment; the branch is kept as a checkpoint. */
  taskSpendLimitEnabled?: boolean;
  /** Estimated list-price spend a task may reach before it asks, in US dollars
   * (default 5, accepted 0.10 – 10000). Ignored when taskSpendLimitEnabled is off. */
  taskSpendLimitUsd?: number;
  /** Working language of the project's agents ("en" | "es" | "pt" | "fr" | "de"): chat replies,
   * narrated reasoning, plans, reviews and generated documents. Set at creation from the
   * creator's UI locale; edited on the project Settings page. Null/absent on projects that
   * predate the setting — those follow each requester's UI locale until a language is saved.
   * The navbar language switcher only changes UI text, never this. */
  language?: string | null;
}

// ── Schedules (Automation page) ─────────────────────────────────────

export type ScheduleKind = "Playbook" | "AutopilotStart" | "AutopilotStop" | "Reminder";
export type ScheduleRunStatus = "Running" | "Succeeded" | "Failed" | "Skipped";

export interface Schedule {
  id: string;
  projectId: string;
  name: string;
  kind: ScheduleKind;
  isEnabled: boolean;
  cronExpression: string | null;
  timeZoneId: string;
  fireAt: string | null;
  nextRunAt: string | null;
  lastRunAt: string | null;
  lastRunStatus: ScheduleRunStatus | null;
  agentId: string | null;
  skillId: string | null;
  message: string | null;
  conversationId: string | null;
  autopilotCapUsd: number | null;
  createdByUserId: string;
  runCount: number;
  consecutiveFailures: number;
  createdAt: string;
  updatedAt: string;
}

export interface ScheduleRun {
  id: string;
  scheduleId: string;
  status: ScheduleRunStatus;
  startedAt: string;
  finishedAt: string | null;
  conversationId: string | null;
  summary: string | null;
  error: string | null;
  trigger: string;
}

export interface UpsertScheduleRequest {
  name: string;
  kind: ScheduleKind;
  cronExpression?: string | null;
  timeZoneId?: string | null;
  fireAt?: string | null;
  agentId?: string | null;
  skillId?: string | null;
  message?: string | null;
  autopilotCapUsd?: number | null;
  uncapped?: boolean;
  isEnabled?: boolean;
}

/** The forge a remote repo lives on — decides which CLI (gh / az / glab) opens its PRs. */
export type RepoForge = "github" | "azuredevops" | "gitlab" | "bitbucket" | "other";

/** POST /api/projects/{id}/repos */
export interface AddProjectRepoRequest {
  name: string;
  /** https, ssh:// or scp-style (`git@host:owner/repo.git`, host aliases included). An https
   * URL carrying a password or token is rejected. */
  gitUrl: string;
  defaultBranch: string;
  /** Null/absent = detected from the URL's host. */
  provider?: RepoForge | null;
  /** Which forge CLI account opens this repo's pull requests; null = the CLI's active one. */
  cliAccount?: string | null;
}

/** GET /api/projects/{id}/repos/access-check — one repo as the caller's runner sees it. */
export interface RepoAccessStatus {
  repoId: string;
  name: string;
  ok: boolean;
  reason: string | null;
  advice: string | null;
}

/** supported=false = no runner could answer (offline or too old) — not a failure. */
export interface RepoAccessCheck {
  supported: boolean;
  repos: RepoAccessStatus[];
  error?: string | null;
}

export interface ProjectRepo {
  id: string;
  projectId: string;
  name: string;
  gitUrl: string;
  defaultBranch: string;
  /** "local" for a linked folder, else the forge: github / azuredevops / gitlab / bitbucket / other. */
  provider: "local" | RepoForge | string;
  /** Which forge CLI account opens this repo's pull requests; null = the CLI's active one. */
  cliAccount?: string | null;
  workspaceStatus: string;
  cardColor: string;
  /**
   * Shell command that prepares a fresh task worktree — installing dependencies, putting
   * env in place — so the app can actually start. Null when the repo needs none.
   */
  setupCommand: string | null;
  /**
   * Command the QA agent runs to start this repo's app under test. Null falls back to the
   * project-level setting, so single-repo projects configured before this existed keep
   * working.
   */
  qaRunCommand: string | null;
  /**
   * Command that proves this repo still compiles (e.g. `npm run build`). Run after an
   * automatic merge resolution, before the result is pushed — the compiler's output is what
   * lets the resolver fix a merge that looks clean but does not build. Null skips the check;
   * a check that cannot run never blocks a merge.
   */
  buildCommand: string | null;
  /**
   * Command that proves this repo's change is good — lint + typecheck + tests (e.g.
   * `npm run lint && npm test`). Run in the task worktree after every dev run, before the
   * PR/review; a failing check sends the task back to the developer agent with the output
   * (bounded cycles). Null means no gate; a check that cannot run never blocks.
   */
  checkCommand?: string | null;
  createdAt: string;
  /** Base-folder repos: the path inside the project's base folder ("" = the folder itself). */
  folderPath?: string | null;
  /** What this repository is for — a routing hint agents see beside the project's other repos. */
  description?: string | null;
  /** When a rescan last failed to find it in the base folder; null while it is there. */
  missingSince?: string | null;
  /** "remote" (branch push + forge PR) or "local" (squash-merged into the folder). */
  location?: "remote" | "local";
}

export type RunTargetKind =
  | "web"
  | "api"
  | "mobile"
  | "desktop"
  | "worker"
  | "database"
  /** A supporting service (Postgres, Redis) started via docker compose; dependents wait for its port. */
  | "infra"
  | "other";

/**
 * One app the Run button can start on the user's own machine, worked out once by reading the
 * repos (or read from the repo's own `orkka.json`). Never carries environment variables: a
 * target says how to START the app, and the .env it needs is already in the working copy
 * the user runs it from — or gets created there from `.env.example`.
 */
export interface RunTarget {
  /** Stable slug, unique per project — the identity dependsOn refers to, and the one the
   * runner uses to recognise (and replace) an already-running process. */
  key: string;
  label: string;
  kind: RunTargetKind;
  repoId: string;
  command: string;
  /** Relative to the repo root — monorepos put the web app in "apps/web". */
  workingDirectory: string | null;
  /** Run before the command when dependencies are evidently missing (a JS app with no node_modules). */
  installCommand: string | null;
  port: number | null;
  url: string | null;
  /** Other target keys that should start first. */
  dependsOn: string[];
  /** Something the user must do themselves first (boot an emulator, start Docker). */
  manualStep: string | null;
  /** Whether a plain Run press includes this target. */
  autoSelect: boolean;
  /** Run before the process is killed (`docker compose stop …`). Absent from older configs. */
  stopCommand?: string | null;
  /** Restart this target when a task merges — for processes that do not hot-reload. */
  restartOnMerge?: boolean;
  /** Path opened first in the browser, appended to url ("/" when null). */
  previewPath?: string | null;
}

export interface ProjectRunConfig {
  targets: RunTarget[];
  notes: string | null;
  /** "manifest" = read from the repo's own orkka.json, no model involved. */
  source: "ai" | "manual" | "manifest";
  detectedAt: string | null;
  /** The repos changed since detection — the UI offers a re-detect, never runs one. */
  stale: boolean;
  /** False when this project has never been analysed; the panel shows the call to action. */
  detected: boolean;
}

/** What a developer needs to clone a project repo onto their own machine. */
export interface RepoCloneAccess {
  /** Ready-to-run `git clone …`. */
  command: string;
  cloneUrl: string;
  defaultBranch: string;
}

/** One directory on the runner's machine, as offered by the folder picker. */
export interface DirectoryEntry {
  name: string;
  fullPath: string;
  /** Already a git repository — selectable as-is, rather than needing a `git init`. */
  isGitRepo: boolean;
}

/**
 * A directory listing from the caller's own machine.
 *
 * `supported` is false whenever no machine can answer — the runner is offline, or too old
 * for the local-folder ops. Both mean the same thing to the UI: offer adding the repo by
 * URL instead of an error the user cannot act on from a browser.
 */
export interface DirectoryListing {
  supported: boolean;
  /** Null at the top level, where entries are the filesystem roots. */
  path: string | null;
  parentPath: string | null;
  entries: DirectoryEntry[];
  error: string | null;
}

/** A folder on the user's machine, now linked to the project as a repository. */
export interface LinkedLocalRepo {
  id: string;
  name: string;
  /** The absolute path on that machine — this repo's `gitUrl`. */
  path: string;
  defaultBranch: string;
  /**
   * Set only when linking turned a plain folder into a repository: what the import kept
   * out of it (dependency folders, build output, .env). Those files are still on disk —
   * they are simply outside the project's history, which is the part worth saying out loud.
   */
  excludedPaths?: string[] | null;
  /** Where the folder was copied before the import touched it, when a copy was made. */
  snapshotPath?: string | null;
}

/** How a linked local repo stands against its own origin remote. */
export interface RepoSyncStatus {
  supported: boolean;
  isGitRepo: boolean;
  /** False means the folder has no origin yet — the "publish to a remote" case. */
  hasRemote: boolean;
  hasUpstream: boolean;
  branch: string | null;
  ahead: number;
  behind: number;
  error: string | null;
  /** The origin URL when the folder has one — what a push authenticates to. */
  remoteUrl?: string | null;
}

export interface Task {
  id: string;
  orgId: string;
  projectId: string;
  /** Per-project sequential number, unique within the project */
  number: number;
  /** Display code like "ORK-142" */
  code: string;
  repoId: string;
  title: string;
  description: string;
  priority: PriorityKey;
  type: TaskTypeKey;
  status: TaskStatusKey;
  currentLane: LaneKey;
  branchName: string | null;
  /** Orkka's own review record (`orkka-local://pull/N`) — not a forge URL. */
  prUrl: string | null;
  /**
   * The pull request the developer's runner opened on the forge when the reviewed work landed
   * on a remote repo (gh / az / glab). Null for local folders and until it is published.
   */
  publishedPrUrl?: string | null;
  /** True once the task's branch merged to the base — reset is no longer possible */
  isMerged: boolean;
  /** Non-null = the last merge attempt failed (conflicts, branch protection, CI checks) */
  mergeFailureReason: string | null;
  /**
   * Non-null = the repository's setup command failed for this task's workspace, so the app
   * cannot start in it. Shown next to "open in editor", where the person about to run it is.
   */
  setupFailure?: string | null;
  queuePosition: number | null;
  tags: string[];
  contextFiles: string[];
  contextDocumentIds: string[];
  isArchived: boolean;
  isBlocked: boolean;
  /** Tasks this one waits on before agents pick it up ("Waiting on") */
  dependsOn: TaskDependencyItem[];
  /** Tasks that wait on this one ("Blocks") */
  dependents: TaskDependencyItem[];
  assigneeId: string | null;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  /** Flat repo info as serialized by the API's TaskResponse */
  repoName: string | null;
  repoCardColor: string | null;
  /**
   * Non-null = this task is one repository's half of a cross-repo task. The shared contract and
   * the merge gate live on the group, not on any member, so this is the only route to them.
   */
  groupId?: string | null;
  /** Clerk user id of the developer whose runner executes the task. */
  runnerUserId?: string | null;
  /** Explicitly pinned dev agent (null/absent = auto-assign by role/repo). */
  assignedAgentId?: string | null;
  /** Per-task model override for the dev agent (null/absent = the agent's own model). */
  model?: string | null;
  /**
   * Advisory cheaper-model suggestion from the PM review (a model id like "claude-haiku-4-5").
   * Rendered as an Apply/Dismiss chip; applying sets the per-task override, never the agent.
   */
  recommendedModel?: string | null;
  /** Milestone this task belongs to (null/absent = the Backlog). */
  milestoneId?: string | null;
  /**
   * True when this task is an interactive development session — a live chat that does real work
   * on its branch, driven by a human rather than by the queue. Set once at creation.
   */
  isInteractive?: boolean;
  /** Non-null only while that session is open; cleared when it finishes or is abandoned. */
  interactiveSessionStartedAt?: string | null;
  /**
   * Non-null between pressing Finish and the worker landing the work — the window where the
   * session is wrapping up and Finish must not be offered again.
   */
  interactiveFinishRequestedAt?: string | null;
  /** Key of the tracker ticket this task was imported from ("SHOP-12" / "4567"). Null for
   * tasks made on the board. */
  externalKey?: string | null;
  /** The ticket in Jira / Azure DevOps. */
  externalUrl?: string | null;
  /** The ticket's epic, as "SHOP-1 Checkout". */
  externalEpic?: string | null;
  /** Set when Orkka gave up moving the ticket's status in the tracker (e.g. "Couldn't move
   * SHOP-12 to “In Review” in Jira"); cleared when a later move succeeds. */
  externalSyncError?: string | null;
  /** A Story member (groupId is the story): its place in the story. */
  storyOrder?: number | null;
  /** A Story member's story ticket key ("SHOP-10") — board cards only. */
  storyKey?: string | null;
  /** How many sub-tasks the member's story has — board cards only. */
  storyCount?: number | null;
}

/** One edge of a task's dependency graph, seen from the task being viewed */
export interface TaskDependencyItem {
  taskId: string;
  number: number;
  code: string;
  title: string;
  status: TaskStatusKey;
  /** A Completed-but-unmerged blocker still blocks — dependents build on merged code */
  isMerged: boolean;
  /**
   * Both ends belong to the same cross-repo group, so this edge clears at Completed rather
   * than at merged: siblings code against the group's contract, not each other's merged
   * source. Ignore it and a running group reads as permanently blocked.
   */
  sameGroup: boolean;
  reason: string | null;
  source: "Ai" | "Manual";
}

/** Lightweight blocker reference carried on board cards and realtime events */
export interface BlockingTaskRef {
  taskId: string;
  code: string;
  status: TaskStatusKey;
  /** False for a Done blocker means its PR hasn't landed yet — still blocking */
  isMerged: boolean;
}

/** Fresh both-direction dependency lists returned by the add-dependency endpoint */
export interface TaskDependenciesResponse {
  dependsOn: TaskDependencyItem[];
  dependents: TaskDependencyItem[];
}

/** Lightweight task shape returned by the board endpoint */
export interface TaskSummary {
  id: string;
  number: number;
  code: string;
  title: string;
  priority: PriorityKey;
  type: TaskTypeKey;
  status: TaskStatusKey;
  currentLane: LaneKey;
  branchName: string | null;
  prUrl: string | null;
  queuePosition: number | null;
  tags: string[];
  repoId: string;
  repoName: string | null;
  repoColor: string | null;
  /** Non-null = one repository's half of a cross-repo task. */
  groupId?: string | null;
  assigneeId: string | null;
  assigneeName: string | null;
  assigneeAvatarUrl: string | null;
  isBlocked: boolean;
  /** Unmet dependencies — non-empty means agents skip this task until they complete */
  blockedBy: BlockingTaskRef[];
  /** Non-null = the last merge attempt failed (conflicts, branch protection, CI checks) */
  mergeFailureReason: string | null;
  hasUnread?: boolean;
  createdAt: string;
  /** When the task reached Done — drives the Code View's "recently done" window. Absent on
   * older APIs, in which case createdAt stands in. */
  completedAt?: string | null;
  /** Clerk user id of the developer whose runner executes the task. */
  runnerUserId?: string | null;
  /** Newest agent message, truncated server-side for the card preview */
  lastAgentMessage?: TaskLastAgentMessage | null;
  /** Milestone this task belongs to (null/absent = the Backlog). Drives board grouping. */
  milestoneId?: string | null;
  /** True only when the board was asked to include archived tasks — cards badge it so an
   * archived card never reads as live work sitting in its lane. */
  isArchived?: boolean;
  /** Key of the tracker ticket this task was imported from ("SHOP-12" / "4567"). Null for
   * tasks made on the board. */
  externalKey?: string | null;
  /** The ticket in Jira / Azure DevOps. */
  externalUrl?: string | null;
  /** The ticket's epic, as "SHOP-1 Checkout". */
  externalEpic?: string | null;
  /** Set when Orkka gave up moving the ticket's status in the tracker (e.g. "Couldn't move
   * SHOP-12 to “In Review” in Jira"); cleared when a later move succeeds. */
  externalSyncError?: string | null;
  /** A Story member (groupId is the story): its place in the story. */
  storyOrder?: number | null;
  /** A Story member's story ticket key ("SHOP-10") — board cards only. */
  storyKey?: string | null;
  /** How many sub-tasks the member's story has — board cards only. */
  storyCount?: number | null;
}

/** Newest agent message on a task, truncated server-side for the board card preview */
export interface TaskLastAgentMessage {
  preview: string;
  messageType: string;
  agentName: string | null;
  createdAt: string;
}

export interface BoardLaneResponse {
  key: string;
  label: string;
  order: number;
  tasks: TaskSummary[];
}

export interface TaskBoardResponse {
  lanes: BoardLaneResponse[];
}

export interface TaskAttachment {
  id: string;
  taskId: string;
  fileUrl: string;
  fileType: string;
  fileName: string;
  fileSize: number;
  createdAt: string;
}

export interface TaskPlan {
  id: string;
  taskId: string;
  version: number;
  content: PlanContent;
  status: PlanStatusKey;
  feedback: string | null;
  approvedAt: string | null;
  createdAt: string;
}

export interface PlanContent {
  summary: string;
  approach: string;
  filesChanged?: { path: string; action: "create" | "modify" | "delete"; description: string }[];
  estimatedTokens?: number;
}

export interface TaskMessage {
  id: string;
  taskId: string;
  /** "tracker" = a comment copied from the task's Jira / Azure DevOps ticket. */
  role: "user" | "assistant" | "system" | "tracker";
  content: string;
  messageType: MessageType;
  senderId?: string;
  metadata: Record<string, unknown>;
  createdAt: string;
}

export interface TaskExecution {
  id: string;
  taskId: string;
  agentId: string | null;
  agentName: string | null;
  agentRole: string | null;
  lane: LaneKey;
  /** API serializes PascalCase (e.g. "Completed") — normalize before comparing */
  status: string;
  agentLog: string | null;
  filesChanged: { path: string; action: string }[];
  /** Prompt + completion only. NOT what was billed — see `billed`. */
  tokensUsed: number;
  promptTokens: number;
  completionTokens: number;
  cost: number;
  /**
   * What `cost` was actually computed from, straight off the usage rows. Null until the run
   * has usage (queued/running), so prefer it when present and fall back to `tokensUsed`.
   *
   * On agentic runs `cacheReadTokens` dwarfs everything else — showing `tokensUsed` beside a
   * dollar figure displays a low-single-digit percentage of the real volume.
   */
  billed: ExecutionTokenBreakdown | null;
  errorMessage: string | null;
  startedAt: string | null;
  completedAt: string | null;
  createdAt: string;
}

export interface ExecutionTokenBreakdown {
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheWriteTokens: number;
  /** Sum of all four buckets — the honest headline number. */
  totalTokens: number;
}

export type AgentStatsWindow = "24h" | "7d" | "30d";

/** Per-agent operational counters over a lookback window (GET /api/projects/{id}/agent-stats). */
export interface AgentStats {
  agentId: string;
  runs: number;
  succeeded: number;
  failed: number;
  running: number;
  avgDurationSeconds: number | null;
  lastRunAt: string | null;
  lastError: string | null;
  lastErrorAt: string | null;
}

export interface AgentStatsEnvelope {
  window: AgentStatsWindow;
  from: string;
  to: string;
  agents: AgentStats[];
}

/** How a metric moved against the previous window of equal length. */
export type MetricTrend = "Improving" | "Worsening" | "Flat" | "Unknown";

/**
 * The API decides the verdict, not the client — it is what knows a falling failure rate is good
 * and a falling ship count is not. `previous` is carried even when `changePct` is null, so a
 * young project can still read "vs 2" without being shown a meaningless percentage.
 */
export interface MetricDelta {
  previous: number | null;
  changePct: number | null;
  trend: MetricTrend;
}

/**
 * Headline numbers for the board strip. Wider than the four cells the UI shows, so which four
 * are on screen stays a client decision.
 */
export interface BoardMetricsStrip {
  shipped: number;
  /** Agent seconds over each shipped task's whole life, not just the window. */
  agentSecondsPerShippedTask: number | null;
  avgRunSeconds: number | null;
  /** Agent runs only — chat, design and brief spend is excluded, so this is lower than the
   * Settings → Usage project total. Label it as agent spend or it reads as a discrepancy. */
  agentSpend: number;
  wastedSpend: number;
  /** The only signal with content when cost is structurally zero (subscription, runner runs). */
  tokens: number;
  runs: number;
  completed: number;
  failed: number;
  cancelled: number;
  running: number;
  tasksTouched: number;
  /** Over finished runs, so it does not dip when a run is in flight. 0..1. */
  failureRate: number | null;
}

export interface BoardMetricsDeltas {
  shipped: MetricDelta;
  agentSecondsPerShippedTask: MetricDelta;
  avgRunSeconds: MetricDelta;
  agentSpend: MetricDelta;
  wastedSpend: MetricDelta;
  failureRate: MetricDelta;
}

/**
 * Agent time in one lane — the sum of execution spans, NOT how long cards wait there. There is
 * no lane-transition history, so dwell time is not a number this platform has.
 */
export interface BoardLaneMetric {
  /** Averaged over (task, lane) groups: a lane re-entered after a bounce is one experience. */
  avgAgentSeconds: number;
  runs: number;
  tasks: number;
  /** Every span in the lane added up — what the column burned, not what a task costs. */
  totalAgentSeconds: number;
  /** Billed tokens with the cache split, over this lane's runs in the window. */
  tokens: number;
  /** Over this lane's finished runs, 0..1. Null while nothing has finished — not zero. */
  failureRate: number | null;
  finishedRuns: number;
}

export interface BoardMetricsResponse {
  window: AgentStatsWindow;
  from: string;
  to: string;
  previousFrom: string;
  current: BoardMetricsStrip;
  deltas: BoardMetricsDeltas;
  /** Keyed by board lane key (`code_review`). Sparse — human lanes are absent, not zero. */
  lanes: Record<string, BoardLaneMetric>;
}

/** One agent's spend in the Activity window. Run counts come from the execution rows. */
export interface ActivityAgentSpend {
  agentId: string;
  cost: number;
  /** Billed tokens including the cache split — unlike `ProjectExecutionRow.tokensUsed`. */
  tokens: number;
}

/**
 * Window totals, counted server-side over every execution in the window rather than over the
 * (capped) `executions` array beside it. Add these up yourself only when `truncated` is false.
 *
 * The status filter is not applied to these — the point of the strip is to say "3 of these failed"
 * while you are looking at something else. The agent filter is.
 */
export interface ActivitySummary {
  runs: number;
  running: number;
  completed: number;
  failed: number;
  cancelled: number;
  tasksTouched: number;
  durationSeconds: number;
  cost: number;
  /** Spend on failed or cancelled runs — money that bought nothing. */
  wastedCost: number;
  tokens: number;
  agents: ActivityAgentSpend[];
}

/**
 * How a task touched in the window turned out. The executions say what happened; this says whether
 * it worked. `mergeFailureReason`, `setupFailure` and the cycle counts are what the Activity page's
 * attention list is built from.
 */
export interface ActivityTaskRow {
  taskId: string;
  taskKey: string;
  taskTitle: string;
  currentLane: LaneKey;
  /** PascalCase TaskStatus name. */
  status: string;
  prUrl: string | null;
  isMerged: boolean;
  prClosedAt: string | null;
  mergeFailureReason: string | null;
  /** PascalCase MergeFailureKind name, when the merge is the thing that is stuck. */
  mergeFailureKind: string | null;
  setupFailure: string | null;
  reviewCycleCount: number;
  qaCycleCount: number;
  checkCycleCount: number;
  baseSyncCycleCount: number;
  hasAgentWorking: boolean;
  lastActivityAt: string;
}

/** One bar on the project Activity timeline (GET /api/projects/{id}/executions). */
export interface ProjectExecutionRow {
  executionId: string;
  taskId: string;
  taskKey: string;
  taskTitle: string;
  agentId: string | null;
  agentName: string | null;
  agentRole: string | null;
  lane: LaneKey;
  /** PascalCase execution status: Running | Completed | Failed | Cancelled */
  status: string;
  startedAt: string;
  completedAt: string | null;
  durationSeconds: number | null;
  tokensUsed: number;
  cost: number;
  errorMessage: string | null;
}

export interface ProjectExecutionsResponse {
  from: string;
  to: string;
  /** True when more runs exist in the window than `executions` carries. `summary` stays exact. */
  truncated: boolean;
  executions: ProjectExecutionRow[];
  summary: ActivitySummary;
  tasks: ActivityTaskRow[];
}

/** Mirrors the backend NotificationType enum — serialized as PascalCase names. */
export type NotificationTypeKey =
  | "PlanApprovalRequired"
  | "QuestionAsked"
  | "CodeReviewRequired"
  | "QaReviewRequired"
  | "TaskFailed"
  | "TaskMergeFailed"
  | "TaskCompleted"
  | "AgentReplied"
  | "VerificationRequired"
  | "RunnerOffline"
  | "MilestoneCompleted"
  // A completed milestone could not be merged into the default branch. Terminal until
  // someone retries, and it holds every milestone that depends on it — deep-links to
  // the milestone page, where the reason and the retry button are.
  | "MilestoneShipFailed"
  | "AutopilotStopped"
  // Interactive session paused on a command — pushed on every prompt (see orkka-api
  // ApprovalRequestedNotificationHandler); deep-links to the task's approval card.
  | "ApprovalRequested"
  // A schedule (playbook / autopilot window) failed; deep-links to the Automation page.
  | "ScheduleFailed";

/** Every notification type, in display order. */
export const NOTIFICATION_TYPE_KEYS: NotificationTypeKey[] = [
  "PlanApprovalRequired",
  "QuestionAsked",
  "CodeReviewRequired",
  "QaReviewRequired",
  "VerificationRequired",
  "ApprovalRequested",
  "TaskCompleted",
  "TaskFailed",
  "TaskMergeFailed",
  "AgentReplied",
  "MilestoneCompleted",
  "MilestoneShipFailed",
  "AutopilotStopped",
  "ScheduleFailed",
  "RunnerOffline",
];

export interface Notification {
  id: string;
  taskId: string | null;
  type: NotificationTypeKey;
  /** Task title (or agent name for AgentReplied) — display text is rendered client-side from `type` (localized). */
  title: string;
  /** Failure/merge reason (or conversation title for AgentReplied) when relevant. */
  body: string | null;
  projectId: string | null;
  projectSlug: string | null;
  taskNumber: number | null;
  /** AgentReplied deep-link target: the agent whose chat finished a detached reply. */
  agentId: string | null;
  conversationId: string | null;
  isRead: boolean;
  createdAt: string;
}

export interface NotificationList {
  items: Notification[];
  unreadCount: number;
}

// File tree types
export interface FileTreeNode {
  path: string;
}

export interface TreeNode {
  name: string;
  path: string;
  isDirectory: boolean;
  children: TreeNode[];
}

// Upload types
export interface UploadUrlResponse {
  uploadUrl: string;
  blobName: string;
}

export interface ConfirmAttachmentRequest {
  blobName: string;
  fileName: string;
  contentType: string;
  fileSize: number;
}

// Request types
export interface CreateProject {
  name: string;
  description: string;
  slug?: string;
  key?: string;
}

export interface CreateTask {
  projectId: string;
  repoId: string;
  title: string;
  description: string;
  priority: PriorityKey;
  type: TaskTypeKey;
  tags?: string[];
  contextFiles?: string[];
  contextDocumentIds?: string[];
  /** Pin the task to a specific dev agent (skips auto-assign). */
  assignedAgentId?: string;
  /** Run the dev agent on this model instead of its own (omitted = the agent's default). */
  model?: string;
  /** Skip the planning lane: the PM still refines the task, then it goes straight to Developing. */
  skipPlanning?: boolean;
  /** Create straight into a milestone (omitted = the Backlog). */
  milestoneId?: string;
  /**
   * Other repositories this one piece of work also has to change. Omitted or empty (the normal
   * case) creates an ordinary single-repo task.
   *
   * `repoId` stays required and keeps its meaning — it is where this task's own half lands, and
   * the task keeps its number and URL when the split runs at submission.
   */
  spansRepoIds?: string[];
}

/**
 * Opens an interactive session. Deliberately smaller than {@link CreateTask}: priority and type
 * are defaulted and the run goes to the caller's own runner, because a
 * session exists for work that was not worth filling in a form for.
 */
export interface StartInteractiveSession {
  repoId: string;
  /** Omitted/blank gets a placeholder — name the bug after describing it, not before. */
  title?: string;
  /** Pin the session to a specific dev agent (omitted = auto-assign by role/repo). */
  assignedAgentId?: string;
  /** Run the agent on this model instead of its own (omitted = the agent's own). */
  model?: string;
}

export interface FinishInteractiveSessionResult {
  taskId: string;
  finishRequestedAt: string;
}

/**
 * A command an interactive session has paused on, waiting for a human ruling. The agent is
 * blocked until one arrives or the prompt expires.
 */
export interface PendingApproval {
  id: string;
  taskId: string;
  toolUseId: string;
  /** The gated tool — "Bash" today. */
  tool: string;
  /** The exact command line. Shown verbatim: a paraphrase is not what is being approved. */
  command: string;
  /** The coarser shape an "always allow" would cover, e.g. `git push`. */
  approvalKey: string;
  createdAt: string;
}

export type ApprovalDecisionInput = "allow" | "allow_session" | "deny";

export interface CreateMessage {
  content: string;
  messageType?: MessageType;
}

export interface UpdateTaskRequest {
  title?: string;
  description?: string;
  priority?: PriorityKey;
  type?: TaskTypeKey;
  repoId?: string;
  tags?: string[];
  contextFiles?: string[];
  contextDocumentIds?: string[];
  skipPlanning?: boolean;
  /** Tri-state: omit = unchanged, "" = move to the Backlog, id = attach to that milestone. */
  milestoneId?: string;
  /** Tri-state: omit = unchanged, "" = back to auto-assign, id = pin to that dev agent. */
  assignedAgentId?: string;
}

/** Lifecycle of a milestone. Transitions are automatic except Cancelled. */
export type MilestoneStatusKey = "Planned" | "Active" | "Completed" | "Cancelled";

/**
 * Task counts under a milestone, computed server-side on every read.
 * `inFlight` is the remainder — neither finished nor waiting on a human.
 */
export interface MilestoneCounts {
  total: number;
  done: number;
  /** Work the pipeline can move right now — excludes unsubmitted drafts. */
  inFlight: number;
  /** Parked until a human acts — the same set the Attention Inbox uses. */
  needsAttention: number;
  /** Still in Drafting: planned but never submitted. Total is the sum of all four. */
  notStarted: number;
}

export interface Milestone {
  id: string;
  projectId: string;
  title: string;
  /** What is true when this is done. Null until someone writes one. */
  outcome: string | null;
  status: MilestoneStatusKey;
  /**
   * Roadmap order, and the only sequencing signal there is — milestones deliberately
   * carry no target date, since agent throughput makes human-set dates noise.
   */
  position: number;
  startedAt: string | null;
  completedAt: string | null;
  counts: MilestoneCounts;
  createdAt: string;
  /** Repo the work lands in. Null when it spans repos, or the project only has one. */
  repoId: string | null;
  /** Resolved server-side so a multi-repo roadmap can label cards without a join. */
  repoName: string | null;
  /** When the milestone shipped. Every milestone ships as it completes — each task lands on
   * the default branch as it merges, so there is no separate ship step. */
  shippedAt?: string | null;
}

/**
 * Why an autopilot session ended. Anything other than `Manual` happened while nobody was
 * watching, which is what the board's morning state has to explain.
 */
export type AutopilotStopReason =
  | "Manual"
  | "CapReached"
  | "BudgetBlocked"
  | "ConsecutiveFailures"
  | "Drained"
  | "ProjectUnavailable";

/**
 * Autopilot's state for one project. When `running` is false the counters describe the LAST
 * session, so the board can say how the night ended rather than showing an empty toggle.
 */
export interface AutopilotStatus {
  running: boolean;
  sessionId: string | null;
  startedAt: string | null;
  stoppedAt: string | null;
  /** Null while running. */
  stopReason: AutopilotStopReason | null;
  /** Null = uncapped. Cloud spend only — runner tasks cost nothing to bound. */
  capUsd: number | null;
  /** The session answers every question its tasks ask, guarded topics included. Optional
   * because an API from before the field omits it. */
  answerEveryQuestion?: boolean;
  spentUsd: number;
  tasksSubmitted: number;
  tasksCompleted: number;
  tasksFailed: number;
  /** Queued or running right now. */
  tasksInFlight: number;
  /** Parked for a person — the first thing to look at on returning. */
  tasksWaitingOnYou: number;
  /**
   * Submittable tasks left in Drafting. Zero with work still in flight is normal mid-wave
   * (client tasks blocked on their API), and the UI must say so rather than look stalled.
   */
  draftingRemaining: number;
}

export interface StartAutopilotRequest {
  /** Cloud spend ceiling for this session. */
  capUsd?: number;
  /** Explicit "no ceiling". Required — a missing cap is never read as unlimited. */
  uncapped?: boolean;
  /** Answer every question the session's tasks ask, guarded topics included. Needs ProjectsManage. */
  answerEveryQuestion?: boolean;
}

/** The roadmap: milestones in order, plus whatever hasn't been planned into one. */
export interface MilestoneListResponse {
  milestones: Milestone[];
  backlog: MilestoneCounts;
}

export interface CreateMilestoneRequest {
  title: string;
  outcome?: string;
  position?: number;
  /** Repo the work lands in. Omit for cross-repo, or on a single-repo project. */
  repoId?: string;
}

export interface UpdateMilestoneRequest {
  title?: string;
  outcome?: string;
  status?: MilestoneStatusKey;
  position?: number;
  /** Empty string clears it back to cross-repo; omit to leave it alone. */
  repoId?: string;
}

export interface PlanMilestoneRequest {
  /** Optional when `milestoneId` is set: that milestone's outcome is the goal and the server
   * derives it, so there is never a second copy free to drift from the outcome on the card. */
  goal?: string;
  /** Omitted = the project's first repo. */
  repoId?: string;
  /** Plan more work into an existing milestone instead of a new one. */
  milestoneId?: string;
}

/** One task in a proposal — not yet persisted, and freely editable before accepting. */
export interface PlannedTask {
  title: string;
  description: string;
  priority: PriorityKey;
  type: TaskTypeKey;
  /** Stable identifier within the proposal. Echo it back unchanged on accept — `dependsOn`
   * references these, not array positions, so removing a task can't slide an edge onto its
   * neighbour. */
  key: number;
  /** Keys of tasks that must finish before this one starts. Edges naming a task the user
   * removed are dropped server-side. */
  dependsOn?: number[] | null;
  /** Repository name this task belongs in. Echoed back on accept, where it is resolved to a
   * repo id. */
  repo?: string | null;
  /** Tasks sharing a key are two halves of one piece of work spanning repositories, and become
   * a cross-repo group on accept. Null on nearly every task. */
  contractKey?: number | null;
}

/** The interface crossing the boundary between one set of grouped tasks. */
export interface PlannedContract {
  key: number;
  contract: string;
}

/** The nine tokens the app's stylesheet and every generated asset share. */
export interface BrandPalette {
  primary: string;
  primaryHover: string;
  secondary: string;
  accent: string;
  background: string;
  surface: string;
  text: string;
  textMuted: string;
  border: string;
}

/** Where a brand kit's values came from. `Manual` is never regenerated over. */
export type BrandKitSourceKey = "Brief" | "Repository" | "Manual";

export interface BrandKit {
  palette: BrandPalette;
  displayFont: string | null;
  bodyFont: string | null;
  tone: string | null;
  source: BrandKitSourceKey;
}

export interface UpdateBrandKitRequest {
  palette: BrandPalette;
  displayFont?: string | null;
  bodyFont?: string | null;
  tone?: string | null;
}

/** A task under a milestone, as the detail page lists it. */
export interface MilestoneTask {
  id: string;
  number: number;
  title: string;
  lane: LaneKey;
  status: TaskStatusKey;
  priority: PriorityKey;
  type: TaskTypeKey;
}

/** An unfinished milestone earlier in the roadmap. */
export interface MilestoneNeighbour {
  id: string;
  title: string;
  status: MilestoneStatusKey;
}

export interface MilestoneDetail {
  milestone: Milestone;
  tasks: MilestoneTask[];
  /** Nearest earlier milestone that isn't finished. Drives a hint, never a block — planning
   * ahead is the user's call. */
  previousUnfinished: MilestoneNeighbour | null;
}

/** A proposal. Nothing exists server-side until it is accepted. */
export interface MilestonePlanResponse {
  milestoneTitle: string;
  outcome: string;
  tasks: PlannedTask[];
  /** The repo the plan was built against — echo it back on accept, or the created tasks have
   * no repo and can never be submitted. */
  repoId: string | null;
  /** Contracts for the cross-repo groups in `tasks`. Echo them back on accept, or the halves
   * are created as unrelated tasks with no shared contract and no merge gate. */
  contracts?: PlannedContract[] | null;
}

/** Lifecycle of a queued planning run. */
export type MilestonePlanJobStatus = "Pending" | "Running" | "Completed" | "Failed";

/**
 * Which phase a run has reached. Coarse and honest: the model call is one opaque request, so
 * there is no real percentage — what a waiting user can be told is which phase and how long.
 */
export type MilestonePlanStep = "Queued" | "ReadingProject" | "Planning" | "Ordering";

/**
 * `runner_offline`: planning runs on the developer's runner with their own AI subscription, and
 * none was reachable — starting `./orkka start` is the fix. `unusable` means retry is.
 */
export type MilestonePlanFailureCode = "runner_offline" | "unusable" | "error";

/**
 * A planning run.
 *
 * Planning takes 90s–210s, so it no longer happens inside the POST. The request queues a job
 * and returns this; progress arrives over the board SignalR group, and the finished `plan` is
 * read back from here — which is what lets a refresh, a reconnect, or a different device pick
 * up work the browser was no longer waiting on.
 */
export interface MilestonePlanJob {
  jobId: string;
  status: MilestonePlanJobStatus;
  /** Null once the run has finished. */
  step: MilestonePlanStep | null;
  milestoneId: string | null;
  repoId: string | null;
  /** Present only when `status` is `Completed`. Still a proposal — nothing exists server-side
   * until it is accepted. */
  plan: MilestonePlanResponse | null;
  failureCode: MilestonePlanFailureCode | null;
  error: string | null;
  createdAt: string;
  /** When a worker claimed it. Elapsed time is counted from here. */
  startedAt: string | null;
  completedAt: string | null;
}

/**
 * A forecast from measured history. Absent (204) when there isn't enough of it — below the
 * sample floor the whole forecast is suppressed rather than partially summed.
 */
export interface TaskForecast {
  /** Always null now: every run executes on the developer's own runner and subscription,
   * so a forecast is time-only. Kept for wire compatibility. */
  costP25: number | null;
  costMedian: number | null;
  costP75: number | null;
  /**
   * Agent time — the sum of execution spans. Deliberately NOT wall-clock-to-done, which is
   * dominated by how fast the human replies. Render it as "of agent time", never as an ETA.
   */
  agentSecondsP25: number;
  agentSecondsMedian: number;
  agentSecondsP75: number;
  lanesAssumed: string[];
  /**
   * "project" | "org" | "platform" — which fallback bucket produced the samples. "platform"
   * means similar tasks across Orkka, not this org: a fair first guess for a new org, and the
   * UI says so next to the number.
   */
  bucketLevel: string;
  minLaneSamples: number;
}

/** Everything the create form knows that moves the estimate. */
export interface DraftForecastRequest {
  type?: TaskTypeKey;
  model?: string;
  assignedAgentId?: string;
  skipPmReview?: boolean;
  skipPlanning?: boolean;
  skipTesting?: boolean;
  skipCodeReview?: boolean;
}

/** Remaining workload in a milestone. Not a completion date. */
export interface MilestoneForecast {
  costP25: number | null;
  costMedian: number | null;
  costP75: number | null;
  agentSecondsP25: number;
  agentSecondsMedian: number;
  agentSecondsP75: number;
  /** Tasks with enough history to forecast — can be lower than `tasksOpen`. */
  tasksCovered: number;
  tasksOpen: number;
  /** Loosest fallback any covered task needed: "project" | "org" | "platform". */
  bucketLevel?: string | null;
}

export interface AcceptMilestonePlanRequest {
  milestoneTitle: string;
  outcome?: string;
  tasks: PlannedTask[];
  repoId?: string;
  milestoneId?: string;
  contracts?: PlannedContract[];
}

export interface ReviewPlanRequest {
  decision: "Approved" | "Rejected";
  feedback?: string;
}

export interface TaskFilterParams {
  search?: string;
  repoId?: string;
  status?: string;
  isArchived?: boolean;
  page?: number;
  pageSize?: number;
}

// ── Pull Request types ───────────────────────────────────────────────

export interface PullRequestDetail {
  number: number;
  title: string;
  body: string | null;
  state: string;
  mergeable: boolean | null;
  merged?: boolean;
  draft: boolean;
  additions: number;
  deletions: number;
  changedFiles: number;
  headSha: string;
  baseBranch: string;
  headBranch: string;
  files: PrFile[];
  comments: PrComment[];
  reviews: PrReview[];
  /** The pull request published on the forge for a remote repo; null until then and for
   * local folders. Orkka never merges on a forge. */
  forgeUrl?: string | null;
}

/** 202 body from POST /pull-request/merge when conflicts handed off to the auto-resolver.
 *  A 204 (plain merge) yields undefined instead. */
export interface MergePrResult {
  merged: boolean;
  resolutionStarted: boolean;
  message: string | null;
}

/** POST /pull-request/sync — what the forge said and what Orkka did about it. */
export interface SyncPrResult {
  prState: "merged" | "closed" | "open";
  taskUpdated: boolean;
  message: string;
}

export interface PrFile {
  filename: string;
  status: string;
  additions: number;
  deletions: number;
  patch: string | null;
  previousFilename: string | null;
}

/** One permanently stored dev-cycle snapshot (no diff — fetched per snapshot). */
export interface CodeChangeSnapshotSummary {
  id: string;
  executionId: string | null;
  lane: string;
  capturedAt: string;
  branchName: string;
  baseBranch: string;
  fileCount: number;
  additions: number;
  deletions: number;
  isTruncated: boolean;
  /** True for the row mirroring a run in progress (overwritten on each capture); the API
   * lists it first. Absent on older APIs — treat as false. */
  isLive?: boolean;
  /** Capture counter of a live row; matches WorkspaceChangedEvent.revision. */
  revision?: number;
}

/**
 * A snapshot's diff, read live from the task owner's runner — Orkka stores no code. A finished
 * snapshot shows its branch as it is now (`currentBranch`); when the runner is offline or the
 * branch is gone, `diff` is empty and `unavailable` says why.
 */
export interface CodeChangeSnapshotDiff {
  id: string;
  capturedAt: string;
  branchName: string;
  baseBranch: string;
  isTruncated: boolean;
  diff: string;
  isLive?: boolean;
  revision?: number;
  currentBranch?: boolean;
  unavailable?: string | null;
}

export interface PrComment {
  id: number;
  body: string;
  path: string;
  line: number | null;
  authorLogin: string;
  authorAvatarUrl: string;
  createdAt: string;
}

export interface PrReview {
  id: number;
  state: string;
  body: string | null;
  authorLogin: string;
  authorAvatarUrl: string;
  submittedAt: string;
}

export interface AddLineCommentRequest {
  path: string;
  line?: number;
  body: string;
}

export interface SubmitCodeReviewRequest {
  decision: string;
  body?: string;
  comments?: { path: string; line: number; body: string }[];
}

// ── Repo insight types ───────────────────────────────────────────────

export interface RepoCommit {
  sha: string;
  author: string;
  date: string;
  subject: string;
}

export interface RepoBranch {
  name: string;
  sha: string;
  lastCommitSubject: string;
  lastCommitDate: string;
  isDefault: boolean;
  /** Exists only on the repo's remote — never checked out locally. */
  isRemote: boolean;
}

export interface RepoCommitDiff {
  sha: string;
  diff: string;
}

export interface RepoTreeEntry {
  name: string;
  path: string;
  isDirectory: boolean;
}

export interface RepoPrSummary {
  number: number;
  title: string;
  state: string;
  merged: boolean;
  draft: boolean;
  headBranch: string;
  baseBranch: string;
  htmlUrl: string;
  authorLogin: string;
  createdAt: string;
  mergedAt: string | null;
  taskId: string | null;
  taskNumber: number | null;
}

// ── Model catalog ────────────────────────────────────────────────────

/** A row of the admin-managed model catalog (GET /api/models). Rates live on the row in the
 * database but are deliberately not exposed here — that endpoint is public. */
export interface AiModel {
  /** The vendor model id, e.g. "claude-opus-5", "copilot/auto", "ollama/gpt-oss:20b",
   * "grok-4.6", "opencode/anthropic/claude-sonnet-4-6". */
  id: string;
  name: string;
  /** AiProvider enum name: "Anthropic" | "OpenAI" | "Google" | "GitHubCopilot" | "Ollama" |
   * "XAI" | "OpenCode". Kept as a plain string — a strict union breaks the moment a provider
   * ships. */
  provider: string;
  /** Always "Chat": Orkka generates no images, so the catalog lists no image models. */
  kind: "Chat";
  description: string;
  /** Can drive Playwright MCP browser tools — required for the QA role. */
  supportsMcp: boolean;
  /** False = retired. Still returned so a pinned agent renders a real label instead of a raw
   * id, and so the picker can mark it unselectable rather than silently replacing it. */
  isEnabled: boolean;
  /** Only runs through a vendor CLI on a connected runner — pickers hide these when none can run them. */
  requiresRunner: boolean;
  sortOrder: number;
}

// ── Agent types ──────────────────────────────────────────────────────

export interface ProjectAgent {
  id: string;
  projectId: string;
  /** Repo scope: empty = works across all repos, otherwise the subset it handles. */
  repoIds: string[];
  /** Assigned skills (project playbooks). Empty = none. Older APIs omit it. */
  skillIds?: string[];
  name: string;
  role: AgentRoleKey;
  avatar: string;
  model: string;
  systemPrompt: string;
  userInstructions: string;
  isDefault: boolean;
  /** Stored counters — no cache buckets. Prefer `billed` when present. */
  totalTokensIn: number;
  totalTokensOut: number;
  totalCostUsd: number;
  /**
   * Lifetime tokens from the usage rows, cache included — what the `totalCostUsd` list-price
   * estimate was computed from. Null when the agent has no usage rows; fall back to the counters above.
   */
  billed: AgentTokenBreakdown | null;
  tasksCompleted: number;
  tasksFailed: number;
  createdAt: string;
  updatedAt: string;
}

export interface AgentTokenBreakdown {
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheWriteTokens: number;
  /** Sum of all four buckets. */
  totalTokens: number;
}

export interface UpdateAgentRequest {
  name?: string;
  model?: string;
  userInstructions?: string;
  /** Repo scope: undefined = unchanged, [] = clear (all repos), ids = scope to that subset. */
  repoIds?: string[];
  /** Skills: undefined = unchanged, [] = none, ids = exactly that set (project skills only). */
  skillIds?: string[];
}

// ── Skills (project playbooks) ───────────────────────────────────────

/** A project-scoped SKILL.md-style playbook. Built-ins are seeded per project and can be
 * edited or disabled, never deleted. */
export interface Skill {
  id: string;
  projectId: string;
  /** `[a-z0-9-]{1,64}`, unique per project; also the chat command (`/slug`). */
  slug: string;
  name: string;
  description: string;
  content: string;
  isEnabled: boolean;
  isBuiltIn: boolean;
  assignedAgentIds: string[];
  createdAt: string;
  updatedAt: string;
}

export interface CreateSkillRequest {
  name: string;
  description: string;
  content: string;
  slug?: string;
  isEnabled?: boolean;
  agentIds?: string[];
}

export interface UpdateSkillRequest {
  name?: string;
  description?: string;
  content?: string;
  slug?: string;
  isEnabled?: boolean;
}

// ── Usage types (GET /api/usage/*) ───────────────────────────────────
// Runs use the developer's own AI subscription, so Orkka charges nothing: every cost here is
// an estimate at the model's list price, and is often 0. Tokens are the figure with content.

export interface UsageSummary {
  /** Estimated list-price cost of the period, in US dollars. Never a charge. */
  totalCostUsd: number;
  totalInputTokens: number;
  totalOutputTokens: number;
  totalCacheReadTokens: number;
  totalCacheWriteTokens: number;
  /** All four buckets — what the estimate was computed from. */
  totalTokens: number;
  taskCount: number;
}

export interface DailyUsageItem {
  /** yyyy-MM-dd */
  date: string;
  costUsd: number;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheWriteTokens: number;
  /** All four buckets. Use this for any "tokens" figure. */
  totalTokens: number;
  taskCount: number;
}

export type UsageGroupBy = "project" | "agent" | "model" | "task";

export interface UsageBreakdownItem {
  groupKey: string;
  groupName: string;
  /** Task-grouping rows only: the row is a real task the reader can open. */
  linkProjectId?: string | null;
  linkTaskNumber?: number | null;
  costUsd: number;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheWriteTokens: number;
  /** All four buckets. Use this for any "tokens" figure. */
  totalTokens: number;
  taskCount: number;
}

// ── Architecture Diagram types ──────────────────────────────────────

export interface ArchitectureDiagram {
  id: string;
  projectId: string;
  agentId: string;
  conversationId: string;
  messageId: string | null;
  title: string;
  type: DiagramTypeKey;
  diagramData: Record<string, unknown>;
  version: number;
  description: string | null;
  createdAt: string;
  updatedAt: string;
  /** Virtual Architecture-board folder ("" = root). */
  folderPath: string;
}

export interface UpdateDiagramRequest {
  title?: string;
  diagramData?: Record<string, unknown>;
  description?: string;
}

// ── Design Assets ────────────────────────────────────────────────────

export type DesignAssetKind = "Svg" | "Image" | "Gif";
export type DesignAssetStatus = "Pending" | "Approved" | "Rejected";

export interface DesignAsset {
  id: string;
  projectId: string;
  /** Null for user-uploaded assets — they have no authoring agent. */
  agentId: string | null;
  agentName: string | null;
  conversationId: string | null;
  messageId: string | null;
  name: string;
  fileName: string;
  kind: DesignAssetKind;
  mimeType: string;
  sizeBytes: number;
  prompt: string | null;
  sourceModel: string;
  parentAssetId: string | null;
  rootAssetId: string;
  version: number;
  status: DesignAssetStatus;
  feedback: string | null;
  /** Short-lived signed URL — refetch rather than persist. */
  url: string;
  /** Same signed URL with an attachment disposition — browsers save it under the
   * asset's file name instead of the id-based blob name. */
  downloadUrl: string;
  approvedAt: string | null;
  requestedByUserId: string | null;
  requestedByUserName: string | null;
  reviewedByUserId: string | null;
  reviewedByUserName: string | null;
  reviewedAt: string | null;
  createdAt: string;
  /** Inline SVG markup — present only on the single-asset endpoint for kind "Svg";
   * used to rasterize a PNG download client-side. */
  svgText?: string | null;
  /** Virtual gallery folder ("" = root). */
  folderPath: string;
}

/** A file uploaded as agent-chat context; referenced from messages via
 * visible "[attachment:<id> ...]" markers. */
export interface ChatAttachment {
  id: string;
  fileName: string;
  mimeType: string;
  sizeBytes: number;
  createdAt: string;
}

// ── Agent Chat types ────────────────────────────────────────────────

export interface AgentConversation {
  id: string;
  projectId: string;
  agentId: string;
  title: string | null;
  messageCount: number;
  lastMessageAt: string | null;
  createdAt: string;
  /** A detached agent reply is still being generated — reopening shows the thinking indicator. */
  hasActiveRun?: boolean;
}

export interface AgentMessageDiagram {
  id: string;
  title: string;
}

export interface AgentMessageTask {
  id: string;
  number: number;
  title: string;
}

export interface AgentMessageDocument {
  id: string;
  title: string;
}

export interface AgentMessageDesignAsset {
  id: string;
  name: string;
  kind: string;
  version: number;
  url: string;
}

export interface AgentMessage {
  id: string;
  conversationId: string;
  role: "user" | "assistant";
  content: string;
  createdAt: string;
  diagrams?: AgentMessageDiagram[] | null;
  tasks?: AgentMessageTask[] | null;
  documents?: AgentMessageDocument[] | null;
  designAssets?: AgentMessageDesignAsset[] | null;
}

export interface AgentMessageList {
  messages: AgentMessage[];
  hasMore: boolean;
}

// ── Task "Discuss" chat ─────────────────────────────────────────────

/** One message of a task's Discuss conversation (one conversation per task + agent). */
export interface TaskChatMessage {
  id: string;
  role: "user" | "assistant";
  content: string;
  createdAt: string;
}

export interface TaskChatAgent {
  id: string;
  name: string;
  role: string;
}

/** GET /api/tasks/{taskId}/chat?agentId= — the conversation is created lazily. */
export interface TaskChat {
  conversationId: string;
  taskId: string;
  agentId: string;
  agentName: string;
  agentRole: string;
  /** The task's own agent — what the picker starts on. */
  defaultAgentId: string;
  lane: string;
  /** A reply is still being generated (the panel was reopened mid-reply). */
  hasActiveRun: boolean;
  messages: TaskChatMessage[];
  agents: TaskChatAgent[];
}

/**
 * POST /api/tasks/{taskId}/chat/apply. `messageId` applies one assistant message;
 * `summarize` applies a utility-model summary of the discussion with `agentId`'s agent.
 */
export interface ApplyTaskChatRequest {
  messageId?: string;
  text?: string;
  summarize?: boolean;
  agentId?: string;
}

export type ApplyTaskChatAction = "answered" | "planChangesRequested" | "posted";

export interface ApplyTaskChatResult {
  lane: string;
  status: string;
  action: ApplyTaskChatAction;
  /** The task message the discussion became. */
  messageId: string;
  content: string;
}

// ── Project Documents ────────────────────────────────────────────────

export type DocumentSource = "Manual" | "Agent" | "Brief" | "Task";

/** A document a finished task's documentation pass created or revised. */
export interface TaskDocument {
  id: string;
  title: string;
  path: string;
  version: number;
  outcome: "Created" | "Updated";
}

export interface ProjectDocument {
  id: string;
  title: string;
  path: string;
  source: DocumentSource;
  currentVersion: number;
  createdAt: string;
  updatedAt: string;
  /** Set only on file documents (an uploaded PDF stored as-is); null for markdown. */
  fileName: string | null;
  fileContentType: string | null;
}

export interface ProjectDocumentDetail {
  id: string;
  projectId: string;
  title: string;
  path: string;
  /** Markdown body. Always empty on a file document — render the file instead. */
  content: string;
  source: DocumentSource;
  currentVersion: number;
  createdByUserId: string | null;
  sourceAgentId: string | null;
  createdAt: string;
  updatedAt: string;
  fileName: string | null;
  fileContentType: string | null;
  fileSizeBytes: number | null;
}

export interface DocumentRevisionMeta {
  version: number;
  title: string;
  path: string;
  changedByUserId: string | null;
  changedByUserName: string | null;
  changedByAgentId: string | null;
  changedByAgentName: string | null;
  requestedByUserId: string | null;
  requestedByUserName: string | null;
  sourceConversationId: string | null;
  sourceMessageId: string | null;
  createdAt: string;
  /** Set when a finished task's documentation pass wrote this revision. */
  sourceTaskId: string | null;
  /** Null when that task has since been deleted — the revision still names one. */
  sourceTaskTitle: string | null;
}

export interface DocumentRevisionDetail extends DocumentRevisionMeta {
  content: string;
}

export interface DiagramVersionMeta {
  version: number;
  title: string;
  description: string | null;
  changedByUserId: string | null;
  changedByUserName: string | null;
  changedByAgentId: string | null;
  changedByAgentName: string | null;
  requestedByUserId: string | null;
  requestedByUserName: string | null;
  sourceConversationId: string | null;
  sourceMessageId: string | null;
  createdAt: string;
}

export interface DiagramVersionDetail extends DiagramVersionMeta {
  diagramData: unknown;
}

export interface CreateDocumentRequest {
  title: string;
  path?: string | null;
  content: string;
}

export interface UpdateDocumentRequest {
  title?: string | null;
  path?: string | null;
  content?: string | null;
}

// Parsed diff structures (internal use)
export interface DiffHunk {
  oldStart: number;
  oldCount: number;
  newStart: number;
  newCount: number;
  header: string;
  lines: DiffLine[];
}

export interface DiffLine {
  type: "add" | "delete" | "context";
  content: string;
  oldLineNumber: number | null;
  newLineNumber: number | null;
}


// ── Merge resolution (non-tech merge page) ──────────────────────────────────

export type MergeResolutionStatus =
  | "none"
  | "resolved"
  | "resolving"
  | "awaitingChoice"
  // Environmental failure (dirty working repo, runner offline) — no strategy can fix
  // it; the page shows the reason and polls until the user's fix lets the merge through.
  | "blocked"
  // Automatic resolution ran its full budget, its output was verified, and the merge still
  // is not finished. Distinct from "awaitingChoice" on purpose: there, a choice will most
  // likely land it; here, the AI combine has already been tried and failed, so the page has
  // to say so rather than offering it again as the recommended path.
  | "exhausted";

/**
 * Why a merge is stuck, as the API classifies it. "Unknown" means automation is still on the
 * retry path — most failures are that, and none of them should be presented as terminal.
 * Null on tasks that failed before the API recorded a kind.
 */
export type MergeFailureKind =
  | "Unknown"
  | "EnvironmentBlocked"
  | "ResolutionExhausted"
  | "PushFailed"
  | "ForgeRejected";

export interface MergeConflictFileInfo {
  path: string;
  detail: string;
  choice: string | null;
}

export interface MergeConflictsInfo {
  status: MergeResolutionStatus;
  taskTitle: string;
  summary: string | null;
  files: MergeConflictFileInfo[];
  failureReason: string | null;
  pendingStrategy: string | null;
  /** Machine-readable companion to `failureReason`. Null on older failures. */
  failureKind: MergeFailureKind | null;
  /** When the running attempt began. Null unless one is running. */
  startedAt: string | null;
  /** A follow-up queued behind the running attempt; it starts when that one ends unmerged. */
  queuedStrategy: string | null;
  queuedGuidance: string | null;
}

export type MergeStrategy = "combine" | "task" | "current" | "perFile";

export interface ResolveMergeBody {
  strategy: MergeStrategy;
  guidance?: string;
  files?: { path: string; choice: "task" | "current" | "ai" }[];
}

// ── Attention Inbox ────────────────────────────────────────────────────────

/**
 * What the human owes the task. Kept open-ended (`string`) on purpose: the API may add
 * kinds, and an older client renders unknown ones with a neutral fallback rather than
 * dropping the item.
 */
export type InboxItemKind =
  | "plan_approval"
  | "question"
  | "code_review"
  | "qa_review"
  | "verification"
  | "merge_conflict"
  | "merge_failed"
  | "blocked"
  | "runner_offline"
  | "out_of_tokens"
  | "error";

export interface InboxItem {
  taskId: string;
  projectId: string;
  projectName: string;
  projectKey: string;
  code: string;
  number: number;
  title: string;
  kind: InboxItemKind | string;
  lane: LaneKey;
  status: string;
  priority: string;
  repoName: string | null;
  prUrl: string | null;
  createdByUserId: string | null;
  agentName: string | null;
  preview: string | null;
  waitingSince: string;
  /** Open tasks that cannot start until this one is resolved. */
  blockedDependents: number;
  /** Server-side ranking score; the list arrives already sorted by it. */
  score: number;
}

export interface InboxCount {
  kind: InboxItemKind | string;
  count: number;
}

export interface InboxResponse {
  items: InboxItem[];
  counts: InboxCount[];
  total: number;
}

export interface InboxFilterParams {
  scope?: "all" | "mine";
  projectId?: string;
  kind?: string;
  limit?: number;
}

// ── Proof-of-done evidence ─────────────────────────────────────────────────

export type EvidenceFileState = "delivered" | "missing" | "unplanned";

export interface EvidenceFileCheck {
  path: string;
  state: EvidenceFileState;
  plannedAction: string | null;
  plannedDescription: string | null;
}

export interface EvidenceVerdict {
  verdict: "pass" | "fail" | "approve" | "request_changes" | "unknown";
  content: string;
  agentName: string | null;
  createdAt: string;
}

export interface EvidencePlan {
  planId: string;
  version: number;
  status: PlanStatusKey;
  summary: string;
  approach: string;
  estimatedTokens: number | null;
  approvedAt: string | null;
}

export interface EvidenceRun {
  executionId: string;
  lane: LaneKey;
  status: string;
  agentName: string | null;
  errorMessage: string | null;
  filesTouched: number;
  tokensUsed: number;
  cost: number;
  durationSeconds: number | null;
  startedAt: string | null;
  completedAt: string | null;
}

export interface EvidenceAttachment {
  id: string;
  fileName: string;
  fileType: string;
  fileUrl: string;
  fileSize: number;
  createdAt: string;
}

export interface EvidenceTotals {
  totalTokens: number;
  totalCost: number;
  runCount: number;
  filesTouched: number;
  totalDurationSeconds: number | null;
  reviewCycleCount: number;
  qaCycleCount: number;
  checkCycleCount: number;
}

export interface TaskEvidence {
  taskId: string;
  code: string;
  title: string;
  lane: LaneKey;
  status: string;
  branchName: string | null;
  prUrl: string | null;
  isMerged: boolean;
  plan: EvidencePlan | null;
  fileChecklist: EvidenceFileCheck[];
  qaVerdict: EvidenceVerdict | null;
  codeReview: EvidenceVerdict | null;
  runs: EvidenceRun[];
  attachments: EvidenceAttachment[];
  totals: EvidenceTotals;
}

// ── Editor escape hatch ──────────────────────────────────────────────────

export interface EditorOption {
  key: string;
  displayName: string;
}

export interface EditorsResponse {
  /** False when the runner is offline or too old — the UI hides the action entirely. */
  supported: boolean;
  editors: EditorOption[];
}

export interface OpenInEditorResponse {
  openedWith: string;
}

// ── Cross-repo task groups ────────────────────────────────────────────────────

/** One repository's half of a cross-repo task. */
export interface TaskGroupMember {
  id: string;
  number: number;
  code: string;
  title: string;
  repoId: string | null;
  repoName: string | null;
  repoColor: string | null;
  currentLane: LaneKey;
  status: TaskStatusKey;
  prUrl: string | null;
  isMerged: boolean;
  /** The PR was closed without merging — this half will never land on its own. */
  prClosed: boolean;
  mergeFailureReason: string | null;
  /** A Story member: its place in the story and its sub-task key. */
  storyOrder?: number | null;
  externalKey?: string | null;
}

/** One repository's shared branch of a Story and the forge pull request opened from it. */
export interface TaskGroupBranch {
  repoId: string;
  repoName: string | null;
  branchName: string;
  prUrl: string | null;
  /** "none" until the first sub-task lands, then "draft"; "ready" after the last; then the forge's "merged" / "closed". */
  prState: "none" | "draft" | "ready" | "merged" | "closed";
  readyAt: string | null;
}

export type TaskGroupStatusKey =
  | "Splitting"
  | "Active"
  | "AwaitingMerge"
  | "Merged"
  | "PartiallyMerged"
  | "Cancelled";

/**
 * One piece of work split across repositories: the original request, the contract the halves
 * are coded against, and where each half has got to.
 */
export interface TaskGroup {
  id: string;
  title: string;
  request: string | null;
  /** Markdown. Binding on every member, and written before any of them started. */
  contract: string | null;
  contractSource: "Declared" | "Human";
  mergePolicy: "Gated" | "Independent";
  status: TaskGroupStatusKey;
  milestoneId: string | null;
  /** Every half is finished and holds a landable PR. */
  ready: boolean;
  /** Why the gate is still holding, in words. Null when it isn't. */
  blockedBy: string | null;
  settledAt: string | null;
  createdAt: string;
  members: TaskGroupMember[];
  /** Agent-raised asks to add another repository. Only "Pending" ones need a decision. */
  repoRequests: RepoRequest[];
  /** "Story": a tracker Story whose members are its sub-tasks, worked in order on one shared branch. */
  kind?: "CrossRepo" | "Story";
  /** A Story's shared branch. */
  branchName?: string | null;
  externalKey?: string | null;
  externalUrl?: string | null;
  /** When the Story's pull requests were marked ready for review. */
  readyAt?: string | null;
  branches?: TaskGroupBranch[] | null;
}

/** An agent's ask to add a repository to a cross-repo task. */
export interface RepoRequest {
  repoName: string;
  reason: string;
  status: "Pending" | "Approved" | "Declined";
  requestedAt: string;
}

// ── Ticket trackers (Jira / Azure DevOps) ─────────────────────────────────────

/** "jira" = Jira Cloud (email + API token); "ado" = Azure DevOps Services (PAT). */
export type TrackerKind = "jira" | "ado";

/**
 * GET /api/trackers/connections — one of the caller's own tracker connections. Metadata only:
 * the token lives in the secret store of the machine that added it and never leaves it.
 * Added on that machine with `./orkka tracker add jira|ado …`.
 */
export interface TrackerConnection {
  id: string;
  /** Stable handle on the machine (e.g. "jira-acme"). */
  key: string;
  kind: TrackerKind | string;
  /** Jira: https://acme.atlassian.net — Azure DevOps: https://dev.azure.com/acme */
  siteUrl: string;
  accountId: string | null;
  displayName: string | null;
  /** Whether the last check with the stored token succeeded. */
  healthy: boolean;
  healthDetail: string | null;
  machineName: string;
  lastSeenAt: string;
  checkedAt: string | null;
}

export type TrackerQueryKind = "test" | "scopes" | "epics" | "statuses" | "preview";

/** POST /api/trackers/connections/{id}/query */
export interface TrackerQueryRequest {
  kind: TrackerQueryKind;
  /** Jira project key / Azure DevOps project name. Required for epics, statuses and preview. */
  scope?: string;
  /** Epics: title text or an exact key/id. */
  search?: string;
  /** Preview: the configured epics. */
  epicKeys?: string[];
  /** Preview: extra JQL (Jira) or WIQL condition (ADO), AND-ed onto the generated query. */
  extraClause?: string;
  /** Preview: grandchildren too, not only direct children. */
  includeDescendants?: boolean;
  /** Preview: the statuses a ticket must be in to be picked up (default "To Do"). */
  intakeStatuses?: string[];
  /** Preview: every ticket assigned to the caller ("All") or only the epics' ("Epics"). */
  scopeMode?: TrackerScopeMode;
}

export interface TrackerScope {
  key: string;
  name: string;
}

export interface TrackerEpic {
  key: string;
  title: string;
  status: string | null;
  url: string | null;
}

export interface TrackerStatus {
  name: string;
  /** "todo" | "inprogress" | "done" */
  category: string | null;
  /** The tracker's own category when it says more (Azure DevOps: Proposed, Resolved…). */
  trackerCategory?: string | null;
}

export interface TrackerTicketPreview {
  key: string;
  title: string;
  type: string | null;
  status: string | null;
  url: string | null;
}

/** Exactly one payload field is set, matching the query's kind. `error` carries the tracker's
 * own explanation (a bad token, a JQL syntax error) when `ok` is false. */
export interface TrackerQueryResult {
  ok: boolean;
  error?: string | null;
  connection?: TrackerConnection | null;
  scopes?: TrackerScope[] | null;
  epics?: TrackerEpic[] | null;
  statuses?: TrackerStatus[] | null;
  matchCount?: number | null;
  sample?: TrackerTicketPreview[] | null;
  /** The JQL / WIQL the preview actually ran. */
  effectiveQuery?: string | null;
}

/** supported=false = the caller's runner could not answer (offline or too old) — not an error. */
export interface TrackerQueryResponse {
  supported: boolean;
  result?: TrackerQueryResult | null;
  /** A successful `statuses` query: what a new ticket source starts from. */
  defaults?: TrackerDefaults | null;
}

/** The status map and intake statuses the host works out from the scope's statuses. */
export interface TrackerDefaults {
  statusMap: TrackerStatusMap;
  intakeStatuses: string[];
}

/**
 * POST /api/trackers/connections — a connection typed into Settings → Trackers. The token is
 * relayed to the caller's own runner, which tests it and keeps it in the OS secret store; it
 * is never stored, logged or echoed by the host.
 */
export interface AddTrackerConnectionRequest {
  kind: TrackerKind;
  /** Jira site (acme.atlassian.net) or Azure DevOps organization. */
  site: string;
  /** Jira only: the Atlassian account email. */
  email?: string;
  token: string;
}

/** supported=false: the runner is offline or too old. ok=false: the tracker refused (error says why). */
export interface AddTrackerConnectionResponse {
  supported: boolean;
  ok: boolean;
  error?: string | null;
  connection?: TrackerConnection | null;
}

/** DELETE /api/trackers/connections/{id} */
export interface RemoveTrackerConnectionResponse {
  supported: boolean;
  ok: boolean;
  error?: string | null;
}

/**
 * Which tracker status a ticket is moved to at each Orkka step; null / unset = don't move it
 * at that step. Moves never go backwards. Every team uses its tracker differently, so each step
 * is any status name of the project's workflow.
 */
export interface TrackerStatusMap {
  /** The task was created from the ticket. */
  pickedUp?: string | null;
  /** The agent started analysing / planning. */
  planning?: string | null;
  /** The plan was approved and development started. */
  developing?: string | null;
  /** Orkka's internal code review. */
  inReview?: string | null;
  /** The pull request is published (for a Story: when the whole story is ready). */
  prOpened?: string | null;
  /** The pull request merged. */
  merged?: string | null;
}

/** The Orkka steps a ticket's status can follow, in pipeline order. */
export type TrackerStatusStep = keyof TrackerStatusMap;

/** "All" = every ticket assigned to the owner in an intake status; "Epics" = only these epics'. */
export type TrackerScopeMode = "All" | "Epics";

/** component = Jira component; label = Jira label / Azure DevOps tag; areaPath = ADO area (and below). */
export type TrackerRoutingRuleKind = "component" | "label" | "areaPath";

/** Tickets matching `value` are worked in `repoId` (optionally a folder inside it). First match wins. */
export interface TrackerRoutingRule {
  kind: TrackerRoutingRuleKind;
  value: string;
  repoId: string;
  subpath?: string | null;
}

/** One epic whose tickets this project picks up, and where their work goes. */
export interface TrackerEpicMapping {
  epicKey: string;
  epicTitle: string | null;
  /** Null = the epic only scopes intake; routing is left to the rules / the AI. */
  repoId: string | null;
  /** A folder inside the repo (e.g. "packages/api"); null = the repo root. */
  subpath: string | null;
}

/** GET /api/projects/{id}/tracker — the project's ticket source (204 → null when none). */
export interface ProjectTrackerConfig {
  id: string;
  projectId: string;
  /** User id of the owner — their runner polls with their connection. */
  ownerUserId: string;
  trackerConnectionId: string;
  connection: TrackerConnection | null;
  scope: string;
  extraClause: string | null;
  includeDescendants: boolean;
  autoSubmit: boolean;
  pollIntervalSeconds: number;
  statusMap: TrackerStatusMap;
  branchTemplate: string;
  enabled: boolean;
  lastPolledAt: string | null;
  lastPollError: string | null;
  epics: TrackerEpicMapping[];
  scopeMode: TrackerScopeMode;
  intakeStatuses: string[];
  routingRules: TrackerRoutingRule[];
  defaultRepoId: string | null;
  /** 1 = work a Story's sub-tasks in order on one branch; 0 = each ticket on its own. */
  storyMode: number;
}

/** PUT /api/projects/{id}/tracker — the caller becomes the owner and must own the connection. */
export interface SaveProjectTrackerRequest {
  trackerConnectionId: string;
  scope: string;
  epics: { epicKey: string; epicTitle?: string | null; repoId?: string | null; subpath?: string | null }[];
  scopeMode?: TrackerScopeMode;
  routingRules?: TrackerRoutingRule[];
  /** Statuses a ticket must be in to be picked up. Empty = "To Do". */
  intakeStatuses?: string[];
  defaultRepoId?: string | null;
  storyMode?: number;
  extraClause?: string | null;
  includeDescendants?: boolean;
  autoSubmit?: boolean;
  /** 60–300. */
  pollIntervalSeconds?: number;
  statusMap?: TrackerStatusMap | null;
  /** Must contain {key}. Null = the default "{type}/{key}-{slug}". */
  branchTemplate?: string | null;
  enabled?: boolean;
}
