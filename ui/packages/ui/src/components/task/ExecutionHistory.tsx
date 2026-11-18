import { useState } from "react";
import { useTranslations } from "use-intl";
import { Link } from "@/i18n/navigation";
import { cn } from "@/lib/utils";
import { GlassCard, Badge } from "@/components/ui";
import { LANE_MAP } from "@/lib/constants";
import { useLabels } from "@/hooks/use-labels";
import { formatRelativeTime } from "@/lib/utils";
import type { TaskExecution } from "@/lib/types";

interface ExecutionHistoryProps {
  executions: TaskExecution[];
  /** Enables linking the agent to the Team page */
  projectId?: string;
}

const STATUS_STYLES: Record<string, { variant: "default" | "success" | "error" | "warning"; labelKey: string }> = {
  queued: { variant: "default", labelKey: "status.queued" },
  running: { variant: "warning", labelKey: "status.running" },
  completed: { variant: "success", labelKey: "status.completed" },
  failed: { variant: "error", labelKey: "status.failed" },
  cancelled: { variant: "default", labelKey: "status.cancelled" },
};

function formatDuration(start: string | null, end: string | null): string {
  if (!start) return "—";
  const s = new Date(start).getTime();
  const e = end ? new Date(end).getTime() : Date.now();
  const seconds = Math.floor((e - s) / 1000);
  if (seconds < 60) return `${seconds}s`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ${seconds % 60}s`;
  return `${Math.floor(seconds / 3600)}h ${Math.floor((seconds % 3600) / 60)}m`;
}

function formatCost(cost: number): string {
  if (cost === 0) return "—";
  return `$${cost.toFixed(4)}`;
}

function formatTokens(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (n >= 10_000) return `${Math.round(n / 1000)}k`;
  return n.toLocaleString();
}

/**
 * The number shown beside the cost. Prefers the billed total — prompt+completion excludes the
 * cached prefix, which on a long run is the overwhelming majority of both the tokens and the
 * bill, so showing it next to a dollar figure makes a correct charge look impossible.
 */
function billedTotal(exec: TaskExecution): number {
  return exec.billed?.totalTokens ?? exec.tokensUsed;
}

function TokenStat({
  label,
  value,
  total,
}: {
  label: string;
  value: number;
  /** When given, adds the share of the run's billed tokens this bucket accounts for. */
  total?: number;
}) {
  const share = total && total > 0 ? Math.round((value / total) * 100) : null;
  return (
    <div>
      <div className="text-xs text-gray-400">{label}</div>
      <div className="text-xs text-gray-300">
        {value.toLocaleString()}
        {share !== null && share > 0 && (
          <span className="ml-1 text-gray-400">({share}%)</span>
        )}
      </div>
    </div>
  );
}

function ExecutionHistory({ executions, projectId }: ExecutionHistoryProps) {
  const t = useTranslations("dashboard.executionHistory");
  const labels = useLabels();
  const [expandedId, setExpandedId] = useState<string | null>(null);

  if (executions.length === 0) return null;

  return (
    <GlassCard padding="md">
      <h4 className="text-label mb-3">
        {t("title", { count: executions.length })}
      </h4>
      <div className="space-y-2">
        {executions.map((exec) => {
          const lane = LANE_MAP[exec.lane as keyof typeof LANE_MAP];
          const statusStyle =
            STATUS_STYLES[exec.status?.toLowerCase() ?? ""] ?? STATUS_STYLES.queued;
          const isExpanded = expandedId === exec.id;

          return (
            <div key={exec.id}>
              <div
                role="button"
                tabIndex={0}
                onClick={() => setExpandedId(isExpanded ? null : exec.id)}
                onKeyDown={(e) => {
                  if (e.key === "Enter" || e.key === " ") {
                    e.preventDefault();
                    setExpandedId(isExpanded ? null : exec.id);
                  }
                }}
                className="flex w-full cursor-pointer items-center gap-2 rounded-lg px-3 py-2 text-left text-xs transition-colors hover:bg-white/[0.03]"
              >
                <Badge variant={statusStyle.variant} size="sm">
                  {t(statusStyle.labelKey)}
                </Badge>
                {lane && (
                  <span
                    className="rounded px-1.5 py-0.5 text-xs font-medium"
                    style={{
                      backgroundColor: `${lane.color}15`,
                      color: lane.color,
                    }}
                  >
                    {labels.lane(exec.lane)}
                  </span>
                )}
                {exec.agentName &&
                  (projectId ? (
                    <Link
                      href={`/p/${projectId}/team`}
                      onClick={(e) => e.stopPropagation()}
                      title={t("chatWithAgent", { agent: exec.agentName })}
                      className="flex items-center gap-1 rounded-full bg-white/[0.04] py-0.5 pl-0.5 pr-2 text-xs text-gray-300 hover:bg-white/[0.08] hover:text-white"
                    >
                      <span className="flex h-4 w-4 items-center justify-center rounded-full bg-primary-800/60 text-xs font-semibold text-primary-300">
                        {exec.agentName.charAt(0).toUpperCase()}
                      </span>
                      {exec.agentName}
                    </Link>
                  ) : (
                    <span className="flex items-center gap-1 rounded-full bg-white/[0.04] py-0.5 pl-0.5 pr-2 text-xs text-gray-300">
                      <span className="flex h-4 w-4 items-center justify-center rounded-full bg-primary-800/60 text-xs font-semibold text-primary-300">
                        {exec.agentName.charAt(0).toUpperCase()}
                      </span>
                      {exec.agentName}
                    </span>
                  ))}
                <span className="ml-auto text-gray-400">
                  {formatDuration(exec.startedAt, exec.completedAt)}
                </span>
                <span
                  className="text-gray-400"
                  title={exec.billed ? t("billedTooltip") : undefined}
                >
                  {formatTokens(billedTotal(exec))} {t("tokenUnit")}
                </span>
                <span className="text-gray-400">
                  {formatCost(exec.cost)}
                </span>
                <svg
                  width="12"
                  height="12"
                  viewBox="0 0 16 16"
                  fill="none"
                  className={cn(
                    "shrink-0 text-gray-400 transition-transform",
                    isExpanded && "rotate-180"
                  )}
                >
                  <path d="M4 6l4 4 4-4" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
                </svg>
              </div>

              {isExpanded && (
                <div className="ml-3 mt-1 mb-2 space-y-2 rounded-lg bg-white/[0.02] p-3">
                  {exec.errorMessage && (
                    <div className="rounded-lg bg-red-500/[0.05] border border-red-500/10 p-2 text-xs text-red-400">
                      {exec.errorMessage}
                    </div>
                  )}
                  {(exec.filesChanged?.length ?? 0) > 0 && (
                    <div>
                      <span className="text-label">
                        {t("filesTitle", { count: exec.filesChanged!.length })}
                      </span>
                      <div className="mt-1 space-y-0.5">
                        {exec.filesChanged!.map((f, i) => (
                          <div key={i} className="flex items-center gap-2 text-xs">
                            <span
                              className={cn(
                                "shrink-0 rounded px-1 py-0.5 text-xs font-bold",
                                f.action === "create" && "bg-emerald-500/10 text-emerald-400",
                                f.action === "modify" && "bg-amber-500/10 text-amber-400",
                                f.action === "delete" && "bg-red-500/10 text-red-400",
                              )}
                            >
                              {f.action.charAt(0).toUpperCase()}
                            </span>
                            <span className="font-mono text-gray-400 truncate">
                              {f.path}
                            </span>
                          </div>
                        ))}
                      </div>
                    </div>
                  )}
                  {exec.billed ? (
                    <div>
                      <span className="text-label">{t("billedTokens")}</span>
                      <div className="mt-1 grid grid-cols-2 gap-x-4 gap-y-1 sm:grid-cols-4">
                        <TokenStat label={t("input")} value={exec.billed.inputTokens} />
                        <TokenStat label={t("output")} value={exec.billed.outputTokens} />
                        <TokenStat
                          label={t("cacheRead")}
                          value={exec.billed.cacheReadTokens}
                          total={exec.billed.totalTokens}
                        />
                        <TokenStat
                          label={t("cacheWrite")}
                          value={exec.billed.cacheWriteTokens}
                          total={exec.billed.totalTokens}
                        />
                      </div>
                      <p className="mt-2 text-xs text-gray-400">
                        {t("billedExplainer", { total: exec.billed.totalTokens.toLocaleString() })}
                      </p>
                    </div>
                  ) : (
                    <div className="flex gap-4 text-xs text-gray-400">
                      <span>{t("promptLabel")} {exec.promptTokens.toLocaleString()}</span>
                      <span>{t("completionLabel")} {exec.completionTokens.toLocaleString()}</span>
                    </div>
                  )}
                  {exec.startedAt && (
                    <div className="text-xs text-gray-400">
                      {t("started", { time: formatRelativeTime(exec.startedAt) })}
                    </div>
                  )}
                </div>
              )}
            </div>
          );
        })}
      </div>
    </GlassCard>
  );
}

export { ExecutionHistory };
