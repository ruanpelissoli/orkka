import type {
  Project,
  MergePrResult,
  SyncPrResult,
  Task,
  TaskSummary,
  TaskBoardResponse,
  BrandKit,
  UpdateBrandKitRequest,
  AutopilotStatus,
  StartAutopilotRequest,
  Milestone,
  MilestoneDetail,
  MilestoneListResponse,
  CreateMilestoneRequest,
  UpdateMilestoneRequest,
  PlanMilestoneRequest,
  MilestonePlanJob,
  AcceptMilestonePlanRequest,
  TaskForecast,
  TaskGroup,
  MilestoneForecast,
  DraftForecastRequest,
  TaskMessage,
  TaskPlan,
  TaskAttachment,
  TaskExecution,
  AgentStatsEnvelope,
  AgentStatsWindow,
  BoardMetricsResponse,
  ProjectExecutionsResponse,
  ProjectRepo,
  ProjectRunConfig,
  AppRunState,
  RunTarget,
  RepoCloneAccess,
  MemberPreferences,
  OrgMe,
  OrgProfile,
  RunnerToken,
  MyRunnerStatus,
  NotificationList,
  AiModel,
  ProjectAgent,
  Skill,
  CreateSkillRequest,
  UpdateSkillRequest,
  Schedule,
  ScheduleRun,
  UpsertScheduleRequest,
  ApiKey,
  MemoryPage,
  MemoryListQuery,
  ProjectMemory,
  UpsertMemoryRequest,
  CreateApiKeyResponse,
  UpdateAgentRequest,
  CreateProject,
  CreateTask,
  CreateMessage,
  UpdateTaskRequest,
  TaskDependenciesResponse,
  ReviewPlanRequest,
  TaskFilterParams,
  UploadUrlResponse,
  ConfirmAttachmentRequest,
  AddProjectRepoRequest,
  RepoForge,
  RepoAccessCheck,
  TrackerConnection,
  AddTrackerConnectionRequest,
  AddTrackerConnectionResponse,
  RemoveTrackerConnectionResponse,
  TrackerQueryRequest,
  TrackerQueryResponse,
  ProjectTrackerConfig,
  SaveProjectTrackerRequest,
  FileTreeNode,
  PullRequestDetail,
  PrComment,
  RepoCommit,
  RepoBranch,
  RepoCommitDiff,
  RepoPrSummary,
  RepoTreeEntry,
  AddLineCommentRequest,
  SubmitCodeReviewRequest,
  UsageSummary,
  DailyUsageItem,
  UsageBreakdownItem,
  UsageGroupBy,
  ArchitectureDiagram,
  UpdateDiagramRequest,
  DiagramVersionMeta,
  DiagramVersionDetail,
  ChatAttachment,
  DesignAsset,
  DesignAssetStatus,
  AgentConversation,
  AgentMessageList,
  TaskChat,
  ApplyTaskChatRequest,
  ApplyTaskChatResult,
  ProjectDocument,
  ProjectDocumentDetail,
  TaskDocument,
  DocumentRevisionMeta,
  DocumentRevisionDetail,
  CreateDocumentRequest,
  UpdateDocumentRequest,
  MergeConflictsInfo,
  InboxResponse,
  InboxFilterParams,
  TaskEvidence,
  CodeChangeSnapshotSummary,
  CodeChangeSnapshotDiff,
  EditorsResponse,
  OpenInEditorResponse,
  ResolveMergeBody,
  StartInteractiveSession,
  FinishInteractiveSessionResult,
  PendingApproval,
  ApprovalDecisionInput,
  DirectoryListing,
  LinkedLocalRepo,
  DiscoverReposResponse,
  CreateProjectFromFolderRequest,
  CreateProjectFromFolderResponse,
  RescanProjectResponse,
  RepoSyncStatus,
  Workspace,
} from "./types";
import { resolveLocale } from "./locale";
import { WORKSPACE_HEADER, getClientWorkspace, isWorkspaceId, workspaceHeaders } from "./workspace";

// The API client runs in the browser only, on the page the Orkka host served: the API is
// always same-origin, so every URL is relative and authenticates with the host's session
// cookie. (`npm run dev` proxies the same paths to the host.)
const BASE_URL = "";

/**
 * Sent on every request. The local host refuses cookie-authenticated requests without it: a
 * cross-site page can make the browser send the session cookie, but not a custom header.
 */
export const CSRF_HEADERS: Readonly<Record<string, string>> = { "X-Orkka-Csrf": "1" };

/**
 * Auth headers for one request. The browser passes an empty token — the host's httpOnly
 * session cookie authenticates it — so no Authorization header is sent. A non-empty token
 * (an API key, the mobile app) goes out as a bearer token.
 */
export function authHeaders(token: string): Record<string, string> {
  return token ? { Authorization: `Bearer ${token}`, ...CSRF_HEADERS } : { ...CSRF_HEADERS };
}

export class ApiError extends Error {
  constructor(
    public status: number,
    public body: string
  ) {
    super(`API Error ${status}: ${body}`);
    this.name = "ApiError";
  }
}

/** Human-readable message from a failed API call: FastEndpoints ProblemDetails
 * (errors.generalErrors / detail / title) when parseable, else a generic fallback. */
export function extractErrorMessage(e: unknown, fallback = "Something went wrong. Please try again."): string {
  if (e instanceof ApiError) {
    try {
      const problem = JSON.parse(e.body);
      return (
        problem?.errors?.generalErrors?.[0] ??
        problem?.detail ??
        problem?.title ??
        fallback
      );
    } catch {
      return fallback;
    }
  }
  return fallback;
}

function toQuery(params: Record<string, string | number | undefined>): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined) search.set(key, String(value));
  }
  const qs = search.toString();
  return qs ? `?${qs}` : "";
}

// ── Client-side GET cache ────────────────────────────────────────────────
// Browser only (outside a browser — tests, prerendering — nothing is cached). Two layers:
//  1. In-flight dedup: identical concurrent GETs share one promise (modal
//     opens, StrictMode double-effects). Always on, zero staleness.
//  2. Opt-in TTL cache (`ttlMs`) for stable metadata (projects, repos,
//     agents, org). Realtime data (board, tasks, messages) must NOT set a
//     TTL — SignalR-triggered refetches need fresh responses.
// Any mutation clears the whole cache: it's one user's view, so a blanket
// invalidation is cheap and never wrong. Entries are keyed by workspace as well as path —
// the same path answers differently in another job's workspace — and switching workspace
// clears everything anyway (see invalidateApiCache's callers).
const isBrowser = typeof window !== "undefined";
const inflightGets = new Map<string, Promise<unknown>>();
const responseCache = new Map<string, { expires: number; value: unknown }>();

export function invalidateApiCache(): void {
  responseCache.clear();
  inflightGets.clear();
}

type FetchOptions = RequestInit & {
  ttlMs?: number;
  /**
   * Ask a specific workspace instead of the current one (e.g. the switcher's per-workspace
   * runner dot). `null` = send no workspace header at all, so the API answers for the
   * caller's active workspace — used by calls that must work even when the current
   * workspace cookie is stale.
   */
  workspace?: string | null;
};

/** The workspace header for one request: an explicit override, else the current one. */
async function requestWorkspaceHeaders(options?: FetchOptions): Promise<Record<string, string>> {
  if (options && "workspace" in options) {
    return isWorkspaceId(options.workspace) ? { [WORKSPACE_HEADER]: options.workspace } : {};
  }
  return workspaceHeaders();
}

async function fetchApi<T>(
  path: string,
  token: string,
  options?: FetchOptions
): Promise<T> {
  const method = options?.method ?? "GET";

  if (!isBrowser || method !== "GET") {
    if (isBrowser) invalidateApiCache();
    return doFetch<T>(path, token, options);
  }

  const workspace =
    options && "workspace" in options ? (options.workspace ?? "") : (getClientWorkspace() ?? "");
  const key = `${workspace}|${path}`;

  const ttl = options?.ttlMs ?? 0;
  if (ttl > 0) {
    const hit = responseCache.get(key);
    if (hit && hit.expires > Date.now()) return hit.value as T;
  }

  const inflight = inflightGets.get(key);
  if (inflight) return inflight as Promise<T>;

  const request = doFetch<T>(path, token, options)
    .then((value) => {
      if (ttl > 0) responseCache.set(key, { expires: Date.now() + ttl, value });
      return value;
    })
    .finally(() => inflightGets.delete(key));

  inflightGets.set(key, request);
  return request;
}

async function doFetch<T>(
  path: string,
  token: string,
  options?: FetchOptions
): Promise<T> {
  const headers: Record<string, string> = {
    ...authHeaders(token),
    "Accept-Language": await resolveLocale(),
    ...(await requestWorkspaceHeaders(options)),
    ...(options?.body ? { "Content-Type": "application/json" } : {}),
    ...(options?.headers as Record<string, string>),
  };
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  const { ttlMs, workspace, ...init } = options ?? {};
  const res = await fetch(`${BASE_URL}${path}`, {
    ...init,
    cache: "no-store",
    credentials: "same-origin",
    headers,
  });
  if (!res.ok) throw new ApiError(res.status, await res.text());
  if (res.status === 204) return undefined as T;
  return res.json();
}

/**
 * What `uploadFileRequest` accepts as the file part. Browsers hand us a `File`/`Blob`;
 * React Native's `FormData` instead takes a `{ uri, name, type }` descriptor pointing at
 * a local file (the runtime streams it). Both are appended verbatim — the shape decides.
 */
export type UploadPart = Blob | { uri: string; name: string; type: string };

/** Multipart upload — must NOT set a JSON Content-Type; the runtime adds the boundary. */
async function uploadFileRequest<T>(
  path: string,
  token: string,
  file: UploadPart,
  fields?: Record<string, string>
): Promise<T> {
  const form = new FormData();
  // RN's FormData typing only knows string | Blob; the descriptor form is a documented
  // runtime feature, so the cast is deliberate.
  form.append("file", file as Blob);
  for (const [key, value] of Object.entries(fields ?? {})) form.append(key, value);
  const res = await fetch(`${BASE_URL}${path}`, {
    method: "POST",
    cache: "no-store",
    credentials: "same-origin",
    headers: {
      ...authHeaders(token),
      "Accept-Language": await resolveLocale(),
      ...(await workspaceHeaders()),
    },
    body: form,
  });
  if (!res.ok) throw new ApiError(res.status, await res.text());
  return res.json();
}

async function fetchText(
  path: string,
  token: string
): Promise<string | null> {
  const res = await fetch(`${BASE_URL}${path}`, {
    cache: "no-store",
    credentials: "same-origin",
    headers: {
      ...authHeaders(token),
      "Accept-Language": await resolveLocale(),
      ...(await workspaceHeaders()),
    },
  });
  if (!res.ok) throw new ApiError(res.status, await res.text());
  if (res.status === 204) return null;
  return res.text();
}

function buildQueryString(params: Record<string, string | number | boolean | undefined | null>): string {
  const entries = Object.entries(params).filter(
    ([, v]) => v !== undefined && v !== null && v !== ""
  );
  if (entries.length === 0) return "";
  return "?" + entries.map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(String(v))}`).join("&");
}

function auditQueryString(query: object): string {
  // General query-string builder (name kept from its first use).
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query) as [string, string | number | undefined | null][]) {
    if (value !== undefined && value !== "" && value !== null) params.set(key, String(value));
  }
  const qs = params.toString();
  return qs ? `?${qs}` : "";
}

export const api = {
  projects: {
    list: (token: string) => fetchApi<Project[]>("/api/projects", token, { ttlMs: 30_000 }),
    get: (id: string, token: string) =>
      fetchApi<Project>(`/api/projects/${id}`, token, { ttlMs: 30_000 }),
    create: (data: CreateProject, token: string) =>
      fetchApi<Project>("/api/projects", token, {
        method: "POST",
        body: JSON.stringify(data),
      }),
    update: (id: string, data: Partial<Project>, token: string) =>
      fetchApi<Project>(`/api/projects/${id}`, token, {
        method: "PATCH",
        body: JSON.stringify(data),
      }),
    delete: (id: string, token: string) =>
      fetchApi<void>(`/api/projects/${id}`, token, { method: "DELETE" }),
    // Creates the project from a folder on the caller's machine: the runner discovers its
    // repositories and each one is registered (remote when it has an origin, else local).
    // 422 when the runner is offline or the folder holds nothing usable.
    createFromFolder: (data: CreateProjectFromFolderRequest, token: string) =>
      fetchApi<CreateProjectFromFolderResponse>("/api/projects/from-folder", token, {
        method: "POST",
        body: JSON.stringify(data),
      }),
    // Re-reads a base-folder project's folder. `auto` is what opening the Repos page sends:
    // throttled server-side and silent (skipped) when the runner is offline.
    rescan: (id: string, token: string, auto = false) =>
      fetchApi<RescanProjectResponse>(
        `/api/projects/${id}/rescan${auto ? "?auto=true" : ""}`,
        token,
        { method: "POST" }
      ),
  },

  board: {
    /** `includeArchived` shows archived cards in their lanes — the board's half of the
     * "archiving hides a task, it does not unmake it" rule. Off by default. */
    get: (projectId: string, token: string, includeArchived = false) =>
      fetchApi<TaskBoardResponse>(
        `/api/projects/${projectId}/board${buildQueryString({
          includeArchived: includeArchived || undefined,
        })}`,
        token
      ),
    /** Strip aggregates + per-lane agent time for the board header. Cached briefly because the
     * board refetches it on hub events, which arrive in bursts as a task moves. */
    metrics: (projectId: string, window: AgentStatsWindow, token: string) =>
      fetchApi<BoardMetricsResponse>(
        `/api/projects/${projectId}/board-metrics${toQuery({ window })}`,
        token,
        { ttlMs: 15_000 }
      ),
    search: (
      projectId: string,
      q: string,
      opts: { includeMessages: boolean; includeArchived: boolean },
      token: string
    ) =>
      fetchApi<TaskSummary[]>(
        `/api/projects/${projectId}/board/search${buildQueryString({
          q,
          includeMessages: opts.includeMessages,
          includeArchived: opts.includeArchived,
        })}`,
        token
      ),
  },

  brand: {
    /** 404 when the project has no brand kit yet. */
    get: async (projectId: string, token: string) => {
      try {
        return await fetchApi<BrandKit>(`/api/projects/${projectId}/brand`, token);
      } catch (e) {
        if (e instanceof ApiError && e.status === 404) return null;
        throw e;
      }
    },
    /** Marks the kit Manual — derivation never runs over it again. */
    update: (projectId: string, data: UpdateBrandKitRequest, token: string) =>
      fetchApi<BrandKit>(`/api/projects/${projectId}/brand`, token, {
        method: "PUT",
        body: JSON.stringify(data),
      }),
  },

  autopilot: {
    /** Current state, or the last session's outcome when none is open. */
    get: (projectId: string, token: string) =>
      fetchApi<AutopilotStatus>(`/api/projects/${projectId}/autopilot`, token),
    /**
     * Opens a session. `uncapped` is required to run with no ceiling and needs BillingManage —
     * omitting `capUsd` alone is "unspecified" and is rejected, never read as unlimited.
     */
    start: (projectId: string, data: StartAutopilotRequest, token: string) =>
      fetchApi<AutopilotStatus>(`/api/projects/${projectId}/autopilot`, token, {
        method: "POST",
        body: JSON.stringify(data),
      }),
    /** Stops submitting. In-flight tasks finish — cancelling them would waste what they cost. */
    stop: (projectId: string, token: string) =>
      fetchApi<AutopilotStatus>(`/api/projects/${projectId}/autopilot`, token, {
        method: "DELETE",
      }),
  },

  milestones: {
    /** Roadmap payload: milestones in position order plus the backlog's counts. */
    list: (projectId: string, token: string) =>
      fetchApi<MilestoneListResponse>(`/api/projects/${projectId}/milestones`, token),
    /** One milestone with its tasks — what the detail page renders without pulling the board. */
    get: (projectId: string, milestoneId: string, token: string) =>
      fetchApi<MilestoneDetail>(
        `/api/projects/${projectId}/milestones/${milestoneId}`,
        token
      ),
    create: (projectId: string, data: CreateMilestoneRequest, token: string) =>
      fetchApi<Milestone>(`/api/projects/${projectId}/milestones`, token, {
        method: "POST",
        body: JSON.stringify(data),
      }),
    update: (
      projectId: string,
      milestoneId: string,
      data: UpdateMilestoneRequest,
      token: string
    ) =>
      fetchApi<Milestone>(`/api/projects/${projectId}/milestones/${milestoneId}`, token, {
        method: "PATCH",
        body: JSON.stringify(data),
      }),
    /** Detaches its tasks back to the Backlog — never deletes the work underneath. */
    delete: (projectId: string, milestoneId: string, token: string) =>
      fetchApi<void>(`/api/projects/${projectId}/milestones/${milestoneId}`, token, {
        method: "DELETE",
      }),
    /**
     * Queues a planning run and returns the job — 202, nothing planned yet.
     *
     * Generating a plan takes minutes, so it does not happen inside this request any more: a
     * user who navigated away used to abort the fetch and destroy the generation with it.
     * Follow the returned job over the board SignalR group, or re-read it with `planJob`.
     * Costs a model call, so it sits in the tighter rate-limit bucket.
     */
    plan: (projectId: string, data: PlanMilestoneRequest, token: string) =>
      fetchApi<MilestonePlanJob>(`/api/projects/${projectId}/milestones/plan`, token, {
        method: "POST",
        body: JSON.stringify(data),
      }),
    /** One planning run, including its finished plan. */
    planJob: (projectId: string, jobId: string, token: string) =>
      fetchApi<MilestonePlanJob>(`/api/projects/${projectId}/milestone-plans/${jobId}`, token),
    /**
     * The caller's newest run for this project (or for one milestone), or null if there is
     * none. This is the reattach path: a reloaded page finds the run it left behind instead of
     * showing a Plan button as though nothing were happening.
     */
    activePlanJob: async (
      projectId: string,
      milestoneId: string | undefined,
      token: string
    ): Promise<MilestonePlanJob | null> => {
      const query = milestoneId ? `?milestoneId=${encodeURIComponent(milestoneId)}` : "";
      try {
        return await fetchApi<MilestonePlanJob>(
          `/api/projects/${projectId}/milestone-plans/active${query}`,
          token
        );
      } catch (e) {
        // 404 is the ordinary "nothing in flight" answer, not a failure worth surfacing.
        if (e instanceof ApiError && e.status === 404) return null;
        throw e;
      }
    },
    /** Remaining workload for a milestone. Fetched after the roadmap renders — it forecasts
     * every open task, unlike the list endpoint which is a single count query. */
    forecast: async (projectId: string, milestoneId: string, token: string) =>
      (await fetchApi<MilestoneForecast | null>(
        `/api/projects/${projectId}/milestones/${milestoneId}/forecast`,
        token
      )) ?? null,
    /** Persists an edited proposal: one milestone plus its tasks, in one transaction. */
    accept: (projectId: string, data: AcceptMilestonePlanRequest, token: string) =>
      fetchApi<Milestone>(`/api/projects/${projectId}/milestones/accept`, token, {
        method: "POST",
        body: JSON.stringify(data),
      }),
  },

  taskGroups: {
    get: (projectId: string, groupId: string, token: string) =>
      fetchApi<TaskGroup>(`/api/projects/${projectId}/task-groups/${groupId}`, token),
    /** Corrects the contract by hand. Marks it Human so a later run cannot overwrite it. */
    updateContract: (projectId: string, groupId: string, contract: string | null, token: string) =>
      fetchApi<void>(`/api/projects/${projectId}/task-groups/${groupId}`, token, {
        method: "PATCH",
        body: JSON.stringify({ contract }),
      }),
    /** Approving creates that repository's half; declining records the decision so a
     *  re-entered phase cannot put the same request to the user twice. */
    resolveRepoRequest: (
      projectId: string,
      groupId: string,
      repoName: string,
      approve: boolean,
      token: string
    ) =>
      fetchApi<void>(`/api/projects/${projectId}/task-groups/${groupId}/repo-requests`, token, {
        method: "POST",
        body: JSON.stringify({ repoName, approve }),
      }),
  },
  tasks: {
    list: (projectId: string, token: string) =>
      fetchApi<Task[]>(`/api/projects/${projectId}/tasks`, token),
    listFiltered: (projectId: string, params: TaskFilterParams, token: string) =>
      fetchApi<Task[]>(
        `/api/projects/${projectId}/tasks${buildQueryString(params as Record<string, string | number | boolean | undefined | null>)}`,
        token
      ),
    get: (taskId: string, token: string) =>
      fetchApi<Task>(`/api/tasks/${taskId}`, token),
    getByNumber: (projectRef: string, number: number, token: string) =>
      fetchApi<Task>(`/api/projects/${projectRef}/tasks/number/${number}`, token),
    /**
     * Estimate for a task that doesn't exist yet. POST because the parameters are a body —
     * nothing is persisted. Re-run it when the model or a skip toggle changes; that the number
     * moves is the point.
     */
    forecastDraft: async (projectId: string, data: DraftForecastRequest, token: string) =>
      (await fetchApi<TaskForecast | null>(
        `/api/projects/${projectId}/tasks/forecast`,
        token,
        { method: "POST", body: JSON.stringify(data) }
      )) ?? null,
    create: (data: CreateTask, token: string) =>
      fetchApi<Task>(`/api/projects/${data.projectId}/tasks`, token, {
        method: "POST",
        body: JSON.stringify(data),
      }),
    update: (taskId: string, data: UpdateTaskRequest, token: string) =>
      fetchApi<Task>(`/api/tasks/${taskId}`, token, {
        method: "PATCH",
        body: JSON.stringify(data),
      }),
    delete: (taskId: string, token: string) =>
      fetchApi<void>(`/api/tasks/${taskId}`, token, { method: "DELETE" }),
    submit: (taskId: string, token: string) =>
      fetchApi<Task>(`/api/tasks/${taskId}/submit`, token, { method: "POST" }),
    /**
     * Opens an interactive development session — a live chat that works on its own branch.
     * The session IS a task from this call onward, so it appears on the board immediately.
     */
    startInteractiveSession: (
      projectId: string,
      data: StartInteractiveSession,
      token: string
    ) =>
      fetchApi<Task>(`/api/projects/${projectId}/interactive-sessions`, token, {
        method: "POST",
        body: JSON.stringify(data),
      }),
    /**
     * Ends a session and ships it. Returns as soon as the request is recorded — the worker
     * driving the session does the committing and pushing, so the task keeps moving after this
     * resolves. Poll or wait for the lane-changed event rather than treating it as done.
     */
    finishInteractiveSession: (taskId: string, token: string) =>
      fetchApi<FinishInteractiveSessionResult>(
        `/api/tasks/${taskId}/interactive-session/finish`,
        token,
        { method: "POST" }
      ),
    /** Commands the session is currently paused on — what a freshly opened tab must render
     *  before any live event arrives. */
    listApprovals: (taskId: string, token: string) =>
      fetchApi<PendingApproval[]>(`/api/tasks/${taskId}/approvals`, token),
    /** Rules on a paused command. 409 means it was already settled — by someone else, or by
     *  expiry — and the caller must not report success. */
    decideApproval: (
      taskId: string,
      approvalId: string,
      decision: ApprovalDecisionInput,
      token: string
    ) =>
      fetchApi<void>(`/api/tasks/${taskId}/approvals/${approvalId}`, token, {
        method: "POST",
        body: JSON.stringify({ decision }),
      }),
    moveLane: (taskId: string, targetLane: string, token: string) =>
      fetchApi<Task>(`/api/tasks/${taskId}/lane`, token, {
        method: "PATCH",
        body: JSON.stringify({ targetLane }),
      }),
    /**
     * Cost + agent-time forecast from measured history. Resolves to null on 204 — the API
     * says "not enough history" that way, and an empty state is the correct render, not an error.
     */
    forecast: async (taskId: string, surface: string, token: string) =>
      // doFetch yields `undefined` on 204; normalize so the declared type is honest.
      (await fetchApi<TaskForecast | null>(
        `/api/tasks/${taskId}/forecast${buildQueryString({ surface })}`,
        token
      )) ?? null,
    /** Proof-of-done bundle: promised vs delivered, QA/review verdicts, run costs. */
    evidence: (taskId: string, token: string) =>
      fetchApi<TaskEvidence>(`/api/tasks/${taskId}/evidence`, token),
    /** Permanently stored per-session diffs — survives worktree GC and branch deletion. */
    codeChanges: (taskId: string, token: string) =>
      fetchApi<CodeChangeSnapshotSummary[]>(`/api/tasks/${taskId}/code-changes`, token),
    codeChangeDiff: (taskId: string, snapshotId: string, token: string) =>
      fetchApi<CodeChangeSnapshotDiff>(
        `/api/tasks/${taskId}/code-changes/${snapshotId}`,
        token
      ),
    getMergeConflicts: (taskId: string, token: string) =>
      fetchApi<MergeConflictsInfo>(`/api/tasks/${taskId}/merge-conflicts`, token),
    resolveMerge: (taskId: string, data: ResolveMergeBody, token: string) =>
      fetchApi<void>(`/api/tasks/${taskId}/resolve-merge`, token, {
        method: "POST",
        body: JSON.stringify(data),
      }),
    cancelQueuedMerge: (taskId: string, token: string) =>
      fetchApi<void>(`/api/tasks/${taskId}/resolve-merge/queued`, token, {
        method: "DELETE",
      }),
    /** Apply or dismiss the PM's advisory cheaper-model suggestion. */
    resolveModelRecommendation: (taskId: string, action: "apply" | "dismiss", token: string) =>
      fetchApi<Task>(`/api/tasks/${taskId}/model-recommendation`, token, {
        method: "POST",
        body: JSON.stringify({ action }),
      }),
    archive: (taskId: string, token: string) =>
      fetchApi<Task>(`/api/tasks/${taskId}/archive`, token, { method: "POST" }),
    unarchive: (taskId: string, token: string) =>
      fetchApi<Task>(`/api/tasks/${taskId}/unarchive`, token, { method: "POST" }),
    block: (taskId: string, token: string) =>
      fetchApi<Task>(`/api/tasks/${taskId}/block`, token, { method: "POST" }),
    unblock: (taskId: string, token: string) =>
      fetchApi<Task>(`/api/tasks/${taskId}/unblock`, token, { method: "POST" }),
    // Re-queue a stuck task in the lane it died in (Blocked / stale Active / parked
    // WaitingForRunner) so the worker re-runs that lane's phase. Unlike `unblock`,
    // which restores the lane's *resting* status — for Code Review that means
    // "waiting for a human", which strands a task whose reviewer agent never ran.
    retry: (taskId: string, token: string) =>
      fetchApi<Task>(`/api/tasks/${taskId}/retry`, token, { method: "POST" }),
    // Answers to a spend checkpoint — a task parked in Pending Questions because it reached
    // the project's task spend limit. Continue grants one more allotment and re-queues the
    // interrupted phase from the agent's hand-off; Stop parks it as Blocked in that lane.
    spendContinue: (taskId: string, token: string) =>
      fetchApi<Task>(`/api/tasks/${taskId}/spend/continue`, token, { method: "POST" }),
    spendStop: (taskId: string, token: string) =>
      fetchApi<Task>(`/api/tasks/${taskId}/spend/stop`, token, { method: "POST" }),
    // Full reset back to Drafting: flags the task; the worker cancels any running
    // agent, wipes messages/plans, deletes the worktree, then finalizes (SignalR
    // LaneChanged announces the landing in Drafting).
    reset: (taskId: string, token: string) =>
      fetchApi<Task>(`/api/tasks/${taskId}/reset`, token, { method: "POST" }),
    assign: (taskId: string, userId: string, token: string) =>
      fetchApi<Task>(`/api/tasks/${taskId}/assign`, token, {
        method: "POST",
        body: JSON.stringify({ userId }),
      }),
    addDependency: (taskId: string, data: { dependsOnTaskId: string; reason?: string }, token: string) =>
      fetchApi<TaskDependenciesResponse>(`/api/tasks/${taskId}/dependencies`, token, {
        method: "POST",
        body: JSON.stringify(data),
      }),
    removeDependency: (taskId: string, dependsOnTaskId: string, token: string) =>
      fetchApi<void>(`/api/tasks/${taskId}/dependencies/${dependsOnTaskId}`, token, {
        method: "DELETE",
      }),
    submitVerification: (
      taskId: string,
      data: { decision: "approve" | "request_changes"; feedback?: string },
      token: string
    ) =>
      fetchApi<void>(`/api/tasks/${taskId}/verification`, token, {
        method: "POST",
        body: JSON.stringify(data),
      }),
    /**
     * The task's "Discuss" conversation with one agent (default: the task's own agent).
     * Created lazily; sending a turn streams through `streamTaskChatMessage` in sse.ts.
     */
    getChat: (taskId: string, agentId: string | null | undefined, token: string) =>
      fetchApi<TaskChat>(`/api/tasks/${taskId}/chat${buildQueryString({ agentId })}`, token),
    /**
     * Turns the discussion into task input: an answer in Pending Questions, a plan-change
     * request in Plan Ready, a plain post otherwise. Returns the lane the task lands in.
     */
    applyChat: (taskId: string, data: ApplyTaskChatRequest, token: string) =>
      fetchApi<ApplyTaskChatResult>(`/api/tasks/${taskId}/chat/apply`, token, {
        method: "POST",
        body: JSON.stringify(data),
      }),
  },

  messages: {
    list: (taskId: string, token: string) =>
      fetchApi<TaskMessage[]>(`/api/tasks/${taskId}/messages`, token),
    send: (taskId: string, data: CreateMessage, token: string) =>
      fetchApi<TaskMessage>(`/api/tasks/${taskId}/messages`, token, {
        method: "POST",
        body: JSON.stringify(data),
      }),
  },

  plans: {
    list: (taskId: string, token: string) =>
      fetchApi<TaskPlan[]>(`/api/tasks/${taskId}/plans`, token),
    latest: (taskId: string, token: string) =>
      fetchApi<TaskPlan>(`/api/tasks/${taskId}/plans/latest`, token),
    review: (taskId: string, planId: string, data: ReviewPlanRequest, token: string) =>
      fetchApi<TaskPlan>(`/api/tasks/${taskId}/plans/${planId}/review`, token, {
        method: "POST",
        body: JSON.stringify(data),
      }),
  },

  attachments: {
    list: (taskId: string, token: string) =>
      fetchApi<TaskAttachment[]>(`/api/tasks/${taskId}/attachments`, token),
    getUploadUrl: (taskId: string, data: { fileName: string; contentType: string }, token: string) =>
      fetchApi<UploadUrlResponse>(`/api/tasks/${taskId}/attachments/upload-url`, token, {
        method: "POST",
        body: JSON.stringify(data),
      }),
    confirm: (taskId: string, data: ConfirmAttachmentRequest, token: string) =>
      fetchApi<TaskAttachment>(`/api/tasks/${taskId}/attachments`, token, {
        method: "POST",
        body: JSON.stringify(data),
      }),
    delete: (id: string, token: string) =>
      fetchApi<void>(`/api/attachments/${id}`, token, { method: "DELETE" }),
  },


  executions: {
    list: (taskId: string, token: string) =>
      fetchApi<TaskExecution[]>(`/api/tasks/${taskId}/executions`, token),
    /** Every execution in a project inside a window — the Activity timeline. Realtime data: no TTL. */
    listForProject: (
      projectId: string,
      params: { from?: string; to?: string; status?: string; agentId?: string; limit?: number },
      token: string
    ) =>
      fetchApi<ProjectExecutionsResponse>(
        `/api/projects/${projectId}/executions${toQuery(params)}`,
        token
      ),
  },

  repos: {
    list: (projectId: string, token: string) =>
      fetchApi<ProjectRepo[]>(`/api/projects/${projectId}/repos`, token, { ttlMs: 30_000 }),
    // Any https / ssh / scp-style remote the developer can already clone with their own git
    // credentials. `provider` null = detected from the URL host; `cliAccount` picks which
    // forge CLI login opens this repo's pull requests.
    add: (projectId: string, data: AddProjectRepoRequest, token: string) =>
      fetchApi<ProjectRepo>(`/api/projects/${projectId}/repos`, token, {
        method: "POST",
        body: JSON.stringify(data),
      }),
    // A command omitted = leave it alone; empty string = clear it.
    update: (
      projectId: string,
      repoId: string,
      data: {
        defaultBranch: string;
        setupCommand?: string;
        qaRunCommand?: string;
        buildCommand?: string;
        checkCommand?: string;
        provider?: RepoForge;
        /** Empty string clears it (back to the CLI's active account). */
        cliAccount?: string;
        /** The routing description agents see. Empty string clears it. */
        description?: string;
      },
      token: string
    ) =>
      fetchApi<ProjectRepo>(`/api/projects/${projectId}/repos/${repoId}`, token, {
        method: "PATCH",
        body: JSON.stringify(data),
      }),
    remove: (repoId: string, token: string) =>
      fetchApi<void>(`/api/repos/${repoId}`, token, { method: "DELETE" }),
    listFiles: (projectId: string, repoId: string, token: string) =>
      fetchApi<FileTreeNode[]>(`/api/projects/${projectId}/repos/${repoId}/files`, token),
    getFileContent: (projectId: string, repoId: string, filePath: string, token: string) =>
      fetchText(`/api/projects/${projectId}/repos/${repoId}/files/content?path=${encodeURIComponent(filePath)}`, token),
    searchFiles: (projectId: string, repoId: string, query: string, token: string) =>
      // The endpoint returns FileTreeNode objects ({ path }); flatten to plain
      // paths so consumers can treat results as strings.
      fetchApi<FileTreeNode[]>(
        `/api/projects/${projectId}/repos/${repoId}/files/search${buildQueryString({ q: query, limit: 20 })}`,
        token
      ).then((nodes) => nodes.map((n) => n.path)),
    // Not cached and POST on purpose — for managed repos the response embeds a
    // short-lived credential, so it must be minted fresh per request.
    cloneAccess: (projectId: string, repoId: string, token: string) =>
      fetchApi<RepoCloneAccess>(
        `/api/projects/${projectId}/repos/${repoId}/clone-access`,
        token,
        { method: "POST" }
      ),

    // Links a folder that exists only on the caller's machine. Never cached: the answer
    // depends on a disk the server cannot see, and it changes whenever the user does.
    linkLocal: (projectId: string, path: string, token: string) =>
      fetchApi<LinkedLocalRepo>(`/api/projects/${projectId}/repos/local`, token, {
        method: "POST",
        body: JSON.stringify({ path }),
      }),

    // Publishes the folder to a repository the user created: the runner sets origin and
    // pushes with the developer's own git credentials. Any https / ssh / scp remote.
    attachRemote: (projectId: string, repoId: string, remoteUrl: string, token: string) =>
      fetchApi<{ remoteUrl: string }>(
        `/api/projects/${projectId}/repos/${repoId}/attach-remote`,
        token,
        { method: "POST", body: JSON.stringify({ remoteUrl }) }
      ),

    syncStatus: (projectId: string, repoId: string, token: string) =>
      fetchApi<RepoSyncStatus>(
        `/api/projects/${projectId}/repos/${repoId}/sync-status`,
        token
      ),

    push: (projectId: string, repoId: string, branch: string | null, token: string) =>
      fetchApi<void>(`/api/projects/${projectId}/repos/${repoId}/push`, token, {
        method: "POST",
        body: JSON.stringify({ branch }),
      }),

    // "Can my machine read these repos?" — asked of the caller's own runner, never cached.
    // supported=false = no runner could answer (offline / too old).
    accessCheck: (projectId: string, token: string) =>
      fetchApi<RepoAccessCheck>(`/api/projects/${projectId}/repos/access-check`, token),
  },

  // The caller's own filesystem, reached through their runner. Only ever the caller's:
  // a folder path means nothing on anyone else's machine.
  runnerFs: {
    // Preview of what a base folder holds, read by the caller's runner. Never creates anything.
    discover: (path: string, token: string) =>
      fetchApi<DiscoverReposResponse>(`/api/runners/fs/discover`, token, {
        method: "POST",
        body: JSON.stringify({ path }),
      }),
    browse: (path: string | null, token: string) =>
      fetchApi<DirectoryListing>(`/api/runners/fs/browse`, token, {
        method: "POST",
        body: JSON.stringify({ path }),
      }),
    initRepo: (parentPath: string, name: string, token: string) =>
      fetchApi<{ path: string; defaultBranch: string }>(
        `/api/runners/fs/init-repo`,
        token,
        { method: "POST", body: JSON.stringify({ parentPath, name }) }
      ),
  },

  // How to run the project on the user's own machine. `get` is free; `detect` is the only
  // call that spends tokens, which is why it is a button and not something the panel does
  // on open.
  runConfig: {
    get: (projectId: string, token: string) =>
      fetchApi<ProjectRunConfig>(`/api/projects/${projectId}/run-config`, token),
    // force re-reads the repos even when nothing changed — for when detection got a command
    // wrong and the user wants another pass rather than to type it themselves.
    detect: (projectId: string, token: string, force = false) =>
      fetchApi<ProjectRunConfig>(
        `/api/projects/${projectId}/run-config/detect${force ? "?force=true" : ""}`,
        token,
        { method: "POST" }
      ),
    save: (
      projectId: string,
      data: { targets: RunTarget[]; notes: string | null },
      token: string
    ) =>
      fetchApi<ProjectRunConfig>(`/api/projects/${projectId}/run-config`, token, {
        method: "PUT",
        body: JSON.stringify(data),
      }),
  },

  // The hosted Run button: commands relayed to the caller's own runner through the worker
  // holding its socket; output comes back over the tasks hub (AppRunOutput/AppRunState),
  // not through these responses.
  appRun: {
    attach: (projectId: string, token: string) =>
      fetchApi<AppRunState>(`/api/projects/${projectId}/app-run`, token),
    start: (
      projectId: string,
      data: { targetKeys: string[] | null; forceFreePorts: boolean },
      token: string
    ) =>
      fetchApi<AppRunState>(`/api/projects/${projectId}/app-run/start`, token, {
        method: "POST",
        body: JSON.stringify(data),
      }),
    stop: (projectId: string, runId: string | null, token: string) =>
      fetchApi<AppRunState>(`/api/projects/${projectId}/app-run/stop`, token, {
        method: "POST",
        body: JSON.stringify({ runId }),
      }),
    input: (projectId: string, runId: string, dataBase64: string, token: string) =>
      fetchApi<void>(`/api/projects/${projectId}/app-run/input`, token, {
        method: "POST",
        body: JSON.stringify({ runId, dataBase64 }),
      }),
    resize: (projectId: string, runId: string, cols: number, rows: number, token: string) =>
      fetchApi<void>(`/api/projects/${projectId}/app-run/resize`, token, {
        method: "POST",
        body: JSON.stringify({ runId, cols, rows }),
      }),
  },


  org: {
    me: (token: string) => fetchApi<OrgMe>("/api/org/me", token, { ttlMs: 60_000 }),
    // org.me is cached (ttlMs above) — call invalidateApiCache() after updating.
    /**
     * Rename the workspace. The slug is not renameable. org.me carries the name, so
     * invalidate the cache after this resolves.
     */
    rename: (name: string, token: string) =>
      fetchApi<OrgProfile>("/api/org", token, {
        method: "PUT",
        body: JSON.stringify({ name }),
      }),
    /** Partial by design: the API merges over the stored blob, so a screen that flips one
     *  preference never has to know (or clobber) the others. */
    updateMyPreferences: (preferences: Partial<MemberPreferences>, token: string) =>
      fetchApi<MemberPreferences>("/api/org/me/preferences", token, {
        method: "PUT",
        body: JSON.stringify(preferences),
      }),
  },

  // Per-user runner daemon (./orkka): personal access tokens + presence.
  runner: {
    /** `workspaceId` asks about the runner in another workspace (runner tokens and
     * presence are per workspace); omitted = the current one. */
    status: (token: string, workspaceId?: string) =>
      fetchApi<MyRunnerStatus>(
        "/api/me/runner",
        token,
        workspaceId ? { workspace: workspaceId } : undefined
      ),
    tokens: {
      list: (token: string) =>
        fetchApi<RunnerToken[]>("/api/me/runner-tokens", token),
      revoke: (id: string, token: string) =>
        fetchApi<void>(`/api/me/runner-tokens/${id}`, token, {
          method: "DELETE",
        }),
    },
  },

  /**
   * The Attention Inbox — everything across every project waiting on a human, already
   * ranked. Deliberately uncached: a stale inbox tells you to do work that is already done.
   */
  inbox: {
    list: (token: string, params: InboxFilterParams = {}) =>
      fetchApi<InboxResponse>(
        `/api/inbox${buildQueryString({
          scope: params.scope,
          projectId: params.projectId,
          kind: params.kind,
          limit: params.limit,
        })}`,
        token
      ),
  },

  /** Hand a task's worktree to a desktop editor on the runner's machine. Task-scoped: the
   * API can only answer per task, because the machine it must reach is the one that ran it. */
  editors: {
    list: (taskId: string, token: string) =>
      fetchApi<EditorsResponse>(`/api/tasks/${taskId}/editors`, token),
    openTask: (taskId: string, token: string, editor?: string) =>
      fetchApi<OpenInEditorResponse>(`/api/tasks/${taskId}/open-in-editor`, token, {
        method: "POST",
        body: JSON.stringify({ editor: editor ?? null }),
      }),
    // The repository itself on its default branch (the runner's persistent main
    // checkout) — not a task's worktree.
    listForRepo: (projectId: string, repoId: string, token: string) =>
      fetchApi<EditorsResponse>(`/api/projects/${projectId}/repos/${repoId}/editors`, token),
    openRepo: (projectId: string, repoId: string, token: string, editor?: string) =>
      fetchApi<OpenInEditorResponse>(
        `/api/projects/${projectId}/repos/${repoId}/open-in-editor`,
        token,
        { method: "POST", body: JSON.stringify({ editor: editor ?? null }) }
      ),
  },

  notifications: {
    list: (token: string, limit?: number) =>
      fetchApi<NotificationList>(
        `/api/notifications${limit ? `?limit=${limit}` : ""}`,
        token
      ),
    markRead: (id: string, token: string) =>
      fetchApi<void>(`/api/notifications/${id}/read`, token, {
        method: "POST",
      }),
    markAllRead: (token: string) =>
      fetchApi<void>("/api/notifications/read-all", token, {
        method: "POST",
      }),
  },

  /** Token usage and its list-price estimate (the developer's own subscription pays). */
  usage: {
    summary: (from: string, to: string, token: string) =>
      fetchApi<UsageSummary>(`/api/usage/summary${buildQueryString({ from, to })}`, token),
    daily: (from: string, to: string, token: string) =>
      fetchApi<{ items: DailyUsageItem[] }>(
        `/api/usage/daily${buildQueryString({ from, to })}`,
        token
      ),
    breakdown: (groupBy: UsageGroupBy, from: string, to: string, token: string) =>
      fetchApi<{ items: UsageBreakdownItem[] }>(
        `/api/usage/breakdown${buildQueryString({ groupBy, from, to })}`,
        token
      ),
  },

  models: {
    /** The admin-managed model catalog. Stable metadata rather than realtime data, so it takes
     * a long TTL — the Team page and the task form both read it on every render. */
    list: (token: string) =>
      fetchApi<AiModel[]>("/api/models", token, { ttlMs: 300_000 }),
  },

  agents: {
    list: (projectId: string, token: string) =>
      fetchApi<ProjectAgent[]>(`/api/projects/${projectId}/agents`, token, { ttlMs: 30_000 }),
    /** Runs / success rate / avg duration / last error per agent over a window. */
    stats: (projectId: string, window: AgentStatsWindow, token: string) =>
      fetchApi<AgentStatsEnvelope>(
        `/api/projects/${projectId}/agent-stats${toQuery({ window })}`,
        token,
        { ttlMs: 15_000 }
      ),
    switchProvider: (projectId: string, provider: string, token: string) =>
      fetchApi<{ switchedCount: number; skippedQaCount: number }>(
        `/api/projects/${projectId}/agents/switch-provider`,
        token,
        { method: "POST", body: JSON.stringify({ provider }) }
      ),
    get: (projectId: string, agentId: string, token: string) =>
      fetchApi<ProjectAgent>(`/api/projects/${projectId}/agents/${agentId}`, token),
    update: (projectId: string, agentId: string, data: UpdateAgentRequest, token: string) =>
      fetchApi<ProjectAgent>(`/api/projects/${projectId}/agents/${agentId}`, token, {
        method: "PUT",
        body: JSON.stringify(data),
      }),
    delete: (projectId: string, agentId: string, token: string) =>
      fetchApi<void>(`/api/projects/${projectId}/agents/${agentId}`, token, {
        method: "DELETE",
      }),
  },

  skills: {
    list: (projectId: string, token: string) =>
      fetchApi<Skill[]>(`/api/projects/${projectId}/skills`, token, { ttlMs: 15_000 }),
    get: (projectId: string, skillId: string, token: string) =>
      fetchApi<Skill>(`/api/projects/${projectId}/skills/${skillId}`, token),
    create: (projectId: string, data: CreateSkillRequest, token: string) =>
      fetchApi<Skill>(`/api/projects/${projectId}/skills`, token, {
        method: "POST",
        body: JSON.stringify(data),
      }),
    update: (projectId: string, skillId: string, data: UpdateSkillRequest, token: string) =>
      fetchApi<Skill>(`/api/projects/${projectId}/skills/${skillId}`, token, {
        method: "PUT",
        body: JSON.stringify(data),
      }),
    delete: (projectId: string, skillId: string, token: string) =>
      fetchApi<void>(`/api/projects/${projectId}/skills/${skillId}`, token, { method: "DELETE" }),
    /** Replace an agent's assigned skill set. */
    setForAgent: (projectId: string, agentId: string, skillIds: string[], token: string) =>
      fetchApi<string[]>(`/api/projects/${projectId}/agents/${agentId}/skills`, token, {
        method: "PUT",
        body: JSON.stringify({ skillIds }),
      }),
  },



  memories: {
    list: (projectId: string, query: MemoryListQuery, token: string) =>
      fetchApi<MemoryPage>(`/api/projects/${projectId}/memories${auditQueryString(query)}`, token),
    preview: (projectId: string, q: string, agentId: string | null, token: string) =>
      fetchApi<ProjectMemory[]>(
        `/api/projects/${projectId}/memories/retrieve${auditQueryString({ q, agentId: agentId ?? undefined })}`,
        token
      ),
    create: (projectId: string, data: UpsertMemoryRequest, token: string) =>
      fetchApi<ProjectMemory>(`/api/projects/${projectId}/memories`, token, {
        method: "POST",
        body: JSON.stringify(data),
      }),
    update: (projectId: string, memoryId: string, data: UpsertMemoryRequest, token: string) =>
      fetchApi<ProjectMemory>(`/api/projects/${projectId}/memories/${memoryId}`, token, {
        method: "PUT",
        body: JSON.stringify(data),
      }),
    delete: (projectId: string, memoryId: string, token: string) =>
      fetchApi<void>(`/api/projects/${projectId}/memories/${memoryId}`, token, { method: "DELETE" }),
  },


  apiKeys: {
    list: (token: string) => fetchApi<ApiKey[]>("/api/me/api-keys", token),
    create: (data: { name: string; permissions: string[]; projectId?: string | null }, token: string) =>
      fetchApi<CreateApiKeyResponse>("/api/me/api-keys", token, {
        method: "POST",
        body: JSON.stringify(data),
      }),
    revoke: (id: string, token: string) =>
      fetchApi<void>(`/api/me/api-keys/${id}`, token, { method: "DELETE" }),
  },

  schedules: {
    list: (projectId: string, token: string) =>
      fetchApi<Schedule[]>(`/api/projects/${projectId}/schedules`, token),
    create: (projectId: string, data: UpsertScheduleRequest, token: string) =>
      fetchApi<Schedule>(`/api/projects/${projectId}/schedules`, token, {
        method: "POST",
        body: JSON.stringify(data),
      }),
    update: (projectId: string, scheduleId: string, data: UpsertScheduleRequest, token: string) =>
      fetchApi<Schedule>(`/api/projects/${projectId}/schedules/${scheduleId}`, token, {
        method: "PUT",
        body: JSON.stringify(data),
      }),
    setEnabled: (projectId: string, scheduleId: string, isEnabled: boolean, token: string) =>
      fetchApi<Schedule>(`/api/projects/${projectId}/schedules/${scheduleId}/enabled`, token, {
        method: "PATCH",
        body: JSON.stringify({ isEnabled }),
      }),
    delete: (projectId: string, scheduleId: string, token: string) =>
      fetchApi<void>(`/api/projects/${projectId}/schedules/${scheduleId}`, token, { method: "DELETE" }),
    /** Fires now and waits for the run to finish (playbook turns can take minutes). */
    runNow: (projectId: string, scheduleId: string, token: string) =>
      fetchApi<ScheduleRun>(`/api/projects/${projectId}/schedules/${scheduleId}/run`, token, { method: "POST" }),
    runs: (projectId: string, scheduleId: string, token: string) =>
      fetchApi<ScheduleRun[]>(`/api/projects/${projectId}/schedules/${scheduleId}/runs`, token),
  },

  agentChat: {
    listConversations: (projectId: string, agentId: string, token: string) =>
      fetchApi<AgentConversation[]>(
        `/api/projects/${projectId}/agents/${agentId}/conversations`,
        token
      ),
    listMessages: (projectId: string, agentId: string, conversationId: string, token: string, opts?: { before?: string; limit?: number }) =>
      fetchApi<AgentMessageList>(
        `/api/projects/${projectId}/agents/${agentId}/conversations/${conversationId}/messages${toQuery({ before: opts?.before, limit: opts?.limit })}`,
        token
      ),
    createConversation: (projectId: string, agentId: string, token: string) =>
      fetchApi<AgentConversation>(
        `/api/projects/${projectId}/agents/${agentId}/conversations`,
        token,
        { method: "POST", body: JSON.stringify({}) }
      ),
    deleteConversation: (projectId: string, agentId: string, conversationId: string, token: string) =>
      fetchApi<void>(
        `/api/projects/${projectId}/agents/${agentId}/conversations/${conversationId}`,
        token,
        { method: "DELETE" }
      ),
    cancelRun: (projectId: string, agentId: string, conversationId: string, token: string) =>
      fetchApi<{ cancelled: boolean }>(
        `/api/projects/${projectId}/agents/${agentId}/conversations/${conversationId}/cancel-run`,
        token,
        { method: "POST" }
      ),
  },

  diagrams: {
    list: (projectId: string, token: string, conversationId?: string) =>
      fetchApi<ArchitectureDiagram[]>(
        `/api/projects/${projectId}/diagrams${conversationId ? `?conversationId=${conversationId}` : ""}`,
        token
      ),
    get: (projectId: string, diagramId: string, token: string) =>
      fetchApi<ArchitectureDiagram>(
        `/api/projects/${projectId}/diagrams/${diagramId}`,
        token
      ),
    update: (projectId: string, diagramId: string, data: UpdateDiagramRequest, token: string) =>
      fetchApi<ArchitectureDiagram>(
        `/api/projects/${projectId}/diagrams/${diagramId}`,
        token,
        { method: "PUT", body: JSON.stringify(data) }
      ),
    delete: (projectId: string, diagramId: string, token: string) =>
      fetchApi<void>(
        `/api/projects/${projectId}/diagrams/${diagramId}`,
        token,
        { method: "DELETE" }
      ),
    listVersions: (projectId: string, diagramId: string, token: string) =>
      fetchApi<DiagramVersionMeta[]>(
        `/api/projects/${projectId}/diagrams/${diagramId}/versions`,
        token
      ),
    getVersion: (projectId: string, diagramId: string, version: number, token: string) =>
      fetchApi<DiagramVersionDetail>(
        `/api/projects/${projectId}/diagrams/${diagramId}/versions/${version}`,
        token
      ),
    restoreVersion: (projectId: string, diagramId: string, version: number, token: string) =>
      fetchApi<ArchitectureDiagram>(
        `/api/projects/${projectId}/diagrams/${diagramId}/versions/${version}/restore`,
        token,
        { method: "POST" }
      ),
    move: (projectId: string, diagramId: string, folderPath: string, token: string) =>
      fetchApi<void>(
        `/api/projects/${projectId}/diagrams/${diagramId}/folder`,
        token,
        { method: "PATCH", body: JSON.stringify({ folderPath }) }
      ),
    renameFolder: (projectId: string, fromPath: string, toPath: string, token: string) =>
      fetchApi<void>(
        `/api/projects/${projectId}/diagrams/folders/rename`,
        token,
        { method: "POST", body: JSON.stringify({ fromPath, toPath }) }
      ),
  },

  chatAttachments: {
    upload: (projectId: string, file: UploadPart, token: string) =>
      uploadFileRequest<ChatAttachment>(
        `/api/projects/${projectId}/chat-attachments`,
        token,
        file
      ),
  },

  designAssets: {
    upload: (projectId: string, file: UploadPart, folderPath: string, token: string) =>
      uploadFileRequest<DesignAsset>(
        `/api/projects/${projectId}/design-assets/upload${folderPath ? `?folderPath=${encodeURIComponent(folderPath)}` : ""}`,
        token,
        file
      ),
    list: (projectId: string, token: string, status?: DesignAssetStatus) =>
      fetchApi<DesignAsset[]>(
        `/api/projects/${projectId}/design-assets${status ? `?status=${status}` : ""}`,
        token
      ),
    get: (projectId: string, assetId: string, token: string) =>
      fetchApi<DesignAsset>(
        `/api/projects/${projectId}/design-assets/${assetId}`,
        token
      ),
    approve: (projectId: string, assetId: string, token: string) =>
      fetchApi<DesignAsset>(
        `/api/projects/${projectId}/design-assets/${assetId}/approve`,
        token,
        { method: "POST" }
      ),
    restore: (projectId: string, assetId: string, token: string) =>
      fetchApi<DesignAsset>(
        `/api/projects/${projectId}/design-assets/${assetId}/restore`,
        token,
        { method: "POST" }
      ),
    reject: (projectId: string, assetId: string, token: string, feedback?: string) =>
      fetchApi<DesignAsset>(
        `/api/projects/${projectId}/design-assets/${assetId}/reject`,
        token,
        { method: "POST", body: JSON.stringify({ feedback: feedback ?? null }) }
      ),
    delete: (projectId: string, assetId: string, token: string) =>
      fetchApi<void>(
        `/api/projects/${projectId}/design-assets/${assetId}`,
        token,
        { method: "DELETE" }
      ),
    /** Deletes a folder. The server refuses a non-empty one unless deleteContents is set. */
    deleteFolder: (
      projectId: string,
      path: string,
      deleteContents: boolean,
      token: string
    ) =>
      fetchApi<void>(
        `/api/projects/${projectId}/design-assets/folders/delete`,
        token,
        { method: "POST", body: JSON.stringify({ path, deleteContents }) }
      ),
    move: (projectId: string, assetId: string, folderPath: string, token: string) =>
      fetchApi<void>(
        `/api/projects/${projectId}/design-assets/${assetId}/folder`,
        token,
        { method: "PATCH", body: JSON.stringify({ folderPath }) }
      ),
    renameFolder: (projectId: string, fromPath: string, toPath: string, token: string) =>
      fetchApi<void>(
        `/api/projects/${projectId}/design-assets/folders/rename`,
        token,
        { method: "POST", body: JSON.stringify({ fromPath, toPath }) }
      ),
  },

  documents: {
    list: (projectId: string, token: string) =>
      fetchApi<ProjectDocument[]>(`/api/projects/${projectId}/documents`, token),
    get: (projectId: string, documentId: string, token: string) =>
      fetchApi<ProjectDocumentDetail>(
        `/api/projects/${projectId}/documents/${documentId}`,
        token
      ),
    /** Documents a finished task's documentation pass created or revised. */
    byTask: (projectId: string, taskId: string, token: string) =>
      fetchApi<TaskDocument[]>(
        `/api/projects/${projectId}/documents/by-task/${taskId}`,
        token
      ),
    create: (projectId: string, data: CreateDocumentRequest, token: string) =>
      fetchApi<ProjectDocumentDetail>(`/api/projects/${projectId}/documents`, token, {
        method: "POST",
        body: JSON.stringify(data),
      }),
    /** Stores a PDF as the document itself — the bytes are kept as uploaded, never converted. */
    uploadFile: (projectId: string, file: UploadPart, path: string, token: string) =>
      uploadFileRequest<ProjectDocumentDetail>(
        `/api/projects/${projectId}/documents/upload`,
        token,
        file,
        { path }
      ),
    /** The stored file's bytes, fetched with the caller's token so the viewer can render them. */
    getFile: async (projectId: string, documentId: string, token: string): Promise<Blob> => {
      const res = await fetch(
        `${BASE_URL}/api/projects/${projectId}/documents/${documentId}/file`,
        {
          cache: "no-store",
          credentials: "same-origin",
          headers: {
            ...authHeaders(token),
            "Accept-Language": await resolveLocale(),
            ...(await workspaceHeaders()),
          },
        }
      );
      if (!res.ok) throw new ApiError(res.status, await res.text());
      return res.blob();
    },
    update: (projectId: string, documentId: string, data: UpdateDocumentRequest, token: string) =>
      fetchApi<ProjectDocumentDetail>(
        `/api/projects/${projectId}/documents/${documentId}`,
        token,
        { method: "PATCH", body: JSON.stringify(data) }
      ),
    delete: (projectId: string, documentId: string, token: string) =>
      fetchApi<void>(
        `/api/projects/${projectId}/documents/${documentId}`,
        token,
        { method: "DELETE" }
      ),
    listRevisions: (projectId: string, documentId: string, token: string) =>
      fetchApi<DocumentRevisionMeta[]>(
        `/api/projects/${projectId}/documents/${documentId}/revisions`,
        token
      ),
    getRevision: (projectId: string, documentId: string, version: number, token: string) =>
      fetchApi<DocumentRevisionDetail>(
        `/api/projects/${projectId}/documents/${documentId}/revisions/${version}`,
        token
      ),
    restoreRevision: (projectId: string, documentId: string, version: number, token: string) =>
      fetchApi<ProjectDocumentDetail>(
        `/api/projects/${projectId}/documents/${documentId}/revisions/${version}/restore`,
        token,
        { method: "POST" }
      ),
    renameFolder: (projectId: string, fromPath: string, toPath: string, token: string) =>
      fetchApi<void>(
        `/api/projects/${projectId}/documents/folders/rename`,
        token,
        { method: "POST", body: JSON.stringify({ fromPath, toPath }) }
      ),
    downloadPdf: async (projectId: string, documentId: string, token: string): Promise<Blob> => {
      const res = await fetch(
        `${BASE_URL}/api/projects/${projectId}/documents/${documentId}/pdf`,
        {
          cache: "no-store",
          credentials: "same-origin",
          headers: {
            ...authHeaders(token),
            "Accept-Language": await resolveLocale(),
            ...(await workspaceHeaders()),
          },
        }
      );
      if (!res.ok) throw new ApiError(res.status, await res.text());
      return res.blob();
    },
    downloadDocx: async (projectId: string, documentId: string, token: string): Promise<Blob> => {
      const res = await fetch(
        `${BASE_URL}/api/projects/${projectId}/documents/${documentId}/docx`,
        {
          cache: "no-store",
          credentials: "same-origin",
          headers: {
            ...authHeaders(token),
            "Accept-Language": await resolveLocale(),
            ...(await workspaceHeaders()),
          },
        }
      );
      if (!res.ok) throw new ApiError(res.status, await res.text());
      return res.blob();
    },
  },

  repoHistory: {
    commits: (
      projectId: string,
      repoId: string,
      params: { branch?: string; skip?: number; take?: number; path?: string },
      token: string
    ) =>
      fetchApi<RepoCommit[]>(
        `/api/projects/${projectId}/repos/${repoId}/commits${buildQueryString(params as Record<string, string | number | undefined>)}`,
        token
      ),
    branches: (projectId: string, repoId: string, token: string) =>
      fetchApi<RepoBranch[]>(
        `/api/projects/${projectId}/repos/${repoId}/branches`,
        token
      ),
    commitDiff: (projectId: string, repoId: string, sha: string, token: string) =>
      fetchApi<RepoCommitDiff>(
        `/api/projects/${projectId}/repos/${repoId}/commits/${sha}/diff`,
        token
      ),
    tree: (
      projectId: string,
      repoId: string,
      params: { path?: string; branch?: string },
      token: string
    ) =>
      fetchApi<RepoTreeEntry[]>(
        `/api/projects/${projectId}/repos/${repoId}/tree${buildQueryString(params as Record<string, string | undefined>)}`,
        token
      ),
  },

  // Workspaces — one per job. The list and the active-workspace switch answer for the caller
  // whatever workspace the request is in, so they are sent without a workspace header: they
  // must work even when the browser's current-workspace cookie is stale.
  workspaces: {
    mine: (token: string) =>
      fetchApi<Workspace[]>("/api/me/workspaces", token, { workspace: null }),
    create: (name: string, token: string) =>
      fetchApi<Workspace>("/api/workspaces", token, {
        method: "POST",
        body: JSON.stringify({ name }),
        workspace: null,
      }),
    /** Where a request with no workspace lands (new tab, mobile, email links). 404 = not a member. */
    setActive: (orgId: string, token: string) =>
      fetchApi<void>("/api/me/active-workspace", token, {
        method: "POST",
        body: JSON.stringify({ orgId }),
        workspace: null,
      }),
  },

  // Ticket trackers. Connections live on the caller's own machines (the token never leaves
  // them); queries are answered by the caller's runner, so none of this is cached.
  trackers: {
    connections: (token: string) =>
      fetchApi<TrackerConnection[]>("/api/trackers/connections", token),
    /** The token travels in the body only (never a URL) to the caller's own runner. */
    addConnection: (request: AddTrackerConnectionRequest, token: string) =>
      fetchApi<AddTrackerConnectionResponse>("/api/trackers/connections", token, {
        method: "POST",
        body: JSON.stringify(request),
      }),
    /** Removes the token from the runner's secret store; 404 = not the caller's connection. */
    removeConnection: (connectionId: string, token: string) =>
      fetchApi<RemoveTrackerConnectionResponse>(`/api/trackers/connections/${connectionId}`, token, {
        method: "DELETE",
      }),
    /** supported=false = the runner is offline or too old; 404 = not the caller's connection. */
    query: (connectionId: string, request: TrackerQueryRequest, token: string) =>
      fetchApi<TrackerQueryResponse>(`/api/trackers/connections/${connectionId}/query`, token, {
        method: "POST",
        body: JSON.stringify(request),
      }),
    /** The project's ticket source, or null when it has none (204). */
    getProjectConfig: async (projectId: string, token: string) =>
      (await fetchApi<ProjectTrackerConfig | undefined>(`/api/projects/${projectId}/tracker`, token)) ??
      null,
    saveProjectConfig: (projectId: string, request: SaveProjectTrackerRequest, token: string) =>
      fetchApi<ProjectTrackerConfig>(`/api/projects/${projectId}/tracker`, token, {
        method: "PUT",
        body: JSON.stringify(request),
      }),
    deleteProjectConfig: (projectId: string, token: string) =>
      fetchApi<void>(`/api/projects/${projectId}/tracker`, token, { method: "DELETE" }),
  },

  repoPulls: {
    list: (repoId: string, state: "all" | "open" | "closed", token: string) =>
      fetchApi<RepoPrSummary[]>(
        `/api/repos/${repoId}/pulls${buildQueryString({ state })}`,
        token
      ),
    get: (repoId: string, number: number, token: string) =>
      fetchApi<PullRequestDetail>(`/api/repos/${repoId}/pulls/${number}`, token),
    merge: (repoId: string, number: number, token: string) =>
      fetchApi<void>(`/api/repos/${repoId}/pulls/${number}/merge`, token, {
        method: "POST",
      }),
    /** Closes without merging. The branch survives; a task behind the PR is recorded as
     * closed-not-shipped rather than merged. */
    close: (repoId: string, number: number, token: string) =>
      fetchApi<void>(`/api/repos/${repoId}/pulls/${number}/close`, token, {
        method: "POST",
      }),
    submitReview: (
      repoId: string,
      number: number,
      data: SubmitCodeReviewRequest,
      token: string
    ) =>
      fetchApi<void>(`/api/repos/${repoId}/pulls/${number}/reviews`, token, {
        method: "POST",
        body: JSON.stringify(data),
      }),
    addComment: (
      repoId: string,
      number: number,
      data: AddLineCommentRequest,
      token: string
    ) =>
      fetchApi<PrComment>(`/api/repos/${repoId}/pulls/${number}/comments`, token, {
        method: "POST",
        body: JSON.stringify(data),
      }),
  },

  pullRequests: {
    get: (taskId: string, token: string) =>
      fetchApi<PullRequestDetail>(`/api/tasks/${taskId}/pull-request`, token),
    addComment: (taskId: string, data: AddLineCommentRequest, token: string) =>
      fetchApi<PrComment>(`/api/tasks/${taskId}/pull-request/comments`, token, {
        method: "POST",
        body: JSON.stringify(data),
      }),
    submitReview: (taskId: string, data: SubmitCodeReviewRequest, token: string) =>
      fetchApi<void>(`/api/tasks/${taskId}/pull-request/reviews`, token, {
        method: "POST",
        body: JSON.stringify(data),
      }),
    merge: (taskId: string, token: string) =>
      // 204 = merged now (undefined); 202 = conflicts handed to the auto-resolver (body).
      fetchApi<MergePrResult | undefined>(`/api/tasks/${taskId}/pull-request/merge`, token, {
        method: "POST",
      }),
    /** Reconciles the task with its pull request record. */
    sync: (taskId: string, token: string) =>
      fetchApi<SyncPrResult>(`/api/tasks/${taskId}/pull-request/sync`, token, {
        method: "POST",
      }),
    /** Manually opens the PR for a task whose automatic creation failed. */
    create: (taskId: string, token: string) =>
      fetchApi<Task>(`/api/tasks/${taskId}/create-pr`, token, {
        method: "POST",
      }),
  },
};
