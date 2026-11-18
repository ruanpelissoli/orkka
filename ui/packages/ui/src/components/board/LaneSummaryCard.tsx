import type { ReactNode } from "react";
import { useTranslations } from "use-intl";
import { Stat, toneForSuccessRate } from "@/components/ui";
import { formatDuration } from "@/components/team/AgentOpsStats";
import { formatTokenCount } from "@/lib/utils";
import type { BoardLaneMetric } from "@/lib/types";

/**
 * The shell every column summary shares: a label and a 2×2 of small stats, sized to sit above the
 * cards rather than compete with them. Extracted so the Done card and the per-lane cards cannot
 * drift into two slightly different boxes.
 */
function SummaryCard({
  title,
  testId,
  children,
}: {
  title: string;
  testId: string;
  children: ReactNode;
}) {
  return (
    <div
      className="rounded-xl border border-white/[0.06] bg-white/[0.03] p-3"
      data-testid={testId}
    >
      <p className="text-label">{title}</p>
      <div className="mt-2 grid grid-cols-2 gap-3">{children}</div>
    </div>
  );
}

interface LaneSummaryCardProps {
  /** Absent for any lane no agent runs in — those columns get no card at all. */
  metric?: BoardLaneMetric;
}

/**
 * What one column cost over the metrics window: agent time, tokens, and how often its runs fail.
 *
 * The lane header already carries the average per task; this carries the totals, so the two are
 * complementary rather than the same number printed twice. Every duration is agent time — the sum
 * of execution spans — and never how long cards sat here, which is not a number this platform has.
 */
function LaneSummaryCard({ metric }: LaneSummaryCardProps) {
  const t = useTranslations("dashboard.boardMetrics");

  // A lane whose runs are all still in flight has nothing to total yet. An empty card claiming
  // "0s, 0 tokens, 0%" reads as a broken widget, so there is simply no card.
  if (!metric || (metric.finishedRuns === 0 && metric.tokens === 0)) return null;

  const failurePct = metric.failureRate === null ? null : metric.failureRate * 100;
  const successPct = failurePct === null ? null : 100 - failurePct;

  return (
    <SummaryCard title={t("laneSummaryTitle")} testId="lane-summary">
      <Stat
        size="sm"
        label={t("laneAgentTime")}
        // Null rather than zero seconds: "0s" would read as instant, not as nothing measured yet.
        value={formatDuration(metric.tasks === 0 ? null : metric.totalAgentSeconds)}
      />
      <Stat size="sm" label={t("tokens")} value={formatTokenCount(metric.tokens)} />
      <Stat
        size="sm"
        label={t("failureRate")}
        value={failurePct === null ? "—" : `${Math.round(failurePct)}%`}
        tone={toneForSuccessRate(successPct)}
      />
      <Stat size="sm" label={t("laneRuns")} value={metric.finishedRuns} />
    </SummaryCard>
  );
}

export { LaneSummaryCard, SummaryCard };
export type { LaneSummaryCardProps };
