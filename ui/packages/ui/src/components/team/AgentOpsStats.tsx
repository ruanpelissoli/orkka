import { useTranslations } from "use-intl";
import { Stat, toneForSuccessRate } from "@/components/ui/Stat";
import { formatRelativeTime } from "@/lib/utils";
import type { AgentStats } from "@/lib/types";

interface AgentOpsStatsProps {
  stats: AgentStats | undefined;
  /** True while the first fetch for the current window is in flight. */
  isLoading?: boolean;
}

/**
 * The "how has this agent been doing lately" strip on an agent card: runs, success rate,
 * average run duration and the most recent error. Complements the lifetime tokens/cost
 * counters (which stay where they are) — those answer "what did it cost", this answers
 * "is it healthy".
 */
function AgentOpsStats({ stats, isLoading }: AgentOpsStatsProps) {
  const t = useTranslations("dashboard.teamStats");

  if (!stats || stats.runs === 0) {
    return (
      <p className="mt-2 text-xs text-gray-400">
        {isLoading ? "…" : t("noRuns")}
      </p>
    );
  }

  const finished = stats.succeeded + stats.failed;
  const successPct = finished === 0 ? null : Math.round((stats.succeeded / finished) * 100);

  return (
    <div className="mt-2 space-y-1.5" data-testid="agent-ops-stats">
      <div className="grid grid-cols-3 gap-2">
        <Stat
          size="sm"
          label={t("runs")}
          value={
            <>
              {stats.runs}
              {stats.running > 0 && (
                <span className="ml-1 text-primary-400" title={t("running", { count: stats.running })}>
                  ●
                </span>
              )}
            </>
          }
        />
        <Stat
          size="sm"
          label={t("successRate")}
          value={successPct === null ? "—" : `${successPct}%`}
          tone={toneForSuccessRate(successPct)}
        />
        <Stat size="sm" label={t("avgDuration")} value={formatDuration(stats.avgDurationSeconds)} />
      </div>
      {stats.lastError && (
        <p
          className="truncate text-xs text-red-400/90"
          title={stats.lastError}
        >
          <span className="text-gray-400">{t("lastError")}:</span> {stats.lastError}
          {stats.lastErrorAt && (
            <span className="text-gray-400"> · {formatRelativeTime(stats.lastErrorAt)}</span>
          )}
        </p>
      )}
    </div>
  );
}

/** "42s", "3m 10s", "1h 05m" — compact enough for a card cell. */
function formatDuration(seconds: number | null): string {
  if (seconds === null || !Number.isFinite(seconds)) return "—";
  const s = Math.round(seconds);
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ${String(s % 60).padStart(2, "0")}s`;
  const h = Math.floor(m / 60);
  return `${h}h ${String(m % 60).padStart(2, "0")}m`;
}

export { AgentOpsStats, formatDuration };
