import { useMemo } from "react";
import { useTranslations } from "use-intl";
import { Link } from "@/i18n/navigation";
import { GlassCard } from "@/components/ui";
import { LANES, LANE_MAP } from "@/lib/constants";
import { useLabels } from "@/hooks/use-labels";
import { cn, hasCostEstimate, formatUsdEstimate } from "@/lib/utils";
import type { ProjectExecutionRow } from "@/lib/types";
import { formatDuration } from "@/components/team/AgentOpsStats";
import { groupByTask } from "./activity-model";

interface ExecutionTimelineProps {
  /** Route ref (slug or ULID) — what the task links are built from. */
  projectId: string;
  rows: ProjectExecutionRow[];
  /** Window edges as epoch millis; the right edge is "now" for live windows. */
  from: number;
  to: number;
  /** Opens a run. Bars are inert without it. */
  onSelectRun?: (row: ProjectExecutionRow) => void;
}

/**
 * Every run in the window as bars on a shared time axis, one row per task, coloured by lane.
 * Hand-rolled with divs (no chart lib) so it stays cheap for a few hundred bars.
 *
 * The secondary view on the Activity page, behind the digest. It is good at showing *shape* — a
 * burst at 2am, a dead afternoon, one task hogging an agent all night — and bad at answering what
 * came of any of it, which is why it is no longer the thing you land on. Purely presentational:
 * the page above owns fetching, filters and the window.
 */
function ExecutionTimeline({ projectId, rows, from, to, onSelectRun }: ExecutionTimelineProps) {
  const t = useTranslations("dashboard.activityPage");
  const labels = useLabels();
  const grouped = useMemo(() => groupByTask(rows), [rows]);

  return (
    <div className="space-y-3">
      <GlassCard padding="none" className="overflow-hidden">
        <div className="overflow-x-auto">
          <div className="min-w-[640px]">
            <TimeAxis from={from} to={to} nowLabel={t("now")} />
            <ul className="divide-y divide-white/[0.04]">
              {grouped.map((g) => (
                <li key={g.taskId} className="flex items-stretch gap-3 px-3 py-1.5 hover:bg-white/[0.02]">
                  <div className="w-56 shrink-0 self-center overflow-hidden">
                    <Link
                      href={`/p/${projectId}/tasks/${g.taskId}`}
                      className="block truncate text-xs text-gray-200 hover:text-primary-400"
                      title={`${g.taskKey} · ${g.taskTitle}`}
                    >
                      <span className="font-mono text-xs text-gray-400">{g.taskKey}</span>{" "}
                      {g.taskTitle}
                    </Link>
                  </div>
                  <div className="relative h-7 flex-1">
                    {g.executions.map((e) => (
                      <ExecutionBar
                        key={e.executionId}
                        execution={e}
                        from={from}
                        to={to}
                        inProgressLabel={t("inProgress")}
                        onSelect={onSelectRun}
                      />
                    ))}
                  </div>
                </li>
              ))}
            </ul>
          </div>
        </div>
      </GlassCard>

      <div className="flex flex-wrap items-center gap-3 text-xs text-gray-400">
        <span>{t("legend")}:</span>
        {LANES.filter((l) => l.key !== "Drafting" && l.key !== "Done" && l.key !== "PlanReady").map((l) => (
          <span key={l.key} className="inline-flex items-center gap-1">
            <span className="inline-block h-2 w-3 rounded-sm" style={{ backgroundColor: l.color }} />
            {labels.lane(l.key)}
          </span>
        ))}
      </div>
    </div>
  );
}

function TimeAxis({ from, to, nowLabel }: { from: number; to: number; nowLabel: string }) {
  const span = to - from;
  const ticks = 6;
  const hourly = span <= 36 * 60 * 60 * 1000;
  const daily = span > 10 * 24 * 60 * 60 * 1000;
  return (
    <div className="flex items-end gap-3 border-b border-white/[0.06] px-3 pb-1 pt-2">
      <div className="w-56 shrink-0" />
      <div className="relative h-4 flex-1 text-xs text-gray-400">
        {Array.from({ length: ticks + 1 }, (_, i) => {
          const at = from + (span * i) / ticks;
          const d = new Date(at);
          const label =
            i === ticks
              ? nowLabel
              : hourly
                ? d.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" })
                : daily
                  ? d.toLocaleDateString(undefined, { month: "short", day: "numeric" })
                  : d.toLocaleDateString(undefined, { weekday: "short", hour: "2-digit" });
          return (
            <span
              key={i}
              className={cn("absolute -translate-x-1/2 whitespace-nowrap", i === ticks && "translate-x-[-100%]")}
              style={{ left: `${(i / ticks) * 100}%` }}
            >
              {label}
            </span>
          );
        })}
      </div>
    </div>
  );
}

function ExecutionBar({
  execution: e,
  from,
  to,
  inProgressLabel,
  onSelect,
}: {
  execution: ProjectExecutionRow;
  from: number;
  to: number;
  inProgressLabel: string;
  onSelect?: (row: ProjectExecutionRow) => void;
}) {
  const t = useTranslations("dashboard.activityPage");
  const labels = useLabels();
  const span = to - from;
  const start = Math.max(new Date(e.startedAt).getTime(), from);
  const end = e.completedAt ? Math.min(new Date(e.completedAt).getTime(), to) : to;
  const left = ((start - from) / span) * 100;
  const width = Math.max(((end - start) / span) * 100, 0.6);
  const lane = LANE_MAP[e.lane];
  const running = e.status === "Running";
  const failed = e.status === "Failed";
  const roleLabel = e.agentRole ? labels.agentRole(e.agentRole) : undefined;
  const statusKey = e.status?.toLowerCase() ?? "";
  const statusLabel = t.has(statusKey) ? t(statusKey) : e.status;
  const title = [
    `${labels.lane(e.lane)} · ${e.agentName ?? "—"}${roleLabel ? ` (${roleLabel})` : ""}`,
    running ? inProgressLabel : `${statusLabel} · ${formatDuration(e.durationSeconds)}`,
    hasCostEstimate(e.cost) ? formatUsdEstimate(e.cost) : null,
    e.errorMessage ? `⚠ ${e.errorMessage}` : null,
  ]
    .filter(Boolean)
    .join("\n");

  const style = {
    left: `${left}%`,
    width: `${width}%`,
    backgroundColor: lane?.color ?? "var(--color-lane-developing)",
    opacity: e.status === "Cancelled" ? 0.4 : 0.9,
  };
  const className = cn(
    "absolute top-1.5 h-4 rounded-sm border",
    running && "animate-pulse",
    failed ? "border-red-400/70" : "border-transparent",
    onSelect && "cursor-pointer hover:brightness-125"
  );

  if (!onSelect) {
    return (
      <div
        className={className}
        style={style}
        title={title}
        data-testid="execution-bar"
        data-status={e.status}
      />
    );
  }

  return (
    <button
      type="button"
      className={className}
      style={style}
      title={title}
      onClick={() => onSelect(e)}
      data-testid="execution-bar"
      data-status={e.status}
    />
  );
}

export { ExecutionTimeline };
export type { ExecutionTimelineProps };
