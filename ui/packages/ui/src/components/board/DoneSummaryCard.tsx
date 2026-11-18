import { useTranslations } from "use-intl";
import { Stat, toneForSuccessRate } from "@/components/ui";
import { formatDuration } from "@/components/team/AgentOpsStats";
import { formatUsdEstimate, formatTokenCount } from "@/lib/utils";
import { SummaryCard } from "./LaneSummaryCard";
import type { BoardMetricsResponse } from "@/lib/types";

interface DoneSummaryCardProps {
  metrics: BoardMetricsResponse | null;
}

/**
 * What landed, pinned to the top of the terminal column.
 *
 * Deliberately the same four figures as the strip above the board, scoped to the outcome the
 * column represents. Someone reading down the Done lane is asking "was this week any good?", and
 * the answer should be where they are looking rather than back at the top of the page.
 */
function DoneSummaryCard({ metrics }: DoneSummaryCardProps) {
  const t = useTranslations("dashboard.boardMetrics");

  // Nothing shipped in the window is not worth a card claiming otherwise — the lane's own cards
  // (or its emptiness) already say it.
  if (!metrics || metrics.current.shipped === 0) return null;

  const { current } = metrics;
  const costless = current.agentSpend === 0 && current.tokens > 0;
  const failurePct = current.failureRate === null ? null : current.failureRate * 100;
  const successPct = failurePct === null ? null : 100 - failurePct;

  return (
    <SummaryCard title={t("doneSummaryTitle")} testId="done-summary">
      <Stat size="sm" label={t("shipped")} value={current.shipped} />
      <Stat
        size="sm"
        label={t("agentTimePerTask")}
        value={formatDuration(current.agentSecondsPerShippedTask)}
      />
      <Stat
        size="sm"
        label={costless ? t("tokens") : t("agentSpend")}
        value={
          costless
            ? formatTokenCount(current.tokens)
            : formatUsdEstimate(current.agentSpend)
        }
      />
      <Stat
        size="sm"
        label={t("failureRate")}
        value={failurePct === null ? "—" : `${Math.round(failurePct)}%`}
        tone={toneForSuccessRate(successPct)}
      />
    </SummaryCard>
  );
}

export { DoneSummaryCard };
export type { DoneSummaryCardProps };
