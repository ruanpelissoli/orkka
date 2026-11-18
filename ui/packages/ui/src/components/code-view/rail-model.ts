import type { BoardLaneResponse, TaskSummary } from "@/lib/types";
import { LANE_KEYS, type LaneKey } from "@/lib/constants";

/**
 * Which tasks the Code View rail shows, and in what order. Pure, so it is testable and the
 * kanban's lane model stays the single source of truth.
 *
 * - Lanes from Developing onward. Pending Questions joins only when the task already has a
 *   branch — a dev-lane task that stopped to ask; a pre-dev question has nothing to show here.
 * - Agent-active first (someone should be watching), then lane order, then most recent.
 * - Done is capped: the last DONE_WINDOW_DAYS, at most DONE_MAX, newest first — a rail is not
 *   an archive.
 */

const DONE_WINDOW_DAYS = 7;
const DONE_MAX = 20;

const RAIL_LANE_ORDER: LaneKey[] = [
  LANE_KEYS.DEVELOPING,
  LANE_KEYS.TESTING,
  LANE_KEYS.CODE_REVIEW,
  LANE_KEYS.PENDING_QUESTIONS,
  LANE_KEYS.DONE,
];

interface RailModel {
  /** In-flight tasks (everything but Done), already ordered. */
  active: TaskSummary[];
  /** Recently completed tasks, newest first, capped. */
  done: TaskSummary[];
  /** How many Done tasks fell outside the window/cap. */
  doneHidden: number;
}

function isAgentActive(task: TaskSummary): boolean {
  return task.status === "Active" || task.status === "InQueue";
}

function recency(task: TaskSummary): number {
  const stamp = task.completedAt ?? task.createdAt;
  const ms = Date.parse(stamp);
  return Number.isFinite(ms) ? ms : 0;
}

function buildRailModel(lanes: BoardLaneResponse[], now: number = Date.now()): RailModel {
  const byLane = new Map<string, TaskSummary[]>();
  for (const lane of lanes) byLane.set(lane.key, lane.tasks);

  const active: TaskSummary[] = [];
  for (const key of RAIL_LANE_ORDER) {
    if (key === LANE_KEYS.DONE) continue;
    const tasks = byLane.get(key) ?? [];
    for (const task of tasks) {
      if (key === LANE_KEYS.PENDING_QUESTIONS && !task.branchName) continue;
      active.push(task);
    }
  }
  const laneRank = (t: TaskSummary) => {
    const i = RAIL_LANE_ORDER.indexOf(t.currentLane);
    return i < 0 ? RAIL_LANE_ORDER.length : i;
  };
  active.sort((a, b) => {
    const aa = isAgentActive(a) ? 0 : 1;
    const bb = isAgentActive(b) ? 0 : 1;
    if (aa !== bb) return aa - bb;
    const lr = laneRank(a) - laneRank(b);
    if (lr !== 0) return lr;
    return recency(b) - recency(a);
  });

  const cutoff = now - DONE_WINDOW_DAYS * 24 * 60 * 60 * 1000;
  const doneAll = (byLane.get(LANE_KEYS.DONE) ?? []).slice().sort((a, b) => recency(b) - recency(a));
  const doneRecent = doneAll.filter((t) => recency(t) >= cutoff).slice(0, DONE_MAX);

  return { active, done: doneRecent, doneHidden: doneAll.length - doneRecent.length };
}

export { buildRailModel, isAgentActive, DONE_WINDOW_DAYS, DONE_MAX, RAIL_LANE_ORDER };
export type { RailModel };
