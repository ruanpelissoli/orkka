import { LANE_KEYS } from "@/lib/constants";
import type { ApplyTaskChatAction } from "@/lib/types";

/**
 * What "apply" does to the task in its current lane — the server decides the same way:
 * an answer while the agent waits on questions, a plan-change request while a plan waits
 * for approval, a plain post anywhere else.
 */
export type TaskDiscussApplyMode = "answer" | "planChanges" | "post";

export function taskDiscussApplyMode(lane: string | null | undefined): TaskDiscussApplyMode {
  if (lane === LANE_KEYS.PENDING_QUESTIONS) return "answer";
  if (lane === LANE_KEYS.PLAN_READY) return "planChanges";
  return "post";
}

/** `dashboard.taskDiscuss` keys of the per-message and summary apply buttons, per mode. */
export const TASK_DISCUSS_APPLY_KEYS: Record<
  TaskDiscussApplyMode,
  { message: string; summary: string }
> = {
  answer: { message: "applyAnswer", summary: "summaryAnswer" },
  planChanges: { message: "applyPlanChanges", summary: "summaryPlanChanges" },
  post: { message: "applyPost", summary: "summaryPost" },
};

/** `dashboard.taskDiscuss` key of the confirmation toast for what the apply turned into. */
export const TASK_DISCUSS_APPLIED_KEYS: Record<ApplyTaskChatAction, string> = {
  answered: "appliedAnswered",
  planChangesRequested: "appliedPlanChanges",
  posted: "appliedPosted",
};
