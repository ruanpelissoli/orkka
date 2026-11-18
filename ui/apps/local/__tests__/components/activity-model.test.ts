import { describe, it, expect } from "vitest";
// Straight at the module, not through the barrel: these are pure functions, and routing the
// import through the components would drag the router and use-intl into a test that needs no DOM.
import {
  bucketRuns,
  detectAttention,
  groupByTask,
  laneProgression,
  rollupByAgent,
  rowsInRange,
  summarize,
  taskOutcome,
  topTasksBySpend,
} from "@/components/activity/activity-model";
import type { ActivitySummary, ActivityTaskRow, ProjectExecutionRow } from "@/lib/types";

/** Fixed clock so "45 minutes ago" means the same thing on every machine. */
const NOW = Date.parse("2026-08-20T12:00:00Z");
const minutesAgo = (m: number) => new Date(NOW - m * 60_000).toISOString();

function row(overrides: Partial<ProjectExecutionRow> = {}): ProjectExecutionRow {
  return {
    executionId: "e1",
    taskId: "t1",
    taskKey: "ORK-1",
    taskTitle: "Add login",
    agentId: "a1",
    agentName: "Pixel",
    agentRole: "FrontendDev",
    lane: "Developing",
    status: "Completed",
    startedAt: minutesAgo(60),
    completedAt: minutesAgo(50),
    durationSeconds: 600,
    tokensUsed: 1000,
    cost: 0.5,
    errorMessage: null,
    ...overrides,
  };
}

function task(overrides: Partial<ActivityTaskRow> = {}): ActivityTaskRow {
  return {
    taskId: "t1",
    taskKey: "ORK-1",
    taskTitle: "Add login",
    currentLane: "Developing",
    status: "Active",
    prUrl: null,
    isMerged: false,
    prClosedAt: null,
    mergeFailureReason: null,
    mergeFailureKind: null,
    setupFailure: null,
    reviewCycleCount: 0,
    qaCycleCount: 0,
    checkCycleCount: 0,
    baseSyncCycleCount: 0,
    hasAgentWorking: false,
    lastActivityAt: minutesAgo(50),
    ...overrides,
  };
}

describe("groupByTask", () => {
  it("groups executions per task, bars chronological, most recent task first", () => {
    const groups = groupByTask([
      row({ executionId: "e1", taskId: "t1", startedAt: "2026-08-17T01:00:00Z" }),
      row({ executionId: "e2", taskId: "t2", taskKey: "ORK-2", startedAt: "2026-08-17T03:00:00Z" }),
      row({ executionId: "e3", taskId: "t1", lane: "Testing", startedAt: "2026-08-17T02:00:00Z" }),
    ]);

    expect(groups.map((g) => g.taskKey)).toEqual(["ORK-2", "ORK-1"]);
    expect(groups[1]!.executions.map((e) => e.executionId)).toEqual(["e1", "e3"]);
  });
});

describe("summarize", () => {
  it("totals runs, failures, cost and duration", () => {
    const s = summarize([
      row({}),
      row({ executionId: "e2", status: "Failed", cost: 0.25, durationSeconds: 30 }),
      row({ executionId: "e3", status: "Running", completedAt: null, durationSeconds: null, cost: 0 }),
    ]);
    expect(s).toEqual({ runs: 3, failed: 1, cost: 0.75, durationSeconds: 630 });
  });
});

describe("detectAttention", () => {
  it("collapses repeated failures of one task and lane into a single entry", () => {
    const items = detectAttention(
      [
        row({ executionId: "e1", status: "Failed", errorMessage: "first", startedAt: minutesAgo(90) }),
        row({ executionId: "e2", status: "Failed", errorMessage: "latest", startedAt: minutesAgo(30) }),
      ],
      [task()],
      NOW
    );

    const failed = items.filter((i) => i.kind === "failed");
    expect(failed).toHaveLength(1);
    // The newest message is the one worth showing; the count carries the rest.
    expect(failed[0]!.detail).toBe("latest");
    expect(failed[0]!.count).toBe(2);
  });

  it("keeps failures of the same task in different lanes apart", () => {
    const items = detectAttention(
      [
        row({ executionId: "e1", status: "Failed", errorMessage: "build", lane: "Developing" }),
        row({ executionId: "e2", status: "Failed", errorMessage: "tests", lane: "Testing" }),
      ],
      [task()],
      NOW
    );
    expect(items.filter((i) => i.kind === "failed")).toHaveLength(2);
  });

  it("flags a retry loop at three runs in one lane and sums their cost", () => {
    const rows = [1, 2, 3].map((i) =>
      row({ executionId: `e${i}`, startedAt: minutesAgo(60 - i * 10), cost: 0.5 })
    );
    const loop = detectAttention(rows, [task()], NOW).find((i) => i.kind === "retryLoop");

    expect(loop).toBeDefined();
    expect(loop!.count).toBe(3);
    expect(loop!.cost).toBeCloseTo(1.5);
  });

  it("does not flag a loop below the threshold", () => {
    const rows = [1, 2].map((i) => row({ executionId: `e${i}` }));
    expect(detectAttention(rows, [task()], NOW).some((i) => i.kind === "retryLoop")).toBe(false);
  });

  it("surfaces merge and setup failures from the task, with no run behind them", () => {
    const items = detectAttention(
      [],
      [
        task({ taskId: "t1", mergeFailureReason: "conflicts in app.ts" }),
        task({ taskId: "t2", taskKey: "ORK-2", setupFailure: "npm install failed" }),
      ],
      NOW
    );

    expect(items.find((i) => i.kind === "mergeFailure")?.detail).toBe("conflicts in app.ts");
    expect(items.find((i) => i.kind === "setupFailure")?.detail).toBe("npm install failed");
    expect(items.every((i) => i.executionId === null)).toBe(true);
  });

  it("calls a long-running run out, but only past the threshold", () => {
    const fresh = row({ status: "Running", completedAt: null, startedAt: minutesAgo(10) });
    const old = row({ executionId: "e2", status: "Running", completedAt: null, startedAt: minutesAgo(90) });

    expect(detectAttention([fresh], [task()], NOW).some((i) => i.kind === "longRunning")).toBe(false);
    expect(detectAttention([old], [task()], NOW).some((i) => i.kind === "longRunning")).toBe(true);
  });

  it("treats a task waiting on a person as waiting, never as stalled", () => {
    const items = detectAttention(
      [],
      [task({ currentLane: "PendingQuestions", lastActivityAt: minutesAgo(600) })],
      NOW
    );
    expect(items.map((i) => i.kind)).toEqual(["awaitingHuman"]);
  });

  it("marks a quiet unfinished task stalled, but not a done or busy one", () => {
    const quiet = task({ taskId: "t1", lastActivityAt: minutesAgo(300) });
    const done = task({ taskId: "t2", currentLane: "Done", lastActivityAt: minutesAgo(300) });
    const busy = task({ taskId: "t3", hasAgentWorking: true, lastActivityAt: minutesAgo(300) });

    const kinds = detectAttention([], [quiet, done, busy], NOW).map((i) => `${i.taskId}:${i.kind}`);
    expect(kinds).toEqual(["t1:stalled"]);
  });

  it("does not call a task stalled while one of its runs is still going", () => {
    const items = detectAttention(
      [row({ status: "Running", completedAt: null, startedAt: minutesAgo(5) })],
      [task({ lastActivityAt: minutesAgo(300) })],
      NOW
    );
    expect(items.some((i) => i.kind === "stalled")).toBe(false);
  });

  it("orders worst first", () => {
    const items = detectAttention(
      [row({ status: "Failed", errorMessage: "boom" })],
      [
        task({ taskId: "t1" }),
        task({ taskId: "t2", taskKey: "ORK-2", currentLane: "PlanReady" }),
        task({ taskId: "t3", taskKey: "ORK-3", setupFailure: "no runner" }),
        task({ taskId: "t4", taskKey: "ORK-4", lastActivityAt: minutesAgo(300) }),
      ],
      NOW
    );
    expect(items.map((i) => i.kind)).toEqual(["failed", "setupFailure", "awaitingHuman", "stalled"]);
  });
});

describe("rollupByAgent", () => {
  const summary: ActivitySummary = {
    runs: 3,
    running: 0,
    completed: 2,
    failed: 1,
    cancelled: 0,
    tasksTouched: 2,
    durationSeconds: 900,
    cost: 9,
    wastedCost: 1,
    tokens: 90_000,
    agents: [
      { agentId: "a1", cost: 3, tokens: 30_000 },
      { agentId: "a2", cost: 6, tokens: 60_000 },
    ],
  };

  it("counts runs from the rows and takes money from the server summary", () => {
    const rollups = rollupByAgent(
      [
        row({ executionId: "e1", agentId: "a1", cost: 0.1 }),
        row({ executionId: "e2", agentId: "a1", status: "Failed", cost: 0.1 }),
        row({ executionId: "e3", agentId: "a2", agentName: "Rex", cost: 0.1 }),
      ],
      summary
    );

    // Biggest spender first, regardless of who ran the most.
    expect(rollups.map((r) => r.agentId)).toEqual(["a2", "a1"]);
    const a1 = rollups.find((r) => r.agentId === "a1")!;
    expect(a1.runs).toBe(2);
    expect(a1.failed).toBe(1);
    expect(a1.successRate).toBe(0.5);
    // Not the 0.2 the rows add up to — the rows' costs are a subset when the page is truncated.
    expect(a1.cost).toBe(3);
    expect(a1.tokens).toBe(30_000);
  });

  it("falls back to row costs for an agent the summary does not price", () => {
    const rollups = rollupByAgent([row({ agentId: "ghost", cost: 0.25 })], summary);
    expect(rollups[0]!.cost).toBe(0.25);
    expect(rollups[0]!.tokens).toBe(0);
  });

  it("leaves the success rate null while nothing has finished", () => {
    const rollups = rollupByAgent(
      [row({ status: "Running", completedAt: null, durationSeconds: null })],
      null
    );
    expect(rollups[0]!.successRate).toBeNull();
    expect(rollups[0]!.avgDurationSeconds).toBeNull();
  });
});

describe("topTasksBySpend", () => {
  it("sums per task, drops free ones and keeps the biggest", () => {
    const top = topTasksBySpend(
      [
        row({ executionId: "e1", taskId: "t1", cost: 1 }),
        row({ executionId: "e2", taskId: "t1", cost: 2 }),
        row({ executionId: "e3", taskId: "t2", taskKey: "ORK-2", cost: 5 }),
        row({ executionId: "e4", taskId: "t3", taskKey: "ORK-3", cost: 0 }),
      ],
      2
    );

    expect(top.map((t) => t.taskId)).toEqual(["t2", "t1"]);
    expect(top[1]!.cost).toBe(3);
    expect(top[1]!.runs).toBe(2);
  });
});

describe("laneProgression", () => {
  it("reads the lanes off the runs in order, without repeats", () => {
    const lanes = laneProgression([
      row({ executionId: "e2", lane: "Developing", startedAt: minutesAgo(50) }),
      row({ executionId: "e1", lane: "Planning", startedAt: minutesAgo(60) }),
      row({ executionId: "e3", lane: "Developing", startedAt: minutesAgo(40) }),
      row({ executionId: "e4", lane: "Testing", startedAt: minutesAgo(30) }),
    ]);
    expect(lanes).toEqual(["Planning", "Developing", "Testing"]);
  });
});

describe("bucketRuns", () => {
  const from = Date.parse("2026-08-20T00:00:00Z");
  const to = Date.parse("2026-08-20T04:00:00Z");
  const at = (iso: string) => ({ startedAt: iso });

  it("counts runs into equal slices and keeps the quiet ones", () => {
    const buckets = bucketRuns(
      [
        row({ executionId: "e1", ...at("2026-08-20T00:30:00Z") }),
        row({ executionId: "e2", ...at("2026-08-20T00:45:00Z") }),
        row({ executionId: "e3", ...at("2026-08-20T03:10:00Z"), status: "Failed" }),
      ],
      from,
      to,
      4
    );

    expect(buckets).toHaveLength(4);
    expect(buckets.map((b) => b.runs)).toEqual([2, 0, 0, 1]);
    expect(buckets.map((b) => b.failed)).toEqual([0, 0, 0, 1]);
  });

  it("keeps a run on the window's trailing edge inside the last bucket", () => {
    const buckets = bucketRuns([row({ ...at("2026-08-20T03:59:59.999Z") })], from, to, 4);
    expect(buckets[3]!.runs).toBe(1);
  });

  it("ignores runs outside the window and degenerate spans", () => {
    expect(bucketRuns([row({ ...at("2026-08-19T23:00:00Z") })], from, to, 4).every((b) => b.runs === 0)).toBe(true);
    expect(bucketRuns([row()], to, from, 4)).toEqual([]);
  });
});

describe("rowsInRange", () => {
  it("keeps only the runs that started inside the brush", () => {
    const kept = rowsInRange(
      [
        row({ executionId: "e1", startedAt: "2026-08-20T01:00:00Z" }),
        row({ executionId: "e2", startedAt: "2026-08-20T05:00:00Z" }),
      ],
      Date.parse("2026-08-20T00:00:00Z"),
      Date.parse("2026-08-20T02:00:00Z")
    );
    expect(kept.map((r) => r.executionId)).toEqual(["e1"]);
  });
});

describe("taskOutcome", () => {
  it("ranks merged above everything, and blocked above an open PR", () => {
    expect(taskOutcome(task({ isMerged: true, mergeFailureReason: "x" }))).toBe("merged");
    expect(taskOutcome(task({ prUrl: "https://pr", mergeFailureReason: "conflicts" }))).toBe("blocked");
    expect(taskOutcome(task({ prUrl: "https://pr" }))).toBe("prOpen");
    expect(taskOutcome(task({ currentLane: "PendingQuestions" }))).toBe("awaitingHuman");
    expect(taskOutcome(task({ status: "WaitingPlanApproval" }))).toBe("awaitingHuman");
    expect(taskOutcome(task({ currentLane: "Done" }))).toBe("done");
    expect(taskOutcome(task())).toBe("inProgress");
  });
});
