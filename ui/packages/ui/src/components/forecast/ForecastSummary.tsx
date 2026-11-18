import { useTranslations } from "use-intl";
import { cn, formatUsd, formatUsdEstimate } from "@/lib/utils";
import type { TaskForecast, MilestoneForecast } from "@/lib/types";

/** Rounds to a unit a human reads at a glance — nobody needs "1847 seconds". */
function formatAgentTime(seconds: number): string {
  if (seconds < 90) return `${Math.max(seconds, 1)}s`;
  const minutes = Math.round(seconds / 60);
  if (minutes < 90) return `${minutes} min`;
  const hours = seconds / 3600;
  return `${hours < 10 ? hours.toFixed(1) : Math.round(hours)}h`;
}

/**
 * A task's forecast as an estimated list-price cost in USD (never a charge — runs use the
 * developer's own subscription), with the median first and the p25–p75 band beside it. A
 * range, never a bare point, and time labelled as AGENT time.
 *
 * The label is not decoration. Wall-clock-to-done is dominated by how fast the human replies —
 * something Orkka has no business predicting — so every rendering of this number says whose
 * time it is.
 */
export function TaskForecastSummary({
  forecast,
  className,
}: {
  forecast: TaskForecast | null;
  className?: string;
}) {
  const t = useTranslations("dashboard");

  // Below the sample floor the API returns nothing at all. With the platform-wide fallback this
  // is now rare (a fresh deployment), but it stays a deliberate empty state rather than a guess.
  if (!forecast) {
    return (
      <p className={cn("font-mono text-xs text-gray-400", className)}>
        {t("forecast.notEnoughHistory")}
      </p>
    );
  }

  const time = t("forecast.agentTimeRange", {
    low: formatAgentTime(forecast.agentSecondsP25),
    high: formatAgentTime(forecast.agentSecondsP75),
  });

  // Null cost = the task runs on the developer's own subscription with no list-price estimate —
  // said out loud rather than omitted, or "no cost shown" reads as "cost unknown".
  const hasCost =
    forecast.costMedian !== null && forecast.costP25 !== null && forecast.costP75 !== null;

  return (
    <div className={cn("font-mono text-xs text-gray-400", className)}>
      <span className="text-gray-400">{t("forecast.estimateLabel")}: </span>
      {hasCost ? (
        <span className="text-gray-200">
          {t("forecast.listPriceRange", {
            median: formatUsd(forecast.costMedian!),
            low: formatUsd(forecast.costP25!),
            high: formatUsd(forecast.costP75!),
          })}
        </span>
      ) : (
        <span className="text-gray-300">{t("forecast.runnerNoCost")}</span>
      )}
      <span className="text-gray-400"> · </span>
      <span>{time}</span>
      {/* Says so when the numbers come from other projects on the platform, not this one — a
          new org's first estimate is a fair guess, and it should read as one. */}
      {forecast.bucketLevel === "platform" && (
        <span className="block text-xs text-gray-400">{t("forecast.platformBasis")}</span>
      )}
    </div>
  );
}

/**
 * A milestone's remaining workload — how much machine work is left, never when it lands.
 *
 * Tasks queue and human gates sit between them, so a sum of agent time is not a delivery date.
 * Same reasoning that kept target dates off milestones entirely: a number users read as a
 * delivery date IS a delivery date, whatever the field is called.
 */
export function MilestoneForecastSummary({
  forecast,
  className,
}: {
  forecast: MilestoneForecast | null;
  className?: string;
}) {
  const t = useTranslations("dashboard");

  if (!forecast || forecast.tasksCovered === 0) return null;

  const time = formatAgentTime(forecast.agentSecondsMedian);

  const label =
    forecast.costMedian !== null
      ? t("forecast.milestoneWorkload", {
          cost: t("forecast.listPrice", {
            amount: formatUsdEstimate(forecast.costMedian),
          }),
          time,
        })
      : t("forecast.milestoneWorkloadTimeOnly", { time });

  return (
    <p className={cn("text-xs text-gray-400", className)}>
      {label}
      {/* Says so out loud when some tasks lacked the history to forecast — a silently
          partial total reads as a complete one. */}
      {forecast.tasksCovered < forecast.tasksOpen && (
        <span className="text-gray-400">
          {" "}
          ({t("forecast.partialCoverage", {
            covered: forecast.tasksCovered,
            open: forecast.tasksOpen,
          })})
        </span>
      )}
    </p>
  );
}
