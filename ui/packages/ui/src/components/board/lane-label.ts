import { LANE_MAP, type LaneKey } from "@/lib/constants";

/** A use-intl translator, narrowed to what the lookup needs. */
interface Translator {
  (key: string): string;
  has(key: string): boolean;
}

/**
 * A lane's name in the reader's language: `common.lanes.<Key>` (every lane but Testing), then
 * `dashboard.taskMisc.lanes.<Key>` (Testing), then the given fallback (the board endpoint's
 * English label), then the built-in English label. Keys are the enum names ("CodeReview") —
 * the board's snake_case keys are normalized at the API boundary.
 */
export function laneLabel(key: string, tCommon: Translator, tDash: Translator, fallback?: string): string {
  if (tCommon.has(`lanes.${key}`)) return tCommon(`lanes.${key}`);
  if (tDash.has(`taskMisc.lanes.${key}`)) return tDash(`taskMisc.lanes.${key}`);
  return fallback ?? LANE_MAP[key as LaneKey]?.label ?? key;
}
