import { LANE_KEYS } from "@/lib/constants";
import type { TaskSummary } from "@/lib/types";

/**
 * Statuses where the pipeline is stalled until a person acts. Same set the card's attention
 * banner keys on — a chip that says "3 need you" has to agree with the three cards that are
 * glowing, or one of them is lying.
 */
const NEEDS_YOU_STATUSES = new Set([
  "WaitingAnswer",
  "WaitingPlanApproval",
  "WaitingCodeReview",
  "WaitingQaReview",
  "WaitingUserVerification",
]);

/**
 * Stalled on the platform rather than on a person: nobody is being asked for anything, the work
 * simply cannot proceed. Kept apart from "needs you" because the fix is different — these clear
 * on their own or need an operator, not an answer.
 */
const STALLED_STATUSES = new Set(["OutOfTokens", "WaitingForRunner", "ReasoningTimeout"]);

/** Waiting on a human. */
function needsYou(task: TaskSummary): boolean {
  return NEEDS_YOU_STATUSES.has(task.status);
}

/** Held up by a dependency or explicitly blocked. */
function isBlocked(task: TaskSummary): boolean {
  return task.isBlocked || (task.blockedBy?.length ?? 0) > 0;
}

/** The last merge attempt failed — conflicts, branch protection, CI. */
function mergeFailed(task: TaskSummary): boolean {
  return task.mergeFailureReason !== null && task.mergeFailureReason !== undefined;
}

/** An agent is mid-run on this task right now. */
function agentWorking(task: TaskSummary): boolean {
  return (
    task.currentLane === LANE_KEYS.PLANNING || task.currentLane === LANE_KEYS.DEVELOPING
  );
}

function isStalled(task: TaskSummary): boolean {
  return STALLED_STATUSES.has(task.status);
}

type BoardFilterKey = "needsYou" | "blocked" | "mergeFailed" | "agentWorking" | "stalled";

interface BoardFilterDef {
  key: BoardFilterKey;
  /** Colour of the condition, so a chip looks like the cards it selects. */
  color: string;
  match: (task: TaskSummary) => boolean;
}

/**
 * The board's problem filters, worst-first. Colours are the same palette the attention states
 * use on the cards themselves.
 */
const BOARD_FILTERS: readonly BoardFilterDef[] = [
  { key: "needsYou", color: "var(--color-lane-questions)", match: needsYou },
  { key: "blocked", color: "var(--color-error)", match: isBlocked },
  { key: "mergeFailed", color: "var(--color-warning)", match: mergeFailed },
  { key: "stalled", color: "var(--color-warning)", match: isStalled },
  { key: "agentWorking", color: "var(--color-lane-developing)", match: agentWorking },
];

export {
  BOARD_FILTERS,
  NEEDS_YOU_STATUSES,
  STALLED_STATUSES,
  agentWorking,
  isBlocked,
  isStalled,
  mergeFailed,
  needsYou,
};
export type { BoardFilterDef, BoardFilterKey };
