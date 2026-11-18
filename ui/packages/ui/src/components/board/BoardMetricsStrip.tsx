import { useTranslations } from "use-intl";
import { Badge, GlassCard, Stat, StatStrip, StatStripCell, toneForSuccessRate } from "@/components/ui";
import { Term } from "@/components/ui/Term";
import { formatDuration } from "@/components/team/AgentOpsStats";
import { formatUsdEstimate, formatTokenCount } from "@/lib/utils";
import type { BoardMetricsResponse, MetricDelta, MetricTrend } from "@/lib/types";

interface BoardMetricsStripProps {
  metrics: BoardMetricsResponse | null;
  isLoading?: boolean;
}

/** Lane palette, reused so a metric looks like the part of the pipeline it describes. */
const ACCENT = {
  shipped: "var(--color-lane-done)",
  spend: "var(--color-accent-violet)",
  time: "var(--color-lane-developing)",
  reliability: "var(--color-lane-review)",
} as const;

/**
 * The four numbers above the board: what shipped, what it cost, how much agent time each shipped
 * task took, and how often runs fail.
 *
 * Every duration here is agent time. It is not how long work sat in a column — there is no
 * lane-transition history, so that number does not exist, and presenting one would mostly measure
 * how fast the human replied.
 */
function BoardMetricsStrip({ metrics, isLoading }: BoardMetricsStripProps) {
  const t = useTranslations("dashboard.boardMetrics");

  if (!metrics) {
    return isLoading ? <StripSkeleton /> : null;
  }

  const { current, deltas } = metrics;

  // A project with no runs in the window has nothing to average. Four dashes read as a broken
  // widget, so say the true thing instead: nothing has happened yet.
  if (current.runs === 0 && current.shipped === 0) {
    return (
      <GlassCard padding="none" className="mx-4 mb-3 lg:mx-6">
        <p className="px-4 py-3 text-xs text-gray-400">{t("empty")}</p>
      </GlassCard>
    );
  }

  // Cost is structurally zero for any org on a subscription and for runner runs, which go
  // through the user's own plan. Showing "$0.00" there reads as a bug; tokens are the only
  // signal with content, so the cell becomes a volume cell.
  const costless = current.agentSpend === 0 && current.tokens > 0;

  const failurePct = current.failureRate === null ? null : current.failureRate * 100;
  const successPct = failurePct === null ? null : 100 - failurePct;

  return (
    <GlassCard padding="none" className="mx-4 mb-3 overflow-hidden lg:mx-6">
      <StatStrip>
        <StatStripCell>
          <Stat
            accentColor={ACCENT.shipped}
            label={t("shipped")}
            value={current.shipped}
            hint={t("shippedHint", { tasks: current.tasksTouched })}
            badge={<TrendPill delta={deltas.shipped} />}
          />
        </StatStripCell>

        <StatStripCell>
          {costless ? (
            <Stat
              accentColor={ACCENT.spend}
              label={t("tokens")}
              value={formatTokenCount(current.tokens)}
              hint={t("tokensHint")}
            />
          ) : (
            <Stat
              accentColor={ACCENT.spend}
              label={t("agentSpend")}
              value={formatUsdEstimate(current.agentSpend)}
              // The number that changes behaviour: what was spent on runs that produced nothing.
              hint={
                current.wastedSpend > 0
                  ? t("wastedHint", { amount: formatUsdEstimate(current.wastedSpend) })
                  : t("noWaste")
              }
              badge={<TrendPill delta={deltas.wastedSpend} label={t("wasteTrend")} />}
            />
          )}
        </StatStripCell>

        <StatStripCell>
          <Stat
            accentColor={ACCENT.time}
            label={t("agentTimePerTask")}
            value={formatDuration(current.agentSecondsPerShippedTask)}
            hint={<Term k="agentTime">{t("agentTimeHint")}</Term>}
            badge={<TrendPill delta={deltas.agentSecondsPerShippedTask} />}
          />
        </StatStripCell>

        <StatStripCell>
          <Stat
            accentColor={ACCENT.reliability}
            label={t("failureRate")}
            value={failurePct === null ? "—" : `${Math.round(failurePct)}%`}
            tone={toneForSuccessRate(successPct)}
            hint={t("failureHint", { runs: current.runs })}
            badge={<TrendPill delta={deltas.failureRate} />}
          />
        </StatStripCell>
      </StatStrip>
    </GlassCard>
  );
}

const TREND_VARIANT: Record<MetricTrend, "success" | "error" | "default"> = {
  Improving: "success",
  Worsening: "error",
  Flat: "default",
  Unknown: "default",
};

/**
 * The direction pill. The verdict comes from the API, which is what knows a falling failure rate
 * is good and a falling ship count is not — the client only picks a colour.
 *
 * An Unknown trend renders nothing rather than a grey pill: a young project should look young,
 * not look like something failed to load.
 */
function TrendPill({ delta, label }: { delta: MetricDelta; label?: string }) {
  if (delta.trend === "Unknown" || delta.changePct === null) return null;
  const sign = delta.changePct > 0 ? "+" : "";
  return (
    <Badge size="sm" variant={TREND_VARIANT[delta.trend]}>
      {label ? `${label} ` : ""}
      {sign}
      {Math.round(delta.changePct)}%
    </Badge>
  );
}

function StripSkeleton() {
  return (
    <GlassCard padding="none" className="mx-4 mb-3 overflow-hidden lg:mx-6">
      <StatStrip>
        {[0, 1, 2, 3].map((i) => (
          <StatStripCell key={i}>
            <div className="h-3 w-20 rounded bg-white/[0.06]" />
            <div className="mt-2 h-6 w-14 rounded bg-white/[0.06]" />
          </StatStripCell>
        ))}
      </StatStrip>
    </GlassCard>
  );
}

export { BoardMetricsStrip };
export type { BoardMetricsStripProps };
