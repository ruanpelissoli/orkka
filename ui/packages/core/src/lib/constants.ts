export const LANES = [
  { key: "Drafting", label: "Drafting", color: "var(--color-lane-drafting)" },
  { key: "Ready", label: "Ready", color: "var(--color-lane-ready)" },
  { key: "Planning", label: "Planning", color: "var(--color-lane-planning)" },
  {
    key: "PlanReady",
    label: "Plan Ready",
    color: "var(--color-lane-plan-ready)",
  },
  {
    key: "PendingQuestions",
    label: "Pending Questions",
    color: "var(--color-lane-questions)",
  },
  {
    key: "Developing",
    label: "Developing",
    color: "var(--color-lane-developing)",
  },
  {
    key: "Testing",
    label: "Testing",
    color: "var(--color-lane-testing)",
  },
  {
    key: "CodeReview",
    label: "Code Review",
    color: "var(--color-lane-review)",
  },
  { key: "Done", label: "Done", color: "var(--color-lane-done)" },
] as const;

export type LaneKey = (typeof LANES)[number]["key"];

export const LANE_KEYS: { [K in Uppercase<string> & string]: LaneKey } = {
  DRAFTING: "Drafting",
  READY: "Ready",
  PLANNING: "Planning",
  PLAN_READY: "PlanReady",
  PENDING_QUESTIONS: "PendingQuestions",
  DEVELOPING: "Developing",
  TESTING: "Testing",
  CODE_REVIEW: "CodeReview",
  DONE: "Done",
} as const;

export const LANE_MAP = Object.fromEntries(
  LANES.map((lane) => [lane.key, lane])
) as Record<LaneKey, (typeof LANES)[number]>;

/**
 * The board endpoint keys lanes in snake_case ("code_review") while every other
 * surface — SignalR events, TaskSummary.currentLane, the lane PATCH endpoint —
 * uses the backend enum names ("CodeReview"). Normalize snake keys to enum keys
 * at the API boundary so all client comparisons speak one dialect.
 */
const BOARD_LANE_KEY_TO_LANE: Record<string, LaneKey> = {
  drafting: "Drafting",
  ready: "Ready",
  planning: "Planning",
  plan_ready: "PlanReady",
  pending_questions: "PendingQuestions",
  developing: "Developing",
  testing: "Testing",
  code_review: "CodeReview",
  done: "Done",
};

export function normalizeBoardLaneKey(key: string): string {
  return BOARD_LANE_KEY_TO_LANE[key] ?? key;
}

export const PRIORITIES = [
  { key: "low", label: "Low", color: "#6b7280" },
  { key: "medium", label: "Medium", color: "#3b82f6" },
  { key: "high", label: "High", color: "#f59e0b" },
  { key: "critical", label: "Critical", color: "#ef4444" },
] as const;

export type PriorityKey = (typeof PRIORITIES)[number]["key"];

export const TASK_TYPES = [
  { key: "Feature", label: "Feature", icon: "lightbulb" },
  { key: "BugFix", label: "Bug Fix", icon: "bug" },
  { key: "Refactor", label: "Refactor", icon: "arrows" },
  { key: "Improvement", label: "Improvement", icon: "sparkle" },
] as const;

export type TaskTypeKey = (typeof TASK_TYPES)[number]["key"];

export const TASK_TYPE_MAP = Object.fromEntries(
  TASK_TYPES.map((t) => [t.key, t])
) as Record<TaskTypeKey, (typeof TASK_TYPES)[number]>;

export const TASK_STATUSES = [
  { key: "Draft", label: "Draft", color: "#6b7280" },
  { key: "Active", label: "Active", color: "#10b981" },
  { key: "InQueue", label: "In Queue", color: "#3b82f6" },
  { key: "WaitingPlanApproval", label: "Awaiting Plan Review", color: "#a78bfa" },
  { key: "WaitingCodeReview", label: "Awaiting Code Review", color: "#06b6d4" },
  { key: "WaitingQaReview", label: "Awaiting QA Review", color: "#fbbf24" },
  { key: "WaitingUserVerification", label: "Awaiting Your Verification", color: "#22d3ee" },
  { key: "WaitingAnswer", label: "Awaiting Your Answer", color: "#f59e0b" },
  { key: "ReasoningTimeout", label: "Reasoning Timeout", color: "#f59e0b" },
  { key: "OutOfTokens", label: "Out of Tokens", color: "#f59e0b" },
  { key: "WaitingForRunner", label: "Waiting for Runner", color: "#f59e0b" },
  { key: "Resetting", label: "Resetting", color: "#f59e0b" },
  { key: "Blocked", label: "Blocked", color: "#ef4444" },
  { key: "Completed", label: "Completed", color: "#10b981" },
  { key: "Archived", label: "Archived", color: "#6b7280" },
] as const;

export type TaskStatusKey = (typeof TASK_STATUSES)[number]["key"];

export const TASK_STATUS_MAP = Object.fromEntries(
  TASK_STATUSES.map((s) => [s.key, s])
) as Record<TaskStatusKey, (typeof TASK_STATUSES)[number]>;

export const PLAN_STATUSES = [
  { key: "Draft", label: "Draft" },
  { key: "WaitingPlanApproval", label: "Awaiting Review" },
  { key: "Approved", label: "Approved" },
  { key: "Rejected", label: "Rejected" },
] as const;

export type PlanStatusKey = (typeof PLAN_STATUSES)[number]["key"];

/** Mirrors the backend Permission enum — names must match exactly. */
export const PERMISSION_GROUPS = [
  {
    key: "tasks",
    permissions: ["TasksCreate", "TasksEdit", "TasksDelete", "TasksExecute"],
  },
  { key: "plans", permissions: ["PlansReview"] },
  { key: "codeReview", permissions: ["CodeReviewReview", "CodeReviewMerge"] },
  { key: "documents", permissions: ["DocumentsManage"] },
  { key: "agents", permissions: ["AgentsChat", "AgentsConfigure"] },
  { key: "design", permissions: ["DesignReview"] },
  {
    key: "projects",
    permissions: ["ProjectsCreate", "ProjectsManage", "ProjectsDelete"],
  },
  { key: "repos", permissions: ["ReposManage"] },
  { key: "billing", permissions: ["BillingManage"] },
  { key: "audit", permissions: ["AuditView"] },
] as const;

export type Permission =
  (typeof PERMISSION_GROUPS)[number]["permissions"][number];

export const ALL_PERMISSIONS: Permission[] = PERMISSION_GROUPS.flatMap(
  (g) => [...g.permissions]
);

export const MESSAGE_TYPES = [
  "text",
  "question",
  "progress",
  "error",
  "plan",
  "code_review",
  "qa_report",
  "answer",
  "tool_call",
  "tool_result",
  "system_event",
  "external_comment",
] as const;

export type MessageType = (typeof MESSAGE_TYPES)[number];

// ── Agent Roles ──────────────────────────────────────────────────────

/**
 * The agent identity palette.
 *
 * This is deliberately wider than the UI palette. Fourteen agents have to be
 * told apart at avatar size, so the constraint here is mutual distinguishability
 * first and brand-adjacency second — flattening them onto primary/cyan/violet
 * would make half the roster look like the other half. Every value is a
 * Tailwind ramp stop at the 400/500 level, so they all carry roughly the same
 * weight against the dark background and clear 4.5:1 as text.
 *
 * Related roles are deliberately spaced rather than shaded: PM and Designer sit
 * in different hues, not two stops of violet.
 *
 * This array is the only place agent colours are defined. Surfaces that refer to
 * agents by name go through AGENT_NAME_COLORS / agentColorByName below.
 */
export const AGENT_ROLES = [
  { key: "PM",           label: "PM",             color: "#a78bfa" }, // violet-400
  { key: "Designer",     label: "Designer",       color: "#f472b6" }, // pink-400
  { key: "FrontendDev",  label: "Frontend Dev",   color: "#38bdf8" }, // sky-400
  { key: "BackendDev",   label: "Backend Dev",    color: "#34d399" }, // emerald-400
  { key: "FullStackDev", label: "Full Stack Dev", color: "#60a5fa" }, // blue-400
  { key: "QA",           label: "QA",             color: "#fbbf24" }, // amber-400
  { key: "CodeReviewer", label: "Code Reviewer",  color: "#06b6d4" }, // cyan-500
  { key: "DevOps",       label: "DevOps",         color: "#fb923c" }, // orange-400
  { key: "SolutionArchitect", label: "Solution Architect", color: "#8b5cf6" }, // violet-500
  { key: "GameDev",      label: "Game Dev",      color: "#f43f5e" }, // rose-500
  { key: "GameDesigner", label: "Game Designer", color: "#e879f9" }, // fuchsia-400
  { key: "DBA",          label: "DBA",           color: "#2dd4bf" }, // teal-400
  { key: "Legal",        label: "Legal",         color: "#94a3b8" }, // slate-400
  { key: "MobileDev",    label: "Mobile Dev",    color: "#a3e635" }, // lime-400
] as const;

export type AgentRoleKey = (typeof AGENT_ROLES)[number]["key"];

/**
 * The default name each seeded agent gets, and therefore the colour that name
 * is drawn in everywhere it appears.
 *
 * The marketing pages talk about agents by name ("Luna", "Pixel") rather than
 * by role key, and each used to carry its own hex map — three copies that had
 * already drifted: Archie was teal on the landing page and violet inside the
 * product. Resolving the name through the role means one palette, defined here.
 */
export const AGENT_DEFAULT_NAMES: Record<AgentRoleKey, string> = {
  PM: "Luna",
  Designer: "Muse",
  FrontendDev: "Pixel",
  BackendDev: "Atlas",
  FullStackDev: "Nova",
  QA: "Scout",
  CodeReviewer: "Sentinel",
  DevOps: "Forge",
  SolutionArchitect: "Archie",
  GameDev: "Rune",
  GameDesigner: "Sprite",
  DBA: "Sage",
  Legal: "Lex",
  MobileDev: "Nomad",
};

/** Lowercased agent name -> its role colour. */
export const AGENT_NAME_COLORS: Record<string, string> = Object.fromEntries(
  AGENT_ROLES.map((role) => [
    AGENT_DEFAULT_NAMES[role.key].toLowerCase(),
    role.color,
  ])
);

/** The colour for an agent referred to by name; falls back to the muted grey. */
export function agentColorByName(name: string): string {
  return AGENT_NAME_COLORS[name.toLowerCase()] ?? "#9ca3af";
}

// Roles that write code and can be scoped to a single repository (AgentAssigner
// prefers a repo-scoped dev agent for that repo's tasks).
export const DEV_AGENT_ROLE_KEYS: ReadonlySet<AgentRoleKey> = new Set<AgentRoleKey>([
  "FrontendDev",
  "BackendDev",
  "FullStackDev",
  "GameDev",
  "MobileDev",
]);

export const DIAGRAM_TYPES = [
  { key: "SystemArchitecture", label: "System Architecture" },
  { key: "DataFlow",           label: "Data Flow" },
  { key: "NetworkTopology",    label: "Network Topology" },
  { key: "SequenceDiagram",    label: "Sequence Diagram" },
  { key: "ERDiagram",          label: "ER Diagram" },
  { key: "DeploymentDiagram",  label: "Deployment" },
  { key: "ComponentDiagram",   label: "Component" },
  { key: "Other",              label: "Other" },
] as const;

export type DiagramTypeKey = (typeof DIAGRAM_TYPES)[number]["key"];

export const DIAGRAM_TYPE_MAP = Object.fromEntries(
  DIAGRAM_TYPES.map((t) => [t.key, t])
) as Record<DiagramTypeKey, (typeof DIAGRAM_TYPES)[number]>;

export const AGENT_ROLE_MAP = Object.fromEntries(
  AGENT_ROLES.map((r) => [r.key, r])
) as Record<AgentRoleKey, (typeof AGENT_ROLES)[number]>;

// ── Model catalog ──────────────────────────────────────────────────
// The selectable models come from GET /api/models (`api.models.list`), which reads a
// platform-admin-managed table — so models can be added, retired or repriced without a
// redeploy. There is deliberately NO hardcoded fallback list: a stale one would let a user
// pick a model the platform no longer has, or trust a `supportsMcp` flag that lies. When the
// catalog is unavailable the pickers say so and leave the saved model untouched.

/** AiProvider enum name → the label shown as an <optgroup> heading. */
export const PROVIDER_LABELS: Record<string, string> = {
  Anthropic: "Claude",
  OpenAI: "OpenAI",
  Google: "Google",
  GitHubCopilot: "GitHub Copilot",
  Ollama: "Ollama (local)",
  PixelLab: "PixelLab",
  XAI: "Grok (xAI)",
  // Labelled as a harness, not a vendor: OpenCode ships no models of its own, and every one
  // of its ids names the provider it is really driving ("opencode/anthropic/…").
  OpenCode: "OpenCode (any provider)",
};

export function providerLabel(provider: string): string {
  return PROVIDER_LABELS[provider] ?? provider;
}
