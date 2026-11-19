import { OrkkaHttp, type AuthMode } from "./http.js";
import type {
  AddProjectRepoRequest,
  AgentResponse,
  ApiKeyResponse,
  CreateApiKeyResponse,
  CreateProjectRequest,
  CreateTaskRequest,
  CodeChangeSnapshotDiffResponse,
  CodeChangeSnapshotSummary,
  CreateRunnerTokenResponse,
  CreditBalanceResponse,
  MeResponse,
  ModelInfo,
  MyRunnerResponse,
  ProjectRepoResponse,
  ProjectResponse,
  RepoCommandsCheckResponse,
  IntakePreviewResponse,
  ProjectTrackerConfigResponse,
  RunnerTokenResponse,
  SaveProjectTrackerRequest,
  TrackerConnectionResponse,
  TrackerQueryBody,
  TrackerQueryResponse,
  PullRequestResponse,
  TaskMessageResponse,
  TaskPlanResponse,
  TaskResponse,
  UpdateProjectRequest,
  UsageSummaryResponse,
  WorkspaceResponse,
} from "./types.js";

/**
 * Typed façade over the Orkka REST API. Method names mirror the routes. Nothing here is
 * destructive without going through `src/safety` — see `projects.delete`, which requires the
 * caller to pass a project it already fetched so the name guard can run.
 */
export class OrkkaApi {
  constructor(readonly http: OrkkaHttp) {}

  static create(baseUrl: string, auth: AuthMode, rate?: { generalPerMin: number; aiPerMin: number }) {
    return new OrkkaApi(new OrkkaHttp(baseUrl, auth, rate));
  }

  /** The same client, sending `X-Orkka-Workspace: <orgId>` on every request. */
  inWorkspace(orgId: string): OrkkaApi {
    return new OrkkaApi(this.http.withHeaders({ "X-Orkka-Workspace": orgId }));
  }

  readonly org = {
    me: () => this.http.get<MeResponse>("/api/org/me"),
    updateMyPreferences: (prefs: { showTechnicalDetails?: boolean; onboardingTourSeen?: boolean; locale?: string | null }) =>
      this.http.put<unknown>("/api/org/me/preferences", prefs),
    settings: () => this.http.get<Record<string, unknown>>("/api/org/settings"),
    members: () => this.http.get<unknown[]>("/api/org/members"),
    invites: () => this.http.get<unknown[]>("/api/org/invites"),
    runners: () => this.http.get<unknown[]>("/api/org/runners"),
  };

  readonly me = {
    apiKeys: {
      list: () => this.http.get<ApiKeyResponse[]>("/api/me/api-keys"),
      create: (body: { name: string; permissions: string[]; projectId?: string | null }) =>
        this.http.post<CreateApiKeyResponse>("/api/me/api-keys", body, { retry: false }),
      revoke: (keyId: string) => this.http.delete<void>(`/api/me/api-keys/${keyId}`),
    },
    runner: () => this.http.get<MyRunnerResponse>("/api/me/runner"),
    runnerTokens: {
      list: () => this.http.get<RunnerTokenResponse[]>("/api/me/runner-tokens"),
      create: (name: string) => this.http.post<CreateRunnerTokenResponse>("/api/me/runner-tokens", { name }, { retry: false }),
      revoke: (tokenId: string) => this.http.delete<void>(`/api/me/runner-tokens/${tokenId}`),
    },
  };

  readonly projects = {
    list: () => this.http.get<ProjectResponse[]>("/api/projects"),
    get: (idOrSlug: string) => this.http.get<ProjectResponse>(`/api/projects/${idOrSlug}`),
    create: (body: CreateProjectRequest) => this.http.post<ProjectResponse>("/api/projects", body, { retry: false }),
    patch: (id: string, body: UpdateProjectRequest) => this.http.patch<ProjectResponse>(`/api/projects/${id}`, body),
    /** Raw delete. Use `safety.deleteProject` instead — it enforces the e2e- name guard. */
    deleteUnguarded: (id: string) => this.http.delete<void>(`/api/projects/${id}`, { expect: [200, 202, 204] }),
    runConfig: {
      get: (id: string) => this.http.get<Record<string, unknown>>(`/api/projects/${id}/run-config`),
      detect: (id: string) => this.http.post<Record<string, unknown>>(`/api/projects/${id}/run-config/detect`, undefined, { ai: true }),
      put: (id: string, body: unknown) => this.http.put<Record<string, unknown>>(`/api/projects/${id}/run-config`, body),
    },
    board: (id: string) => this.http.get<Record<string, unknown>>(`/api/projects/${id}/board`),
    boardMetrics: (id: string) => this.http.get<Record<string, unknown>>(`/api/projects/${id}/board-metrics`),
    executions: (id: string, query?: Record<string, string | number | undefined>) =>
      this.http.get<Record<string, unknown>>(`/api/projects/${id}/executions`, { query }),
    agentStats: (id: string, window?: string) => this.http.get<unknown>(`/api/projects/${id}/agent-stats`, { query: { window } }),
    members: (id: string) => this.http.get<unknown[]>(`/api/projects/${id}/members`),
    /** The project's ticket source; undefined (204) when it has none. */
    tracker: {
      get: (id: string) => this.http.get<ProjectTrackerConfigResponse | undefined>(`/api/projects/${id}/tracker`, { expect: [200, 204] }),
      put: (id: string, body: SaveProjectTrackerRequest) => this.http.put<ProjectTrackerConfigResponse>(`/api/projects/${id}/tracker`, body),
      delete: (id: string) => this.http.delete<void>(`/api/projects/${id}/tracker`, { expect: [200, 204] }),
      /** The saved source's dry run on the owner's runner (404 without a source). Creates nothing. */
      preview: (id: string) =>
        this.http.post<IntakePreviewResponse>(`/api/projects/${id}/tracker/preview`, undefined, { retry: false, timeoutMs: 4 * 60_000 }),
    },
    /** The guided setup: nothing is pulled from a tracker before Start. */
    setup: {
      step: (id: string, step: string, done = true) =>
        this.http.put<ProjectResponse>(`/api/projects/${id}/setup/steps`, { step, done }),
      start: (id: string, warnings: string[] = []) =>
        this.http.post<ProjectResponse>(`/api/projects/${id}/setup/start`, { warnings }, { retry: false }),
      commands: (id: string) =>
        this.http.post<RepoCommandsCheckResponse>(`/api/projects/${id}/setup/commands`, undefined, { retry: false, timeoutMs: 2 * 60_000 }),
    },
  };

  /** One Orkka org per job. The X-Orkka-Workspace header only applies to Clerk sessions, not API keys. */
  readonly workspaces = {
    list: () => this.http.get<WorkspaceResponse[]>("/api/me/workspaces"),
    create: (name: string) => this.http.post<WorkspaceResponse>("/api/workspaces", { name }, { retry: false }),
    setActive: (orgId: string) => this.http.post<void>("/api/me/active-workspace", { orgId }, { retry: false, expect: [200, 204] }),
  };

  /** The caller's own tracker connections (metadata reported by their runners). */
  readonly trackers = {
    connections: () => this.http.get<TrackerConnectionResponse[]>("/api/trackers/connections"),
    query: (connectionId: string, body: TrackerQueryBody) =>
      this.http.post<TrackerQueryResponse>(`/api/trackers/connections/${connectionId}/query`, body, { retry: false, timeoutMs: 120_000 }),
  };

  readonly repos = {
    list: (projectId: string) => this.http.get<ProjectRepoResponse[]>(`/api/projects/${projectId}/repos`),
    add: (projectId: string, body: AddProjectRepoRequest) =>
      this.http.post<ProjectRepoResponse>(`/api/projects/${projectId}/repos`, body, { retry: false }),
    patch: (projectId: string, repoId: string, body: Record<string, unknown>) =>
      this.http.patch<ProjectRepoResponse>(`/api/projects/${projectId}/repos/${repoId}`, body),
    remove: (repoId: string) => this.http.delete<void>(`/api/repos/${repoId}`, { expect: [200, 204] }),
    addLocal: (projectId: string, path: string) =>
      this.http.post<Record<string, unknown>>(`/api/projects/${projectId}/repos/local`, { path }, { retry: false, timeoutMs: 5 * 60_000 }),
    attachRemote: (projectId: string, repoId: string, body: { remoteUrl: string }) =>
      this.http.post<{ remoteUrl: string }>(`/api/projects/${projectId}/repos/${repoId}/attach-remote`, body, { retry: false, timeoutMs: 5 * 60_000 }),
    syncStatus: (projectId: string, repoId: string) => this.http.get<Record<string, unknown>>(`/api/projects/${projectId}/repos/${repoId}/sync-status`),
    push: (projectId: string, repoId: string, branch?: string) =>
      this.http.post<Record<string, unknown>>(`/api/projects/${projectId}/repos/${repoId}/push`, { branch: branch ?? null }, { retry: false, timeoutMs: 5 * 60_000 }),
    accessCheck: (projectId: string) => this.http.get<Record<string, unknown>>(`/api/projects/${projectId}/repos/access-check`, { timeoutMs: 120_000 }),
    branches: (projectId: string, repoId: string) => this.http.get<unknown>(`/api/projects/${projectId}/repos/${repoId}/branches`),
    tree: (projectId: string, repoId: string, query?: Record<string, string | undefined>) =>
      this.http.get<unknown>(`/api/projects/${projectId}/repos/${repoId}/tree`, { query }),
    files: (projectId: string, repoId: string, query?: Record<string, string | undefined>) =>
      this.http.get<unknown>(`/api/projects/${projectId}/repos/${repoId}/files`, { query }),
    fileContent: (projectId: string, repoId: string, path: string, ref?: string) =>
      this.http.get<Record<string, unknown> | string>(`/api/projects/${projectId}/repos/${repoId}/files/content`, { query: { path, ref, branch: ref } }),
    commits: (projectId: string, repoId: string) => this.http.get<unknown>(`/api/projects/${projectId}/repos/${repoId}/commits`),
    cloneAccess: (projectId: string, repoId: string) => this.http.post<Record<string, unknown>>(`/api/projects/${projectId}/repos/${repoId}/clone-access`),
    fs: {
      browse: (path?: string | null) => this.http.post<Record<string, unknown>>("/api/runners/fs/browse", { path: path ?? null }, { retry: false, timeoutMs: 60_000 }),
      initRepo: (parentPath: string, name: string) =>
        this.http.post<{ path: string; defaultBranch: string }>("/api/runners/fs/init-repo", { parentPath, name }, { retry: false, timeoutMs: 60_000 }),
    },
  };

  readonly tasks = {
    list: (projectId: string, query?: Record<string, string | number | boolean | undefined>) =>
      this.http.get<TaskResponse[] | { items: TaskResponse[] }>(`/api/projects/${projectId}/tasks`, { query }),
    create: (projectId: string, body: CreateTaskRequest) =>
      this.http.post<TaskResponse>(`/api/projects/${projectId}/tasks`, body, { retry: false }),
    get: (taskId: string) => this.http.get<TaskResponse>(`/api/tasks/${taskId}`),
    byNumber: (projectId: string, number: number) => this.http.get<TaskResponse>(`/api/projects/${projectId}/tasks/number/${number}`),
    patch: (taskId: string, body: Record<string, unknown>) => this.http.patch<TaskResponse>(`/api/tasks/${taskId}`, body),
    delete: (taskId: string) => this.http.delete<void>(`/api/tasks/${taskId}`, { expect: [200, 204] }),
    submit: (taskId: string) => this.http.post<unknown>(`/api/tasks/${taskId}/submit`, undefined, { retry: false }),
    retry: (taskId: string) => this.http.post<unknown>(`/api/tasks/${taskId}/retry`, undefined, { retry: false }),
    reset: (taskId: string) => this.http.post<unknown>(`/api/tasks/${taskId}/reset`, undefined, { retry: false }),
    block: (taskId: string) => this.http.post<unknown>(`/api/tasks/${taskId}/block`, undefined, { retry: false }),
    unblock: (taskId: string) => this.http.post<unknown>(`/api/tasks/${taskId}/unblock`, undefined, { retry: false }),
    archive: (taskId: string) => this.http.post<unknown>(`/api/tasks/${taskId}/archive`, undefined, { retry: false }),
    unarchive: (taskId: string) => this.http.post<unknown>(`/api/tasks/${taskId}/unarchive`, undefined, { retry: false }),
    moveLane: (taskId: string, targetLane: string) => this.http.patch<unknown>(`/api/tasks/${taskId}/lane`, { targetLane }),
    messages: {
      list: (taskId: string) => this.http.get<TaskMessageResponse[]>(`/api/tasks/${taskId}/messages`),
      post: (taskId: string, content: string, messageType = "Text") =>
        this.http.post<TaskMessageResponse>(`/api/tasks/${taskId}/messages`, { content, messageType }, { retry: false }),
    },
    executions: (taskId: string) => this.http.get<unknown>(`/api/tasks/${taskId}/executions`),
    evidence: (taskId: string) => this.http.get<unknown>(`/api/tasks/${taskId}/evidence`),
    codeChanges: (taskId: string) => this.http.get<CodeChangeSnapshotSummary[]>(`/api/tasks/${taskId}/code-changes`),
    /** A snapshot's diff, read LIVE from the task owner's runner (`unavailable` says why when it can't be). */
    codeChangeDiff: (taskId: string, snapshotId: string) =>
      this.http.get<CodeChangeSnapshotDiffResponse>(`/api/tasks/${taskId}/code-changes/${snapshotId}`, { timeoutMs: 120_000 }),
    forecast: (taskId: string) => this.http.get<unknown>(`/api/tasks/${taskId}/forecast`, { expect: [200, 204] }),
    editors: (taskId: string) => this.http.get<Record<string, unknown>>(`/api/tasks/${taskId}/editors`),
    verification: (taskId: string, body: Record<string, unknown>) => this.http.post<unknown>(`/api/tasks/${taskId}/verification`, body, { retry: false }),
    spend: {
      continue: (taskId: string) => this.http.post<unknown>(`/api/tasks/${taskId}/spend/continue`, undefined, { retry: false }),
      stop: (taskId: string) => this.http.post<unknown>(`/api/tasks/${taskId}/spend/stop`, undefined, { retry: false }),
    },
    dependencies: {
      add: (taskId: string, dependsOnTaskId: string) =>
        this.http.post<unknown>(`/api/tasks/${taskId}/dependencies`, { dependsOnTaskId }, { retry: false }),
      remove: (taskId: string, dependsOnTaskId: string) => this.http.delete<void>(`/api/tasks/${taskId}/dependencies/${dependsOnTaskId}`),
    },
    inbox: () => this.http.get<unknown>("/api/inbox"),
  };

  readonly plans = {
    list: (taskId: string) => this.http.get<TaskPlanResponse[]>(`/api/tasks/${taskId}/plans`),
    latest: (taskId: string) => this.http.get<TaskPlanResponse | null>(`/api/tasks/${taskId}/plans/latest`, { expect: [200, 204, 404] }),
    review: (taskId: string, planId: string, decision: "approve" | "request_changes" | "reject", feedback?: string) =>
      this.http.post<unknown>(`/api/tasks/${taskId}/plans/${planId}/review`, { decision, feedback: feedback ?? null }, { retry: false }),
  };

  readonly pr = {
    get: (taskId: string) => this.http.get<PullRequestResponse>(`/api/tasks/${taskId}/pull-request`, { expect: [200, 204, 404] }),
    review: (taskId: string, decision: "approve" | "request_changes" | "comment", body?: string, comments?: unknown[]) =>
      this.http.post<unknown>(`/api/tasks/${taskId}/pull-request/reviews`, { decision, body: body ?? null, comments: comments ?? [] }, { retry: false, timeoutMs: 120_000 }),
    comment: (taskId: string, body: Record<string, unknown>) => this.http.post<unknown>(`/api/tasks/${taskId}/pull-request/comments`, body, { retry: false }),
    /** Local folder: squash-merge into it. Remote: PUBLISH a forge PR via the runner's gh/az/glab. */
    merge: (taskId: string) => this.http.post<unknown>(`/api/tasks/${taskId}/pull-request/merge`, undefined, { retry: false, timeoutMs: 5 * 60_000 }),
    sync: (taskId: string) => this.http.post<unknown>(`/api/tasks/${taskId}/pull-request/sync`, undefined, { retry: false, timeoutMs: 5 * 60_000 }),
    mergeConflicts: (taskId: string) => this.http.get<unknown>(`/api/tasks/${taskId}/merge-conflicts`),
    repoPulls: (repoId: string) => this.http.get<unknown[]>(`/api/repos/${repoId}/pulls`),
  };

  readonly agents = {
    list: (projectId: string) => this.http.get<AgentResponse[]>(`/api/projects/${projectId}/agents`),
    get: (projectId: string, agentId: string) => this.http.get<AgentResponse>(`/api/projects/${projectId}/agents/${agentId}`),
    update: (projectId: string, agentId: string, body: Record<string, unknown>) =>
      this.http.put<AgentResponse>(`/api/projects/${projectId}/agents/${agentId}`, body),
    conversations: {
      list: (projectId: string, agentId: string) => this.http.get<unknown[]>(`/api/projects/${projectId}/agents/${agentId}/conversations`),
      create: (projectId: string, agentId: string, body?: Record<string, unknown>) =>
        this.http.post<Record<string, unknown>>(`/api/projects/${projectId}/agents/${agentId}/conversations`, body ?? {}, { retry: false }),
      messages: (projectId: string, agentId: string, conversationId: string) =>
        this.http.get<unknown[]>(`/api/projects/${projectId}/agents/${agentId}/conversations/${conversationId}/messages`),
      sendRaw: (projectId: string, agentId: string, conversationId: string, content: string) =>
        this.http.post<Response>(
          `/api/projects/${projectId}/agents/${agentId}/conversations/${conversationId}/messages`,
          { content },
          { ai: true, raw: true, retry: false, timeoutMs: 15 * 60_000 },
        ),
      cancelRun: (projectId: string, agentId: string, conversationId: string) =>
        this.http.post<unknown>(`/api/projects/${projectId}/agents/${agentId}/conversations/${conversationId}/cancel-run`, undefined, { retry: false }),
      delete: (projectId: string, agentId: string, conversationId: string) =>
        this.http.delete<void>(`/api/projects/${projectId}/agents/${agentId}/conversations/${conversationId}`, { expect: [200, 204] }),
    },
    memories: {
      list: (projectId: string) => this.http.get<unknown>(`/api/projects/${projectId}/memories`),
      create: (projectId: string, body: Record<string, unknown>) => this.http.post<Record<string, unknown>>(`/api/projects/${projectId}/memories`, body, { retry: false }),
      update: (projectId: string, memoryId: string, body: Record<string, unknown>) =>
        this.http.put<Record<string, unknown>>(`/api/projects/${projectId}/memories/${memoryId}`, body),
      delete: (projectId: string, memoryId: string) => this.http.delete<void>(`/api/projects/${projectId}/memories/${memoryId}`, { expect: [200, 204] }),
      retrieve: (projectId: string, query: Record<string, string | undefined>) =>
        this.http.get<unknown>(`/api/projects/${projectId}/memories/retrieve`, { query }),
    },
    skills: {
      list: (projectId: string) => this.http.get<unknown[]>(`/api/projects/${projectId}/skills`),
      create: (projectId: string, body: Record<string, unknown>) => this.http.post<Record<string, unknown>>(`/api/projects/${projectId}/skills`, body, { retry: false }),
      update: (projectId: string, skillId: string, body: Record<string, unknown>) =>
        this.http.put<Record<string, unknown>>(`/api/projects/${projectId}/skills/${skillId}`, body),
      delete: (projectId: string, skillId: string) => this.http.delete<void>(`/api/projects/${projectId}/skills/${skillId}`, { expect: [200, 204] }),
    },
    schedules: {
      list: (projectId: string) => this.http.get<unknown[]>(`/api/projects/${projectId}/schedules`),
      create: (projectId: string, body: Record<string, unknown>) => this.http.post<Record<string, unknown>>(`/api/projects/${projectId}/schedules`, body, { retry: false }),
      delete: (projectId: string, scheduleId: string) => this.http.delete<void>(`/api/projects/${projectId}/schedules/${scheduleId}`, { expect: [200, 204] }),
      setEnabled: (projectId: string, scheduleId: string, enabled: boolean) =>
        this.http.patch<unknown>(`/api/projects/${projectId}/schedules/${scheduleId}/enabled`, { enabled }),
    },
    autopilot: {
      get: (projectId: string) => this.http.get<Record<string, unknown>>(`/api/projects/${projectId}/autopilot`),
      start: (projectId: string, body: Record<string, unknown>) => this.http.post<Record<string, unknown>>(`/api/projects/${projectId}/autopilot`, body, { retry: false }),
      stop: (projectId: string) => this.http.delete<unknown>(`/api/projects/${projectId}/autopilot`, { expect: [200, 204] }),
    },
    designAssets: {
      list: (projectId: string) => this.http.get<unknown>(`/api/projects/${projectId}/design-assets`),
    },
    brand: (projectId: string) => this.http.get<unknown>(`/api/projects/${projectId}/brand`),
    diagrams: (projectId: string) => this.http.get<unknown>(`/api/projects/${projectId}/diagrams`),
  };

  readonly documents = {
    list: (projectId: string) => this.http.get<unknown>(`/api/projects/${projectId}/documents`),
    create: (projectId: string, body: Record<string, unknown>) => this.http.post<Record<string, unknown>>(`/api/projects/${projectId}/documents`, body, { retry: false }),
    delete: (projectId: string, documentId: string) => this.http.delete<void>(`/api/projects/${projectId}/documents/${documentId}`, { expect: [200, 204] }),
  };

  readonly milestones = {
    list: (projectId: string) => this.http.get<unknown[]>(`/api/projects/${projectId}/milestones`),
    create: (projectId: string, body: Record<string, unknown>) => this.http.post<Record<string, unknown>>(`/api/projects/${projectId}/milestones`, body, { retry: false }),
    delete: (projectId: string, milestoneId: string) => this.http.delete<void>(`/api/projects/${projectId}/milestones/${milestoneId}`, { expect: [200, 204] }),
  };

  readonly billing = {
    balance: () => this.http.get<CreditBalanceResponse>("/api/billing/credits/balance"),
    credits: () => this.http.get<Record<string, unknown>>("/api/billing/credits"),
    usageSummary: (query?: Record<string, string | undefined>) => this.http.get<UsageSummaryResponse>("/api/billing/usage/summary", { query }),
    usageDaily: (query?: Record<string, string | undefined>) => this.http.get<unknown>("/api/billing/usage/daily", { query }),
    usageBreakdown: (query?: Record<string, string | undefined>) => this.http.get<unknown>("/api/billing/usage/breakdown", { query }),
    budget: () => this.http.get<Record<string, unknown>>("/api/billing/budget"),
    subscription: () => this.http.get<Record<string, unknown>>("/api/billing/subscription"),
    invoices: () => this.http.get<unknown>("/api/billing/invoices"),
    paymentMethods: () => this.http.get<unknown>("/api/billing/payment-methods"),
    pricing: () => this.http.get<Record<string, unknown>>("/api/pricing"),
  };

  readonly models = {
    list: () => this.http.get<ModelInfo[]>("/api/models"),
  };

  readonly notifications = {
    list: () => this.http.get<unknown>("/api/notifications"),
    readAll: () => this.http.post<unknown>("/api/notifications/read-all", undefined, { retry: false }),
  };

  readonly audit = {
    list: (query?: Record<string, string | number | undefined>) => this.http.get<unknown>("/api/audit", { query }),
    verify: () => this.http.post<Record<string, unknown>>("/api/audit/verify", undefined, { retry: false }),
  };

  readonly admin = {
    me: () => this.http.get<Record<string, unknown>>("/api/admin/me"),
    org: (orgId: string) => this.http.get<Record<string, unknown>>(`/api/admin/orgs/${orgId}`),
    setPlan: (orgId: string, plan: "Free" | "Pro" | "Enterprise", isGifted = true) =>
      this.http.post<unknown>(`/api/admin/orgs/${orgId}/plan`, { plan, isGifted }, { retry: false }),
    grantCredits: (orgId: string, freeCredits: number) => this.http.post<unknown>(`/api/admin/orgs/${orgId}/credits`, { freeCredits }, { retry: false }),
  };

  readonly health = () => this.http.get<unknown>("/health", { headers: { Accept: "*/*" } });
}
