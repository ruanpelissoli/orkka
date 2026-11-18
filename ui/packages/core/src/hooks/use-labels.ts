import { useTranslations } from "use-intl";
import {
  AGENT_ROLE_MAP,
  LANE_MAP,
  PLAN_STATUSES,
  PRIORITIES,
  TASK_STATUS_MAP,
  TASK_TYPE_MAP,
  type AgentRoleKey,
  type LaneKey,
  type TaskStatusKey,
  type TaskTypeKey,
} from "@/lib/constants";

type LabelGroup =
  | "lanes"
  | "taskStatuses"
  | "priorities"
  | "taskTypes"
  | "agentRoles"
  | "planStatuses";

type LabelFn = (key: string | null | undefined) => string;

export interface Labels {
  /** Lane key (`Developing`) → localised lane name. */
  lane: LabelFn;
  /** Task status key (`WaitingCodeReview`) → localised status. */
  taskStatus: LabelFn;
  /** Priority key (`high`) → localised priority. */
  priority: LabelFn;
  /** Task type key (`BugFix`) → localised type. */
  taskType: LabelFn;
  /** Agent role key (`FrontendDev`) → localised role. */
  agentRole: LabelFn;
  /** Plan status key (`WaitingPlanApproval`) → localised plan status. */
  planStatus: LabelFn;
}

/**
 * Localised names for the fixed vocabularies in `@/lib/constants` — lanes, task statuses,
 * priorities, task types, agent roles, plan statuses.
 *
 * The constants keep their English `label` as the canonical fallback (tests and non-React
 * code read it); anything rendered to a person should go through here instead. Lookup order:
 * `common.<group>.<key>` → `dashboard.taskMisc.<group>.<key>` (keys the common catalog has not
 * caught up with yet) → the English label from the constants → the raw key. A new enum value
 * therefore degrades to English, never to a key path.
 */
export function useLabels(): Labels {
  const common = useTranslations("common");
  const misc = useTranslations("dashboard.taskMisc");

  const pick = (group: LabelGroup, key: string | null | undefined, fallback?: string): string => {
    if (!key) return fallback ?? "";
    const path = `${group}.${key}`;
    if (common.has(path)) return common(path);
    if (misc.has(path)) return misc(path);
    return fallback ?? key;
  };

  return {
    lane: (key) => pick("lanes", key, key ? LANE_MAP[key as LaneKey]?.label : undefined),
    taskStatus: (key) =>
      pick("taskStatuses", key, key ? TASK_STATUS_MAP[key as TaskStatusKey]?.label : undefined),
    priority: (key) =>
      pick(
        "priorities",
        key?.toLowerCase(),
        PRIORITIES.find((p) => p.key === key?.toLowerCase())?.label
      ),
    taskType: (key) =>
      pick("taskTypes", key, key ? TASK_TYPE_MAP[key as TaskTypeKey]?.label : undefined),
    agentRole: (key) =>
      pick("agentRoles", key, key ? AGENT_ROLE_MAP[key as AgentRoleKey]?.label : undefined),
    planStatus: (key) =>
      pick("planStatuses", key, PLAN_STATUSES.find((s) => s.key === key)?.label),
  };
}
