import { describe, it, expect } from "vitest";
import { applyLaneChange } from "@/components/board/lane-reducer";
import type { TaskSummary, BoardLaneResponse } from "@/lib/types";
import type { LaneChangedEvent } from "@/hooks/use-board-signalr";

function task(id: string, lane: string, overrides: Partial<TaskSummary> = {}): TaskSummary {
  return {
    id,
    number: 1,
    code: "P-1",
    title: `Task ${id}`,
    priority: "medium",
    type: "Feature",
    status: "InQueue",
    currentLane: lane as TaskSummary["currentLane"],
    branchName: null,
    prUrl: null,
    queuePosition: null,
    tags: [],
    repoId: "repo-1",
    repoName: null,
    repoColor: null,
    assigneeId: null,
    assigneeName: null,
    assigneeAvatarUrl: null,
    isBlocked: false,
    blockedBy: [],
    mergeFailureReason: null,
    hasUnread: false,
    createdAt: "2026-01-01T00:00:00Z",
    lastAgentMessage: null,
    ...overrides,
  } as TaskSummary;
}

function lane(key: string, tasks: TaskSummary[]): BoardLaneResponse {
  return { key, label: key, order: 0, tasks } as BoardLaneResponse;
}

function event(overrides: Partial<LaneChangedEvent> = {}): LaneChangedEvent {
  return {
    taskId: "t1",
    taskTitle: "Task t1",
    fromLane: "PlanReady",
    toLane: "Developing",
    status: "Active",
    timestamp: "2026-01-01T00:00:00Z",
    ...overrides,
  };
}

const find = (lanes: BoardLaneResponse[], id: string) =>
  lanes.flatMap((l) => l.tasks).filter((t) => t.id === id);

describe("applyLaneChange", () => {
  it("moves the task between lanes", () => {
    const lanes = [
      lane("PlanReady", [task("t1", "PlanReady")]),
      lane("Developing", []),
    ];

    const next = applyLaneChange(lanes, event(), null);

    expect(next[0].tasks).toHaveLength(0);
    expect(next[1].tasks.map((t) => t.id)).toEqual(["t1"]);
    expect(next[1].tasks[0].currentLane).toBe("Developing");
    expect(next[1].tasks[0].status).toBe("Active");
  });

  it("keeps the task on the board when the agent re-queues it in place", () => {
    // Plan approval parks the task in Developing; the worker then claims it and
    // emits fromLane === toLane. The card must not vanish.
    const lanes = [
      lane("PlanReady", []),
      lane("Developing", [task("t1", "Developing")]),
    ];

    const next = applyLaneChange(
      lanes,
      event({ fromLane: "Developing", toLane: "Developing" }),
      null
    );

    expect(find(next, "t1")).toHaveLength(1);
    expect(next[1].tasks[0].status).toBe("Active");
  });

  it("keeps the task on the board when the destination lane is not rendered", () => {
    // The Testing lane only exists when QA or manual verification is enabled
    const lanes = [lane("Developing", [task("t1", "Developing")])];

    const next = applyLaneChange(
      lanes,
      event({ fromLane: "Developing", toLane: "Testing" }),
      null
    );

    expect(find(next, "t1")).toHaveLength(1);
  });

  it("does not duplicate a task already in the destination lane", () => {
    const lanes = [
      lane("PlanReady", [task("t1", "PlanReady")]),
      lane("Developing", [task("t1", "Developing")]),
    ];

    const next = applyLaneChange(lanes, event(), null);

    expect(find(next, "t1")).toHaveLength(1);
  });

  it("marks the moved task unread unless it is the one open in the modal", () => {
    const lanes = [
      lane("PlanReady", [task("t1", "PlanReady")]),
      lane("Developing", []),
    ];

    expect(applyLaneChange(lanes, event(), null)[1].tasks[0].hasUnread).toBe(true);
    expect(applyLaneChange(lanes, event(), "t1")[1].tasks[0].hasUnread).toBe(false);
  });

  it("does not mark an in-place re-queue unread while its modal is open", () => {
    const lanes = [lane("Developing", [task("t1", "Developing")])];
    const inPlace = event({ fromLane: "Developing", toLane: "Developing" });

    expect(applyLaneChange(lanes, inPlace, null)[0].tasks[0].hasUnread).toBe(true);
    expect(applyLaneChange(lanes, inPlace, "t1")[0].tasks[0].hasUnread).toBe(false);
  });
});
