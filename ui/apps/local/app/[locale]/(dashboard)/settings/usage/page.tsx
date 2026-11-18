import { Suspense } from "react";
import { useSearchParams } from "@/lib/navigation";
import { useTranslations } from "use-intl";
import { Link } from "@/i18n/navigation";
import { DashboardSkeleton } from "@/components/page";
import { useApiQuery } from "@/hooks/use-api-query";
import { useDocumentTitle } from "@/hooks/use-document-title";
import { api } from "@/lib/api";
import { GlassCard, Stat, StatStrip, StatStripCell } from "@/components/ui";
import { cn, formatTokenCount, formatUsdEstimate, hasCostEstimate } from "@/lib/utils";
import type { DailyUsageItem, UsageBreakdownItem, UsageGroupBy, UsageSummary } from "@/lib/types";

const WINDOWS = [7, 30, 90] as const;
type WindowDays = (typeof WINDOWS)[number];
const GROUPS = ["project", "agent", "model"] as const satisfies readonly UsageGroupBy[];
type Group = (typeof GROUPS)[number];

const EMPTY_SUMMARY: UsageSummary = {
  totalCostUsd: 0,
  totalInputTokens: 0,
  totalOutputTokens: 0,
  totalCacheReadTokens: 0,
  totalCacheWriteTokens: 0,
  totalTokens: 0,
  taskCount: 0,
};

function pickWindow(raw: string | undefined): WindowDays {
  const n = Number(raw);
  return (WINDOWS as readonly number[]).includes(n) ? (n as WindowDays) : 30;
}

function pickGroup(raw: string | undefined): Group {
  return (GROUPS as readonly string[]).includes(raw ?? "") ? (raw as Group) : "project";
}

/**
 * Settings → Usage: the tokens this workspace's agents used, by day and by project, agent or
 * model. Agents run on the developer's own AI subscription, so Orkka bills nothing — the
 * dollar figure is only an estimate at list price, shown when there is one.
 *
 * Window and grouping live in the URL (`?days=&groupBy=`).
 */
export default function UsageSettingsPage() {
  // useSearchParams needs a Suspense boundary on a statically rendered route.
  return (
    <Suspense fallback={<DashboardSkeleton />}>
      <UsageSettings />
    </Suspense>
  );
}

function UsageSettings() {
  const sp = useSearchParams();
  const days = pickWindow(sp.get("days") ?? undefined);
  const groupBy = pickGroup(sp.get("groupBy") ?? undefined);
  const tMeta = useTranslations("dashboard");
  const t = useTranslations("dashboard.usagePage");
  useDocumentTitle(tMeta("meta.usage"));

  const { data } = useApiQuery(async () => {
    const to = new Date();
    const from = new Date(to.getTime() - days * 24 * 60 * 60 * 1000);
    const fromIso = from.toISOString();
    const toIso = to.toISOString();

    const [summary, daily, breakdown] = await Promise.all([
      api.usage.summary(fromIso, toIso, "").catch(() => EMPTY_SUMMARY),
      api.usage
        .daily(fromIso, toIso, "")
        .then((r) => r.items)
        .catch((): DailyUsageItem[] => []),
      api.usage
        .breakdown(groupBy, fromIso, toIso, "")
        .then((r) => r.items)
        .catch((): UsageBreakdownItem[] => []),
    ]);
    return { summary, daily, breakdown };
  }, [days, groupBy]);

  if (!data) return <DashboardSkeleton />;

  const { summary, daily, breakdown } = data;
  const peak = Math.max(1, ...daily.map((d) => d.totalTokens));
  const showCost = hasCostEstimate(summary.totalCostUsd);
  const href = (next: { days?: WindowDays; groupBy?: Group }) =>
    `/settings/usage?days=${next.days ?? days}&groupBy=${next.groupBy ?? groupBy}`;

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="text-sm text-gray-400">{t("subtitle")}</p>
        <div className="flex gap-1.5" role="group" aria-label={t("window")}>
          {WINDOWS.map((d) => (
            <Pill key={d} href={href({ days: d })} active={d === days}>
              {t("windowDays", { days: d })}
            </Pill>
          ))}
        </div>
      </div>

      <GlassCard padding="none">
        <StatStrip>
          <StatStripCell>
            <Stat label={t("tokens")} value={formatTokenCount(summary.totalTokens)} />
          </StatStripCell>
          <StatStripCell>
            <Stat
              label={t("cacheTokens")}
              value={formatTokenCount(summary.totalCacheReadTokens + summary.totalCacheWriteTokens)}
            />
          </StatStripCell>
          <StatStripCell>
            <Stat label={t("tasks")} value={summary.taskCount} />
          </StatStripCell>
          <StatStripCell>
            <Stat
              label={t("estimatedCost")}
              value={showCost ? formatUsdEstimate(summary.totalCostUsd) : "—"}
              hint={showCost ? t("listPriceHint") : t("noCostHint")}
            />
          </StatStripCell>
        </StatStrip>
      </GlassCard>

      <GlassCard>
        <h2 className="text-label mb-3">{t("byDay")}</h2>
        {daily.length === 0 ? (
          <p className="text-sm text-gray-400">{t("empty")}</p>
        ) : (
          <ul className="space-y-1.5" data-testid="usage-daily">
            {daily.map((d) => (
              <li key={d.date} className="flex items-center gap-3 text-xs">
                <span className="w-20 shrink-0 font-mono text-gray-400">{d.date}</span>
                <span className="h-2 min-w-0 flex-1 overflow-hidden rounded-full bg-white/[0.04]">
                  <span
                    className="block h-full rounded-full bg-primary-500/70"
                    style={{ width: `${(d.totalTokens / peak) * 100}%` }}
                  />
                </span>
                <span className="w-16 shrink-0 text-right tabular-nums text-gray-200">
                  {formatTokenCount(d.totalTokens)}
                </span>
                {showCost && (
                  <span className="w-16 shrink-0 text-right tabular-nums text-gray-400">
                    {hasCostEstimate(d.costUsd) ? formatUsdEstimate(d.costUsd) : ""}
                  </span>
                )}
              </li>
            ))}
          </ul>
        )}
      </GlassCard>

      <GlassCard>
        <div className="mb-3 flex flex-wrap items-center justify-between gap-3">
          <h2 className="text-label">{t("breakdown")}</h2>
          <div className="flex gap-1.5" role="group" aria-label={t("groupBy")}>
            {GROUPS.map((g) => (
              <Pill key={g} href={href({ groupBy: g })} active={g === groupBy}>
                {t(`group.${g}`)}
              </Pill>
            ))}
          </div>
        </div>
        {breakdown.length === 0 ? (
          <p className="text-sm text-gray-400">{t("empty")}</p>
        ) : (
          <table className="w-full text-sm" data-testid="usage-breakdown">
            <thead>
              <tr className="text-left text-xs text-gray-400">
                <th className="pb-2 font-medium">{t(`group.${groupBy}`)}</th>
                <th className="pb-2 text-right font-medium">{t("tokens")}</th>
                <th className="hidden pb-2 text-right font-medium sm:table-cell">{t("tasks")}</th>
                {showCost && (
                  <th className="pb-2 text-right font-medium">{t("estimatedCost")}</th>
                )}
              </tr>
            </thead>
            <tbody>
              {breakdown.map((row) => (
                <tr key={row.groupKey} className="border-t border-white/[0.04]">
                  <td className="max-w-0 truncate py-2 pr-3 text-gray-200">{row.groupName}</td>
                  <td className="py-2 text-right tabular-nums text-gray-200">
                    {formatTokenCount(row.totalTokens)}
                  </td>
                  <td className="hidden py-2 text-right tabular-nums text-gray-400 sm:table-cell">
                    {row.taskCount}
                  </td>
                  {showCost && (
                    <td className="py-2 text-right tabular-nums text-gray-400">
                      {hasCostEstimate(row.costUsd) ? formatUsdEstimate(row.costUsd) : "—"}
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </GlassCard>
    </div>
  );
}

function Pill({
  href,
  active,
  children,
}: {
  href: string;
  active: boolean;
  children: React.ReactNode;
}) {
  return (
    <Link
      href={href}
      aria-current={active ? "page" : undefined}
      className={cn(
        "rounded-full border px-2.5 py-1 text-xs font-medium transition-colors",
        active
          ? "border-white/[0.12] bg-white/[0.10] text-white"
          : "border-white/[0.06] bg-white/[0.03] text-gray-400 hover:bg-white/[0.06] hover:text-gray-200"
      )}
    >
      {children}
    </Link>
  );
}
