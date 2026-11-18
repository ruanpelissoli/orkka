import { useMemo } from "react";
import { useTranslations } from "use-intl";
import { Link } from "@/i18n/navigation";
import { Badge, GlassCard } from "@/components/ui";
import { LANE_MAP } from "@/lib/constants";
import { useLabels } from "@/hooks/use-labels";
import { hasCostEstimate, formatUsdEstimate, formatRelativeTime } from "@/lib/utils";
import type { ActivityTaskRow, ProjectExecutionRow } from "@/lib/types";
import { laneProgression, taskOutcome, type OutcomeKind } from "./activity-model";

interface OutcomeListProps {
  tasks: ActivityTaskRow[];
  rows: ProjectExecutionRow[];
  projectId: string;
}

const OUTCOME_VARIANT: Record<OutcomeKind, "success" | "error" | "primary" | "info" | "default"> = {
  merged: "success",
  blocked: "error",
  prOpen: "primary",
  awaitingHuman: "info",
  done: "success",
  inProgress: "default",
};

/**
 * Zone two: what the runs amounted to, one row per task.
 *
 * This is the part the old page had no answer for. A row here is a small sentence — this task went
 * Planning → Developing → Testing, cost this much, and ended up with an open PR — which is the
 * thing someone actually wants to carry away from "what happened overnight".
 */
function OutcomeList({ tasks, rows, projectId }: OutcomeListProps) {
  const t = useTranslations("dashboard.activityPage");
  const labels = useLabels();

  const runsByTask = useMemo(() => {
    const map = new Map<string, ProjectExecutionRow[]>();
    for (const e of rows) {
      const bucket = map.get(e.taskId);
      if (bucket) bucket.push(e);
      else map.set(e.taskId, [e]);
    }
    return map;
  }, [rows]);

  if (tasks.length === 0) {
    return (
      <GlassCard className="text-center">
        <p className="text-sm text-gray-300">{t("empty")}</p>
        <p className="mt-1 text-xs text-gray-400">{t("emptyHint")}</p>
      </GlassCard>
    );
  }

  return (
    <GlassCard padding="none" className="overflow-hidden">
      <ul className="divide-y divide-white/[0.04]" data-testid="outcome-list">
        {tasks.map((task) => {
          const runs = runsByTask.get(task.taskId) ?? [];
          const lanes = laneProgression(runs);
          const cost = runs.reduce((sum, e) => sum + e.cost, 0);
          const outcome = taskOutcome(task);
          const cycles = cycleNote(task);

          return (
            <li key={task.taskId} className="px-4 py-3 hover:bg-white/[0.02]" data-outcome={outcome}>
              <div className="flex flex-wrap items-center gap-2">
                <Link
                  href={`/p/${projectId}/tasks/${task.taskId}`}
                  className="truncate text-sm text-gray-200 hover:text-primary-400"
                  title={`${task.taskKey} · ${task.taskTitle}`}
                >
                  <span className="font-mono text-xs text-gray-400">{task.taskKey}</span>{" "}
                  {task.taskTitle}
                </Link>
                <Badge variant={OUTCOME_VARIANT[outcome]} size="sm">
                  {t(`outcome.${outcome}`)}
                </Badge>
                {task.prUrl && (
                  // Leaves the app for the forge, so it is a plain anchor rather than a locale Link.
                  <a
                    href={task.prUrl}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="text-xs text-primary-400 hover:text-primary-300"
                  >
                    {t("viewPr")}
                  </a>
                )}
                <span className="ml-auto text-xs text-gray-400">
                  {formatRelativeTime(task.lastActivityAt)}
                </span>
              </div>

              <div className="mt-1.5 flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-gray-400">
                {lanes.length > 0 && (
                  <span className="inline-flex flex-wrap items-center gap-1">
                    {lanes.map((lane, i) => (
                      <span key={`${lane}-${i}`} className="inline-flex items-center gap-1">
                        {i > 0 && <span className="text-gray-400">→</span>}
                        <span
                          className="inline-block h-2 w-2 rounded-sm"
                          style={{ backgroundColor: LANE_MAP[lane]?.color }}
                        />
                        {labels.lane(lane)}
                      </span>
                    ))}
                  </span>
                )}
                {runs.length > 0 && <span>{t("summaryRuns", { count: runs.length })}</span>}
                {hasCostEstimate(cost) && <span>{formatUsdEstimate(cost)}</span>}
                {cycles && <span className="text-amber-400/80">{t(cycles.key, cycles.values)}</span>}
              </div>
            </li>
          );
        })}
      </ul>
    </GlassCard>
  );
}

/**
 * Rework worth mentioning. A task that went round code review three times cost three times the
 * review, and that only shows up as a number nobody looks at unless the row says so.
 */
function cycleNote(task: ActivityTaskRow): { key: string; values: { count: number } } | null {
  if (task.reviewCycleCount > 1) return { key: "reviewCycles", values: { count: task.reviewCycleCount } };
  if (task.qaCycleCount > 1) return { key: "qaCycles", values: { count: task.qaCycleCount } };
  if (task.baseSyncCycleCount > 1) return { key: "baseSyncCycles", values: { count: task.baseSyncCycleCount } };
  return null;
}

export { OutcomeList };
export type { OutcomeListProps };
