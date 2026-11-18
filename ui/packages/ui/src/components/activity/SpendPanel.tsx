import { useTranslations } from "use-intl";
import { Link } from "@/i18n/navigation";
import { GlassCard } from "@/components/ui";
import { useLabels } from "@/hooks/use-labels";
import { cn, hasCostEstimate, formatUsdEstimate, formatTokenCount } from "@/lib/utils";
import { formatDuration } from "@/components/team/AgentOpsStats";
import type { AgentRollup, TaskSpend } from "./activity-model";

interface SpendPanelProps {
  agents: AgentRollup[];
  topTasks: TaskSpend[];
  projectId: string;
  /**
   * The window produced tokens but no cost — true for runner runs and for orgs on subscription
   * auth. Derived from the data, so every such case gets the same column rather than a wall
   * of $0.00.
   */
  costless: boolean;
}

/**
 * Zone three: where the window's effort went, per agent and per task.
 *
 * Spend is an estimate at list price in US dollars — runs use the developer's own subscription,
 * so Orkka charges nothing and the figure is only ever "≈ $x".
 *
 * Spend, not tokens, is the headline — `ProjectExecutionRow.tokensUsed` counts prompt and completion
 * only, and on agentic runs cache reads dwarf both, so a token column beside a spend figure shows a
 * low single-digit percentage of the real volume. The token numbers here come from the server
 * summary, which reads the usage rows with the cache split intact.
 *
 * Where every run in the window has no estimate — the usual case on a subscription —
 * the cost column gives way to tokens rather than printing a wall of zeros and implying
 * the runs were free of everything.
 */
function SpendPanel({ agents, topTasks, projectId, costless }: SpendPanelProps) {
  const t = useTranslations("dashboard.activityPage");
  const labels = useLabels();

  if (agents.length === 0) {
    return (
      <GlassCard className="text-center">
        <p className="text-sm text-gray-400">{t("empty")}</p>
      </GlassCard>
    );
  }

  return (
    <div className="grid gap-4 lg:grid-cols-[3fr_2fr]">
      <GlassCard padding="none" className="overflow-hidden">
        <div className="overflow-x-auto">
          <table className="w-full min-w-[480px] text-left text-xs" data-testid="spend-by-agent">
            <thead className="border-b border-white/[0.06] text-gray-400">
              <tr>
                <th scope="col" className="px-4 py-2 font-medium">{t("agent")}</th>
                <th scope="col" className="px-3 py-2 text-right font-medium">{t("runs")}</th>
                <th scope="col" className="px-3 py-2 text-right font-medium">{t("successRate")}</th>
                <th scope="col" className="px-3 py-2 text-right font-medium">{t("avgDuration")}</th>
                <th scope="col" className="px-4 py-2 text-right font-medium">
                  {costless ? t("tokens") : t("spend")}
                </th>
              </tr>
            </thead>
            <tbody className="divide-y divide-white/[0.04]">
              {agents.map((agent) => (
                <tr key={agent.agentId ?? "unassigned"} className="hover:bg-white/[0.02]">
                  <td className="px-4 py-2">
                    <span className="text-gray-200">{agent.agentName ?? t("unassigned")}</span>
                    {agent.agentRole && (
                      <span className="ml-1.5 text-gray-400">
                        {labels.agentRole(agent.agentRole)}
                      </span>
                    )}
                  </td>
                  <td className="px-3 py-2 text-right text-gray-300">
                    {agent.runs}
                    {agent.running > 0 && <span className="ml-1 text-primary-400">●</span>}
                  </td>
                  <td className={cn("px-3 py-2 text-right", successClass(agent.successRate))}>
                    {agent.successRate === null ? "—" : `${Math.round(agent.successRate * 100)}%`}
                  </td>
                  <td className="px-3 py-2 text-right text-gray-300">
                    {formatDuration(agent.avgDurationSeconds)}
                  </td>
                  <td className="px-4 py-2 text-right">
                    {costless ? (
                      <span className="text-gray-300">{formatTokenCount(agent.tokens)}</span>
                    ) : (
                      <>
                        <span className="text-gray-200">{formatUsdEstimate(agent.cost)}</span>
                        {hasCostEstimate(agent.wastedCost) && (
                          <span className="ml-1.5 text-red-400/90" title={t("wastedHint")}>
                            −{formatUsdEstimate(agent.wastedCost)}
                          </span>
                        )}
                      </>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </GlassCard>

      <GlassCard padding="none" className="overflow-hidden">
        <p className="border-b border-white/[0.06] px-4 py-2 text-xs font-medium text-gray-400">
          {t("costliestTasks")}
        </p>
        {topTasks.length === 0 ? (
          <p className="px-4 py-6 text-center text-xs text-gray-400">{t("noSpend")}</p>
        ) : (
          <ul className="divide-y divide-white/[0.04]" data-testid="costliest-tasks">
            {topTasks.map((task) => (
              <li key={task.taskId} className="flex items-center gap-3 px-4 py-2 hover:bg-white/[0.02]">
                <Link
                  href={`/p/${projectId}/tasks/${task.taskId}`}
                  className="min-w-0 flex-1 truncate text-xs text-gray-200 hover:text-primary-400"
                  title={`${task.taskKey} · ${task.taskTitle}`}
                >
                  <span className="font-mono text-gray-400">{task.taskKey}</span> {task.taskTitle}
                </Link>
                <span className="shrink-0 text-xs text-gray-400">
                  {t("summaryRuns", { count: task.runs })}
                </span>
                <span className="shrink-0 text-xs text-gray-200">{formatUsdEstimate(task.cost)}</span>
              </li>
            ))}
          </ul>
        )}
      </GlassCard>
    </div>
  );
}

function successClass(rate: number | null): string {
  if (rate === null) return "text-gray-400";
  if (rate >= 0.9) return "text-emerald-400";
  if (rate >= 0.6) return "text-amber-400";
  return "text-red-400";
}

export { SpendPanel };
export type { SpendPanelProps };
