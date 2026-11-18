import type { ActivitySummary, ActivityTaskRow, ProjectExecutionRow } from "@/lib/types";
import type { LaneKey } from "@/lib/constants";
import { hasCostEstimate } from "@/lib/utils";

/**
 * Everything the Activity page derives from a window of runs, as plain functions over plain data.
 *
 * Kept free of React on purpose: these are the rules that decide what the page *says* — which runs
 * deserve someone's attention, what each task amounted to, where the money went — and they are far
 * easier to argue with, and to test, as functions than as JSX. The components below them only lay
 * the results out.
 */

/** A run still going after this long is worth a second look rather than a spinner. */
const LONG_RUN_MS = 45 * 60 * 1000;

/** Quiet for this long, with nothing running, reads as stuck rather than in-flight. */
const STALL_MS = 2 * 60 * 60 * 1000;

/** Runs of the same task in the same lane before it stops looking like progress. */
const RETRY_LOOP_THRESHOLD = 3;

/** Lanes where the agents have done their part and a person is the thing being waited on. */
const HUMAN_BLOCKED_LANES: readonly LaneKey[] = ["PendingQuestions", "PlanReady"];

/** Task statuses that say the same thing the lanes above do, from the other side. */
const HUMAN_BLOCKED_STATUSES: readonly string[] = [
  "WaitingPlanApproval",
  "WaitingAnswer",
  "WaitingUserVerification",
];

type AttentionKind =
  | "failed"
  | "mergeFailure"
  | "setupFailure"
  | "retryLoop"
  | "longRunning"
  | "awaitingHuman"
  | "stalled";

/**
 * Ranked so the list reads worst-first without the caller having to know the rules. A broken run
 * outranks a slow one; anything a person could unblock right now outranks anything that has merely
 * gone quiet.
 */
const SEVERITY: Record<AttentionKind, number> = {
  failed: 90,
  mergeFailure: 80,
  setupFailure: 80,
  retryLoop: 70,
  longRunning: 60,
  awaitingHuman: 50,
  stalled: 40,
};

interface AttentionItem {
  /** Stable across refetches — the list re-renders every poll. */
  id: string;
  kind: AttentionKind;
  taskId: string;
  taskKey: string;
  taskTitle: string;
  /** The error, merge reason or setup failure, verbatim. Rendered inline, never in a tooltip. */
  detail: string | null;
  /** The run to open in the drawer, when the item is about a run rather than a task. */
  executionId: string | null;
  agentName: string | null;
  lane: LaneKey | null;
  /** When this happened — orders equal-severity items and drives the relative timestamp. */
  at: string;
  /** How many runs this item stands for (repeated failures, retry loops). */
  count: number;
  /** Spend attached to those runs. */
  cost: number;
  severity: number;
}

interface AgentRollup {
  agentId: string | null;
  agentName: string | null;
  agentRole: string | null;
  runs: number;
  succeeded: number;
  failed: number;
  running: number;
  /** Null until at least one run has finished — an average of nothing is not zero. */
  avgDurationSeconds: number | null;
  /** Share of *finished* runs that succeeded, 0–1. Null while nothing has finished. */
  successRate: number | null;
  cost: number;
  tokens: number;
  /** Spend on this agent's failed and cancelled runs. */
  wastedCost: number;
}

interface TaskSpend {
  taskId: string;
  taskKey: string;
  taskTitle: string;
  runs: number;
  cost: number;
}

type OutcomeKind =
  | "merged"
  | "blocked"
  | "prOpen"
  | "awaitingHuman"
  | "done"
  | "inProgress";

interface TaskGroup {
  taskId: string;
  taskKey: string;
  taskTitle: string;
  executions: ProjectExecutionRow[];
  firstStart: number;
}

/** One row per task, ordered by most recent activity first; bars keep chronological order. */
function groupByTask(rows: ProjectExecutionRow[]): TaskGroup[] {
  const map = new Map<string, TaskGroup>();
  for (const e of rows) {
    const g = map.get(e.taskId);
    const started = new Date(e.startedAt).getTime();
    if (g) {
      g.executions.push(e);
      g.firstStart = Math.min(g.firstStart, started);
    } else {
      map.set(e.taskId, { taskId: e.taskId, taskKey: e.taskKey, taskTitle: e.taskTitle, executions: [e], firstStart: started });
    }
  }
  const groups = [...map.values()];
  for (const g of groups) g.executions.sort((a, b) => a.startedAt.localeCompare(b.startedAt));
  groups.sort((a, b) => {
    const la = a.executions[a.executions.length - 1]!.startedAt;
    const lb = b.executions[b.executions.length - 1]!.startedAt;
    return lb.localeCompare(la);
  });
  return groups;
}

/**
 * Totals over the rows in hand. The server sends an exact {@link ActivitySummary} for the window;
 * this exists for the Timeline view and for the brushed sub-range, where the question really is
 * "what is on screen right now".
 */
function summarize(rows: ProjectExecutionRow[]) {
  let failed = 0;
  let cost = 0;
  let durationSeconds = 0;
  for (const e of rows) {
    if (e.status === "Failed") failed++;
    cost += e.cost;
    durationSeconds += e.durationSeconds ?? 0;
  }
  return { runs: rows.length, failed, cost, durationSeconds };
}

/** Groups runs by task *and* lane — the unit a retry loop happens in. */
function byTaskAndLane(rows: ProjectExecutionRow[]): Map<string, ProjectExecutionRow[]> {
  const map = new Map<string, ProjectExecutionRow[]>();
  for (const e of rows) {
    const key = `${e.taskId}::${e.lane}`;
    const bucket = map.get(key);
    if (bucket) bucket.push(e);
    else map.set(key, [e]);
  }
  for (const bucket of map.values()) bucket.sort((a, b) => a.startedAt.localeCompare(b.startedAt));
  return map;
}

function latestBy<T>(items: T[], at: (item: T) => string): T {
  return items.reduce((best, item) => (at(item) > at(best) ? item : best));
}

/**
 * What in this window is worth someone's time, worst first.
 *
 * Repeated failures of the same task in the same lane collapse into one entry carrying the most
 * recent error and a count — three lines saying the same thing about the same task is how a list
 * like this stops being read.
 */
function detectAttention(
  rows: ProjectExecutionRow[],
  tasks: ActivityTaskRow[],
  now: number = Date.now()
): AttentionItem[] {
  const items: AttentionItem[] = [];
  const groups = byTaskAndLane(rows);
  const runningByTask = new Set(rows.filter((e) => e.status === "Running").map((e) => e.taskId));

  for (const [key, group] of groups) {
    const failures = group.filter((e) => e.status === "Failed");
    if (failures.length > 0) {
      const latest = latestBy(failures, (e) => e.startedAt);
      items.push({
        id: `failed:${key}`,
        kind: "failed",
        taskId: latest.taskId,
        taskKey: latest.taskKey,
        taskTitle: latest.taskTitle,
        detail: latest.errorMessage,
        executionId: latest.executionId,
        agentName: latest.agentName,
        lane: latest.lane,
        at: latest.startedAt,
        count: failures.length,
        cost: failures.reduce((sum, e) => sum + e.cost, 0),
        severity: SEVERITY.failed,
      });
    }

    if (group.length >= RETRY_LOOP_THRESHOLD) {
      const latest = group[group.length - 1]!;
      items.push({
        id: `loop:${key}`,
        kind: "retryLoop",
        taskId: latest.taskId,
        taskKey: latest.taskKey,
        taskTitle: latest.taskTitle,
        detail: null,
        executionId: latest.executionId,
        agentName: latest.agentName,
        lane: latest.lane,
        at: latest.startedAt,
        count: group.length,
        cost: group.reduce((sum, e) => sum + e.cost, 0),
        severity: SEVERITY.retryLoop,
      });
    }
  }

  for (const e of rows) {
    if (e.status !== "Running") continue;
    if (now - new Date(e.startedAt).getTime() < LONG_RUN_MS) continue;
    items.push({
      id: `long:${e.executionId}`,
      kind: "longRunning",
      taskId: e.taskId,
      taskKey: e.taskKey,
      taskTitle: e.taskTitle,
      detail: null,
      executionId: e.executionId,
      agentName: e.agentName,
      lane: e.lane,
      at: e.startedAt,
      count: 1,
      cost: e.cost,
      severity: SEVERITY.longRunning,
    });
  }

  for (const task of tasks) {
    const base = {
      taskId: task.taskId,
      taskKey: task.taskKey,
      taskTitle: task.taskTitle,
      executionId: null,
      agentName: null,
      lane: task.currentLane,
      at: task.lastActivityAt,
      count: 1,
      cost: 0,
    };

    if (task.mergeFailureReason) {
      items.push({
        ...base,
        id: `merge:${task.taskId}`,
        kind: "mergeFailure",
        detail: task.mergeFailureReason,
        severity: SEVERITY.mergeFailure,
      });
    }

    if (task.setupFailure) {
      items.push({
        ...base,
        id: `setup:${task.taskId}`,
        kind: "setupFailure",
        detail: task.setupFailure,
        severity: SEVERITY.setupFailure,
      });
    }

    if (isAwaitingHuman(task)) {
      items.push({
        ...base,
        id: `human:${task.taskId}`,
        kind: "awaitingHuman",
        detail: null,
        severity: SEVERITY.awaitingHuman,
      });
    } else if (
      task.currentLane !== "Done" &&
      !task.hasAgentWorking &&
      !runningByTask.has(task.taskId) &&
      now - new Date(task.lastActivityAt).getTime() > STALL_MS
    ) {
      // Deliberately the fallback branch: a task waiting on a person is not stalled, it is
      // waiting on you, and saying both about the same task would be noise.
      items.push({
        ...base,
        id: `stalled:${task.taskId}`,
        kind: "stalled",
        detail: null,
        severity: SEVERITY.stalled,
      });
    }
  }

  return items.sort((a, b) => b.severity - a.severity || b.at.localeCompare(a.at));
}

function isAwaitingHuman(task: ActivityTaskRow): boolean {
  return (
    HUMAN_BLOCKED_LANES.includes(task.currentLane) ||
    HUMAN_BLOCKED_STATUSES.includes(task.status)
  );
}

/**
 * Per-agent performance for the window. Run counts come from the rows so that narrowing the range
 * restates them without a round trip; cost and tokens come from the server summary, because the usage
 * rows own those and the row-level `tokensUsed` counts neither cache reads nor writes.
 */
function rollupByAgent(
  rows: ProjectExecutionRow[],
  summary: ActivitySummary | null
): AgentRollup[] {
  const spendByAgent = new Map((summary?.agents ?? []).map((a) => [a.agentId, a]));
  const buckets = new Map<string, ProjectExecutionRow[]>();

  for (const e of rows) {
    const key = e.agentId ?? "";
    const bucket = buckets.get(key);
    if (bucket) bucket.push(e);
    else buckets.set(key, [e]);
  }

  return [...buckets.entries()]
    .map(([agentId, runs]) => {
      const finished = runs.filter((e) => e.durationSeconds !== null);
      const succeeded = runs.filter((e) => e.status === "Completed").length;
      const failed = runs.filter((e) => e.status === "Failed").length;
      const spend = agentId ? spendByAgent.get(agentId) : undefined;
      const first = runs[0]!;

      return {
        agentId: agentId || null,
        agentName: first.agentName,
        agentRole: first.agentRole,
        runs: runs.length,
        succeeded,
        failed,
        running: runs.filter((e) => e.status === "Running").length,
        avgDurationSeconds:
          finished.length === 0
            ? null
            : Math.round(
                (finished.reduce((sum, e) => sum + (e.durationSeconds ?? 0), 0) / finished.length) * 10
              ) / 10,
        successRate: succeeded + failed === 0 ? null : succeeded / (succeeded + failed),
        cost: spend?.cost ?? runs.reduce((sum, e) => sum + e.cost, 0),
        tokens: spend?.tokens ?? 0,
        wastedCost: runs
          .filter((e) => e.status === "Failed" || e.status === "Cancelled")
          .reduce((sum, e) => sum + e.cost, 0),
      };
    })
    .sort((a, b) => b.cost - a.cost || b.runs - a.runs);
}

/**
 * The tasks that consumed the most, biggest first.
 *
 * Filtered on an estimate worth showing: a task costing a thousandth of a cent rounds to nothing,
 * so listing it as a "costliest task" showing $0.00 reads as a bug.
 */
function topTasksBySpend(rows: ProjectExecutionRow[], limit = 5): TaskSpend[] {
  const map = new Map<string, TaskSpend>();
  for (const e of rows) {
    const existing = map.get(e.taskId);
    if (existing) {
      existing.cost += e.cost;
      existing.runs += 1;
    } else {
      map.set(e.taskId, {
        taskId: e.taskId,
        taskKey: e.taskKey,
        taskTitle: e.taskTitle,
        runs: 1,
        cost: e.cost,
      });
    }
  }
  return [...map.values()]
    .filter((t) => hasCostEstimate(t.cost))
    .sort((a, b) => b.cost - a.cost)
    .slice(0, limit);
}

interface RunBucket {
  /** Bucket start, epoch millis. */
  from: number;
  /** Bucket end (exclusive), epoch millis. */
  to: number;
  runs: number;
  failed: number;
}

/**
 * Runs per equal slice of the window, with failures counted separately so the strip can stack
 * them. Empty buckets are kept — a quiet afternoon is information, and dropping it would compress
 * the gaps out of the shape the strip exists to show.
 */
function bucketRuns(
  rows: ProjectExecutionRow[],
  from: number,
  to: number,
  count = 48
): RunBucket[] {
  const span = to - from;
  if (span <= 0 || count <= 0) return [];

  const width = span / count;
  const buckets: RunBucket[] = Array.from({ length: count }, (_, i) => ({
    from: from + i * width,
    to: from + (i + 1) * width,
    runs: 0,
    failed: 0,
  }));

  for (const e of rows) {
    const at = new Date(e.startedAt).getTime();
    if (at < from || at >= to) continue;
    // Guarded rather than trusted: floating-point width can put a run at the very end of the
    // window one index past the array.
    const bucket = buckets[Math.min(Math.floor((at - from) / width), count - 1)]!;
    bucket.runs += 1;
    if (e.status === "Failed") bucket.failed += 1;
  }

  return buckets;
}

/** Runs that started inside a sub-range of the window — what a brushed selection narrows to. */
function rowsInRange(rows: ProjectExecutionRow[], from: number, to: number): ProjectExecutionRow[] {
  return rows.filter((e) => {
    const at = new Date(e.startedAt).getTime();
    return at >= from && at <= to;
  });
}

/**
 * The lanes a task passed through in this window, in order and without repeats.
 *
 * Reconstructed from the lane stamped on each run rather than from a transition history, which does
 * not exist — and does not need to, since a lane with no run in it is a lane where nothing happened.
 */
function laneProgression(executions: ProjectExecutionRow[]): LaneKey[] {
  const ordered = [...executions].sort((a, b) => a.startedAt.localeCompare(b.startedAt));
  const lanes: LaneKey[] = [];
  for (const e of ordered) {
    if (lanes[lanes.length - 1] !== e.lane) lanes.push(e.lane);
  }
  return lanes;
}

/**
 * What a task amounts to right now. Ordered by what a person would want to hear first: a merge is
 * the end of the story, and being stuck outranks having an open PR, because the PR is why the
 * stuckness matters.
 */
function taskOutcome(task: ActivityTaskRow): OutcomeKind {
  if (task.isMerged) return "merged";
  if (task.mergeFailureReason || task.setupFailure) return "blocked";
  if (task.prUrl) return "prOpen";
  if (isAwaitingHuman(task)) return "awaitingHuman";
  if (task.currentLane === "Done") return "done";
  return "inProgress";
}

export {
  bucketRuns,
  detectAttention,
  groupByTask,
  laneProgression,
  rollupByAgent,
  rowsInRange,
  summarize,
  taskOutcome,
  topTasksBySpend,
  LONG_RUN_MS,
  RETRY_LOOP_THRESHOLD,
  STALL_MS,
};
export type {
  AgentRollup,
  AttentionItem,
  AttentionKind,
  OutcomeKind,
  RunBucket,
  TaskGroup,
  TaskSpend,
};
