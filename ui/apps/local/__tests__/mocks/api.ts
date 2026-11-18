import { vi } from "vitest";

export const mockApi = {
  projects: {
    list: vi.fn().mockResolvedValue([]),
    get: vi.fn().mockResolvedValue({
      id: "proj-1",
      orgId: "org-1",
      name: "Test Project",
      description: "A test project",
      settings: { maxParallelTasks: 3, sameRepoParallelism: false },
      createdAt: "2025-01-01T00:00:00Z",
      updatedAt: "2025-01-01T00:00:00Z",
    }),
    create: vi.fn(),
    update: vi.fn(),
    delete: vi.fn(),
    createFromFolder: vi.fn(),
    rescan: vi.fn().mockResolvedValue({
      skipped: true,
      offline: true,
      added: [],
      missing: [],
      restored: [],
      originUpdated: [],
      warnings: [],
      repos: [],
    }),
  },
  board: {
    get: vi.fn().mockResolvedValue({ lanes: [] }),
    search: vi.fn().mockResolvedValue([]),
    // Rejects by default so the board page's "metrics are optional context" fallback is what
    // every other test exercises — a strip that fails must never take the board with it.
    metrics: vi.fn().mockRejectedValue(new Error("no metrics in tests")),
  },
  autopilot: {
    get: vi.fn().mockResolvedValue({
      running: false,
      sessionId: null,
      startedAt: null,
      stoppedAt: null,
      stopReason: null,
      capUsd: null,
      spentUsd: 0,
      tasksSubmitted: 0,
      tasksCompleted: 0,
      tasksFailed: 0,
      tasksInFlight: 0,
      tasksWaitingOnYou: 0,
      draftingRemaining: 0,
    }),
    start: vi.fn().mockResolvedValue({}),
    stop: vi.fn().mockResolvedValue({}),
  },
  milestones: {
    list: vi.fn().mockResolvedValue({
      milestones: [],
      backlog: { total: 0, done: 0, inFlight: 0, needsAttention: 0, notStarted: 0 },
    }),
    create: vi.fn().mockResolvedValue({}),
    update: vi.fn().mockResolvedValue({}),
    delete: vi.fn().mockResolvedValue(undefined),
    get: vi.fn().mockResolvedValue({}),
    plan: vi.fn().mockResolvedValue({}),
    accept: vi.fn().mockResolvedValue({}),
    forecast: vi.fn().mockResolvedValue(null),
  },
  tasks: {
    list: vi.fn().mockResolvedValue([]),
    getChat: vi.fn(),
    applyChat: vi.fn(),
    listFiltered: vi.fn().mockResolvedValue([]),
    get: vi.fn().mockResolvedValue({
      id: "task-1",
      orgId: "org-1",
      projectId: "proj-1",
      repoId: "repo-1",
      number: 1,
      code: "ORK-1",
      title: "Test Task",
      description: "Task description",
      priority: "Medium",
      type: "Feature",
      status: "Active",
      currentLane: "Drafting",
      branchName: null,
      prUrl: null,
      mergeFailureReason: null,
      queuePosition: null,
      tags: [],
      contextFiles: [],
      isArchived: false,
      isBlocked: false,
      assigneeId: null,
      createdAt: "2025-01-01T00:00:00Z",
      startedAt: null,
      completedAt: null,
    }),
    getByNumber: vi.fn().mockResolvedValue({
      id: "task-1",
      orgId: "org-1",
      projectId: "proj-1",
      repoId: "repo-1",
      number: 1,
      code: "ORK-1",
      title: "Test Task",
      description: "Task description",
      priority: "Medium",
      type: "Feature",
      status: "Active",
      currentLane: "Drafting",
      branchName: null,
      prUrl: null,
      mergeFailureReason: null,
      queuePosition: null,
      tags: [],
      contextFiles: [],
      isArchived: false,
      isBlocked: false,
      assigneeId: null,
      createdAt: "2025-01-01T00:00:00Z",
      startedAt: null,
      completedAt: null,
    }),
    create: vi.fn(),
    update: vi.fn(),
    delete: vi.fn(),
    submit: vi.fn(),
    moveLane: vi.fn(),
    startInteractiveSession: vi.fn(),
    archive: vi.fn(),
    unarchive: vi.fn(),
    block: vi.fn(),
    unblock: vi.fn(),
    retry: vi.fn(),
    // Thin history by default (204 → null): the create form renders its "not enough history"
    // empty state.
    forecastDraft: vi.fn().mockResolvedValue(null),
    listApprovals: vi.fn().mockResolvedValue([]),
    getMergeConflicts: vi.fn().mockResolvedValue({
      status: "none",
      taskTitle: "Test Task",
      summary: null,
      files: [],
      failureReason: null,
      pendingStrategy: null,
      failureKind: null,
      startedAt: null,
      queuedStrategy: null,
      queuedGuidance: null,
    }),
    resolveMerge: vi.fn().mockResolvedValue(undefined),
    cancelQueuedMerge: vi.fn().mockResolvedValue(undefined),
  },
  messages: {
    list: vi.fn().mockResolvedValue([]),
    create: vi.fn(),
    send: vi.fn(),
  },
  plans: {
    list: vi.fn().mockResolvedValue([]),
    get: vi.fn(),
    latest: vi.fn().mockResolvedValue(null),
    review: vi.fn(),
  },
  attachments: {
    list: vi.fn().mockResolvedValue([]),
    getUploadUrl: vi.fn(),
    confirm: vi.fn(),
    delete: vi.fn(),
  },
  executions: {
    list: vi.fn().mockResolvedValue([]),
    listForProject: vi.fn().mockResolvedValue({
      from: "",
      to: "",
      truncated: false,
      executions: [],
      summary: null,
      tasks: [],
    }),
  },
  repos: {
    list: vi.fn().mockResolvedValue([]),
    add: vi.fn(),
    update: vi.fn(),
    remove: vi.fn(),
    fileTree: vi.fn().mockResolvedValue([]),
    fileContent: vi.fn().mockResolvedValue(""),
    cloneAccess: vi.fn().mockResolvedValue({
      command: "git clone https://github.com/acme/app.git",
      cloneUrl: "https://github.com/acme/app.git",
      defaultBranch: "main",
      tokenized: false,
      expiresAt: null,
    }),
    linkLocal: vi.fn(),
    attachRemote: vi.fn(),
    syncStatus: vi.fn(),
    push: vi.fn(),
    accessCheck: vi.fn().mockResolvedValue({ supported: false, repos: [] }),
  },
  runnerFs: {
    discover: vi.fn(),
    browse: vi.fn().mockResolvedValue({
      supported: true,
      path: null,
      parentPath: null,
      entries: [],
      error: null,
    }),
    initRepo: vi.fn(),
  },
  org: {
    me: vi.fn().mockResolvedValue({
      orgId: "org1",
      orgName: "Acme",
      memberId: "m1",
      role: "Owner" as const,
      permissions: [],
      preferences: { showTechnicalDetails: false },
    }),
    updateMyPreferences: vi
      .fn()
      .mockResolvedValue({ showTechnicalDetails: true, onboardingTourSeen: true }),
    rename: vi.fn().mockResolvedValue({ orgId: "org1", name: "Acme", slug: "acme" }),
    runners: {
      list: vi.fn().mockResolvedValue([]),
    },
  },
  runner: {
    status: vi.fn().mockResolvedValue({
      online: false,
      machineName: null,
      daemonVersion: null,
      connectedAt: null,
      lastSeenAt: null,
      providers: null,
      lastKnownProviders: null,
      lastKnownAt: null,
      lastKnownMachineName: null,
      hasActiveTokens: false,
    }),
    tokens: {
      list: vi.fn().mockResolvedValue([]),
      create: vi.fn(),
      revoke: vi.fn(),
    },
  },
  editors: {
    // Default: nothing installed / no runner, so the button renders nothing. Tests that
    // care opt in by overriding this.
    list: vi.fn().mockResolvedValue({ supported: false, editors: [] }),
    openTask: vi.fn(),
  },
  notifications: {
    list: vi.fn().mockResolvedValue([]),
    markRead: vi.fn(),
    markAllRead: vi.fn(),
  },
  usage: {
    summary: vi.fn().mockResolvedValue({
      totalCostUsd: 0,
      totalInputTokens: 0,
      totalOutputTokens: 0,
      totalCacheReadTokens: 0,
      totalCacheWriteTokens: 0,
      totalTokens: 0,
      taskCount: 0,
    }),
    daily: vi.fn().mockResolvedValue({ items: [] }),
    breakdown: vi.fn().mockResolvedValue({ items: [] }),
  },
  models: {
    list: vi.fn().mockResolvedValue([
      {
        id: "claude-sonnet-4-6",
        name: "Claude Sonnet 4.6",
        provider: "Anthropic",
        kind: "Chat",
        description: "",
        supportsMcp: true,
        isEnabled: true,
        requiresRunner: false,
        sortOrder: 10,
      },
      // Runner-only: the hosted pages must filter this out.
      {
        id: "copilot/auto",
        name: "Copilot Auto",
        provider: "GitHubCopilot",
        kind: "Chat",
        description: "",
        supportsMcp: false,
        isEnabled: true,
        requiresRunner: true,
        sortOrder: 20,
      },
    ]),
  },
  agents: {
    list: vi.fn().mockResolvedValue([]),
    stats: vi.fn().mockResolvedValue({ window: "7d", from: "", to: "", agents: [] }),
    get: vi.fn(),
    create: vi.fn(),
    update: vi.fn(),
    delete: vi.fn(),
    switchProvider: vi.fn().mockResolvedValue({ switchedCount: 0, skippedQaCount: 0 }),
  },
  apiKeys: {
    list: vi.fn().mockResolvedValue([]),
    create: vi.fn(),
    revoke: vi.fn(),
  },
  memories: {
    list: vi.fn().mockResolvedValue({ items: [], nextCursor: null, total: 0 }),
    preview: vi.fn().mockResolvedValue([]),
    create: vi.fn(),
    update: vi.fn(),
    delete: vi.fn(),
  },
  schedules: {
    list: vi.fn().mockResolvedValue([]),
    create: vi.fn(),
    update: vi.fn(),
    setEnabled: vi.fn(),
    delete: vi.fn(),
    runNow: vi.fn(),
    runs: vi.fn().mockResolvedValue([]),
  },
  skills: {
    list: vi.fn().mockResolvedValue([]),
    get: vi.fn(),
    create: vi.fn(),
    update: vi.fn(),
    delete: vi.fn(),
    setForAgent: vi.fn(),
  },
  agentChat: {
    conversations: vi.fn().mockResolvedValue([]),
    messages: vi.fn().mockResolvedValue([]),
    send: vi.fn(),
    createConversation: vi.fn(),
  },
  diagrams: {
    list: vi.fn().mockResolvedValue([]),
    get: vi.fn(),
    update: vi.fn(),
    listVersions: vi.fn().mockResolvedValue([]),
    getVersion: vi.fn(),
    restoreVersion: vi.fn(),
  },
  pullRequests: {
    get: vi.fn(),
    addComment: vi.fn(),
    submitReview: vi.fn(),
  },
  workspaces: {
    mine: vi.fn().mockResolvedValue([]),
    create: vi.fn(),
    setActive: vi.fn().mockResolvedValue(undefined),
  },
  trackers: {
    connections: vi.fn().mockResolvedValue([]),
    addConnection: vi.fn(),
    removeConnection: vi.fn(),
    query: vi.fn().mockResolvedValue({ supported: false }),
    getProjectConfig: vi.fn().mockResolvedValue(null),
    saveProjectConfig: vi.fn(),
    deleteProjectConfig: vi.fn().mockResolvedValue(undefined),
  },
  runConfig: {
    get: vi.fn().mockResolvedValue({
      targets: [],
      notes: null,
      source: "ai",
      detectedAt: null,
      stale: false,
      detected: false,
    }),
    detect: vi.fn(),
    save: vi.fn(),
  },
  appRun: {
    attach: vi.fn().mockResolvedValue({ supported: false, snapshots: [] }),
    start: vi.fn(),
    stop: vi.fn(),
    input: vi.fn(),
    resize: vi.fn(),
  },
};

export const mockInvalidateApiCache = vi.fn();

vi.mock("@/lib/api", () => ({
  api: mockApi,
  invalidateApiCache: mockInvalidateApiCache,
  extractErrorMessage: (_e: unknown, fallback = "Something went wrong. Please try again.") =>
    fallback,
  ApiError: class ApiError extends Error {
    constructor(
      public status: number,
      public body: string
    ) {
      super(`API Error ${status}: ${body}`);
      this.name = "ApiError";
    }
  },
}));

export function resetApiMocks() {
  const resetGroup = (group: Record<string, unknown>) => {
    Object.values(group).forEach((fn) => {
      if (typeof fn === "function" && "mockClear" in fn) {
        (fn as ReturnType<typeof vi.fn>).mockClear();
      } else if (fn && typeof fn === "object") {
        // Nested namespaces like org.members / org.invites
        resetGroup(fn as Record<string, unknown>);
      }
    });
  };
  Object.values(mockApi).forEach((group) => {
    if (typeof group === "object") {
      resetGroup(group as Record<string, unknown>);
    }
  });
}
