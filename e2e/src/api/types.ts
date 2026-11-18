/** DTOs mirrored (camelCase) from the .NET records. Only fields the harness reads are typed; the rest is `unknown`. */

export interface MeResponse {
  orgId: string;
  orgName: string;
  memberId: string;
  role: "Owner" | "Member";
  isOriginalRoot: boolean;
  isPlatformAdmin: boolean;
  permissions: string[];
  preferences: { showTechnicalDetails: boolean; onboardingTourSeen: boolean; locale: string | null };
  teamMembersEnabled: boolean;
}

export interface ApiKeyResponse {
  id: string;
  name: string;
  prefix: string;
  permissions: string[];
  projectId: string | null;
  createdAt: string;
  lastUsedAt: string | null;
  revokedAt: string | null;
}
export interface CreateApiKeyResponse {
  key: ApiKeyResponse;
  rawKey: string;
}

export interface ProjectSettings {
  maxParallelTasks: number;
  sameRepoParallelism: boolean;
  defaultTokenLimit?: number;
  defaultModel?: string | null;
  autoApproveCodeReview?: boolean;
  autoApprovePlan?: boolean;
  autoPushOnMerge?: boolean;
  qaEnabled?: boolean;
  qaRunCommand?: string | null;
  manualVerificationEnabled?: boolean;
  showAgentToolCalls?: boolean;
  autopilotQueueDepthFactor?: number;
  documentationSyncEnabled?: boolean;
  automationEnabled?: boolean;
  autoRunOnOpen?: boolean | null;
  autoArchiveDoneTasks?: boolean;
  taskRuntimeLimitEnabled?: boolean;
  taskRuntimeLimitMinutes?: number;
  language?: string | null;
  planAheadDepth?: number | null;
  taskSpendLimitEnabled?: boolean;
  taskSpendLimitCredits?: number;
}

export interface ProjectResponse {
  id: string;
  orgId: string;
  name: string;
  slug: string;
  key: string;
  description: string | null;
  settings: ProjectSettings;
  markupRate: number;
  createdAt: string;
  updatedAt: string;
  taskCount: number | null;
  memberCount: number | null;
}

export interface CreateProjectRequest {
  name: string;
  description?: string | null;
  slug?: string | null;
  key?: string | null;
}
export interface UpdateProjectRequest {
  name?: string | null;
  description?: string | null;
  slug?: string | null;
  settings?: ProjectSettings | null;
}

export interface ProjectRepoResponse {
  id: string;
  projectId: string;
  name: string;
  gitUrl: string;
  defaultBranch: string;
  provider: string;
  workspaceStatus: string;
  cardColor: string;
  setupCommand: string | null;
  qaRunCommand: string | null;
  buildCommand: string | null;
  checkCommand: string | null;
  createdAt: string;
  cliAccount?: string | null;
}
export interface AddProjectRepoRequest {
  name: string;
  gitUrl: string;
  defaultBranch: string;
  /** Forge override: github, azuredevops, gitlab, bitbucket, other. Null detects it from the host. */
  provider?: string | null;
  /** Forge CLI account that publishes this repo's PRs. */
  cliAccount?: string | null;
}

export type Lane =
  | "Drafting"
  | "Ready"
  | "Planning"
  | "PlanReady"
  | "PendingQuestions"
  | "Developing"
  | "Testing"
  | "CodeReview"
  | "Done";
export const LANES: Lane[] = ["Drafting", "Ready", "Planning", "PlanReady", "PendingQuestions", "Developing", "Testing", "CodeReview", "Done"];

export type TaskStatus =
  | "Draft"
  | "Active"
  | "InQueue"
  | "WaitingPlanApproval"
  | "WaitingCodeReview"
  | "ReasoningTimeout"
  | "OutOfTokens"
  | "Blocked"
  | "Completed"
  | "Archived"
  | "WaitingAnswer"
  | "WaitingQaReview"
  | "WaitingUserVerification"
  | "WaitingForRunner"
  | "Resetting";

export interface CreateTaskRequest {
  repoId: string;
  title: string;
  description?: string | null;
  priority?: "Low" | "Medium" | "High" | "Critical";
  type?: "Feature" | "BugFix" | "Refactor" | "Improvement";
  tags?: string[];
  contextFiles?: string[];
  contextDocumentIds?: string[];
  skipPlanning?: boolean;
  skipTesting?: boolean;
  skipCodeReview?: boolean;
  assignedAgentId?: string | null;
  model?: string | null;
  milestoneId?: string | null;
  spansRepoIds?: string[];
}

export interface TaskResponse {
  id: string;
  projectId: string;
  number: number;
  code: string;
  repoId: string | null;
  repoName: string | null;
  groupId: string | null;
  title: string;
  description: string | null;
  priority: string;
  type: string;
  currentLane: Lane | string;
  status: TaskStatus | string;
  isArchived: boolean;
  createdByUserId: string | null;
  branchName: string | null;
  prUrl: string | null;
  isMerged: boolean;
  mergeFailureReason: string | null;
  queuePosition: number | null;
  tags: string[] | null;
  totalTokensUsed: number;
  hasUnmetDependencies: boolean;
  skipPlanning: boolean;
  skipTesting: boolean;
  skipCodeReview: boolean;
  startedAt: string | null;
  completedAt: string | null;
  createdAt: string;
  updatedAt: string;
  /** Whose runner executes the task (every task runs on its owner's runner). */
  runnerUserId: string | null;
  assignedAgentId: string | null;
  model: string | null;
  setupFailure: string | null;
  milestoneId: string | null;
  isInteractive: boolean;
  recommendedModel: string | null;
  /** The forge PR the runner published for this task (gh/az/glab). */
  publishedPrUrl?: string | null;
  /** Tracker ticket key (SHOP-12 / 4711), its URL and epic, for tracker-fed tasks. */
  externalKey?: string | null;
  externalUrl?: string | null;
  externalEpic?: string | null;
  /** Set after five failed status write-backs. */
  externalSyncError?: string | null;
}

export interface TaskPlanResponse {
  id: string;
  taskId: string;
  version?: number;
  status?: string;
  content?: string;
  [k: string]: unknown;
}

export interface TaskMessageResponse {
  id: string;
  taskId?: string;
  role?: string;
  content: string;
  createdAt: string;
  [k: string]: unknown;
}

export interface RunnerProvider {
  provider: string;
  cliFound: boolean;
  authenticated: boolean;
  models: string[] | null;
  detail: string | null;
}
export interface RunnerForgeCli {
  cli: "gh" | "az" | "glab" | string;
  found: boolean;
  authenticated: boolean;
  accounts: string[] | null;
  detail: string | null;
}
export interface MyRunnerResponse {
  online: boolean;
  machineName: string | null;
  daemonVersion: string | null;
  connectedAt: string | null;
  lastSeenAt: string | null;
  providers: RunnerProvider[] | null;
  hasActiveTokens: boolean;
  updateAvailable: boolean;
  gitIdentity: { defaultAccount: string | null; scopedPerRepoPath: boolean; detail: string | null } | null;
  capabilities: string[] | null;
  lastKnownProviders: RunnerProvider[] | null;
  lastKnownAt: string | null;
  lastKnownMachineName: string | null;
  forgeClis?: RunnerForgeCli[] | null;
}

export interface CreditBalanceResponse {
  balance: number;
  mode: string;
  recentTopUps: unknown[];
  includedCredits: number;
  includedResetsAt: string | null;
}
export interface UsageSummaryResponse {
  totalCredits: number;
  totalInputTokens: number;
  totalOutputTokens: number;
  totalCacheReadTokens: number;
  totalCacheWriteTokens: number;
  taskCount: number;
  avgCreditsPerTask: number;
  freeCreditsRemaining: number;
}
export interface FreeCreditsResponse {
  [k: string]: unknown;
}

export interface AgentResponse {
  id: string;
  projectId: string;
  name: string;
  role: string;
  model: string;
  repoId?: string | null;
  isActive?: boolean;
  [k: string]: unknown;
}

export interface ModelInfo {
  id: string;
  provider?: string;
  displayName?: string;
  enabled?: boolean;
  [k: string]: unknown;
}

export interface PullRequestResponse {
  number?: number;
  state?: string;
  url?: string;
  title?: string;
  files?: unknown[];
  [k: string]: unknown;
}

// ── Runner tokens (expire after 90 days; a connected runner renews its own) ──

export interface RunnerTokenResponse {
  id: string;
  name: string;
  createdAt: string;
  lastUsedAt: string | null;
  lastMachineName: string | null;
  revoked: boolean;
  /** When the token stops working; renewed (RotateToken) while the runner is connected. */
  expiresAt: string;
  [k: string]: unknown;
}
export interface CreateRunnerTokenResponse {
  id: string;
  name: string;
  /** The raw `orkr_…` value — shown once. */
  token: string;
  expiresAt: string;
}

// ── Code changes (Orkka stores paths and counts; the diff is read live from the runner) ──

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
  isLive: boolean;
  revision: number;
}
export interface CodeChangeSnapshotDiffResponse {
  id: string;
  capturedAt: string;
  branchName: string;
  baseBranch: string;
  isTruncated: boolean;
  /** Empty when `unavailable` is set. */
  diff: string;
  isLive: boolean;
  revision: number;
  /** A finished snapshot shows its branch as it is NOW (no stored copy exists). */
  currentBranch: boolean;
  /** Why the diff could not be read (runner offline, branch gone); null on success. */
  unavailable: string | null;
}

// ── Workspaces (one Orkka org per job) ───────────────────────────────────

export interface WorkspaceResponse {
  orgId: string;
  name: string;
  slug: string;
  role: "Owner" | "Member" | string;
  isActive: boolean;
  suspended: boolean;
}

// ── Trackers ─────────────────────────────────────────────────────────────

export interface TrackerConnectionResponse {
  id: string;
  /** The connection id on the runner (trackers.json), e.g. jira-acme. */
  key: string;
  kind: "jira" | "ado" | string;
  siteUrl: string;
  accountId: string | null;
  displayName: string | null;
  healthy: boolean;
  healthDetail: string | null;
  machineName: string;
  lastSeenAt: string;
  checkedAt: string | null;
}

/** Six optional steps, each a tracker status or null ("don't change"). */
export interface TrackerStatusMap {
  pickedUp: string | null;
  planning: string | null;
  developing: string | null;
  inReview: string | null;
  prOpened: string | null;
  merged: string | null;
}

export interface TrackerEpicMapping {
  epicKey: string;
  epicTitle?: string | null;
  repoId: string;
  subpath?: string | null;
}

export interface SaveProjectTrackerRequest {
  trackerConnectionId: string;
  scope: string;
  epics?: TrackerEpicMapping[];
  extraClause?: string | null;
  includeDescendants?: boolean;
  autoSubmit?: boolean;
  pollIntervalSeconds?: number;
  statusMap?: Partial<TrackerStatusMap> | null;
  branchTemplate?: string | null;
  enabled?: boolean;
  /** "All" (every ticket assigned to me in an intake status) or "Epics". */
  scopeMode?: "All" | "Epics";
  /** Where a ticket must be to become a task (default ["To Do"]). */
  intakeStatuses?: string[];
  routingRules?: { kind: "component" | "label" | "areaPath"; value: string; repoId?: string | null; subpath?: string | null }[];
  defaultRepoId?: string | null;
  /** 1 = Stories on (default), 0 = off. */
  storyMode?: number;
}

export interface ProjectTrackerConfigResponse {
  id: string;
  projectId: string;
  ownerUserId: string;
  trackerConnectionId: string;
  connection: TrackerConnectionResponse | null;
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
}

export type TrackerQueryKind = "test" | "scopes" | "epics" | "statuses" | "preview";

export interface TrackerQueryBody {
  kind: TrackerQueryKind;
  scope?: string | null;
  search?: string | null;
  epicKeys?: string[] | null;
  extraClause?: string | null;
  includeDescendants?: boolean;
}

/** TrackerQueryResult from orkka-api/src/Orkka.LocalRunner.Contracts/TrackerContracts.cs. */
export interface TrackerQueryResult {
  ok: boolean;
  error?: string | null;
  connection?: unknown;
  scopes?: { key: string; name: string }[] | null;
  epics?: { key: string; title: string; status: string | null; url: string | null }[] | null;
  statuses?: { name: string; category: string | null }[] | null;
  matchCount?: number | null;
  sample?: { key: string; title: string; type: string | null; status: string | null; url: string | null }[] | null;
  effectiveQuery?: string | null;
}

/** supported=false: the owner's runner is offline or too old — never an error. */
export interface TrackerQueryResponse {
  supported: boolean;
  result?: TrackerQueryResult | null;
}
