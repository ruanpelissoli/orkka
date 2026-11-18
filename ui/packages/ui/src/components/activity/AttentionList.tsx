import { useState } from "react";
import { useTranslations } from "use-intl";
import { Link } from "@/i18n/navigation";
import { Badge, Button, GlassCard } from "@/components/ui";
import { LANE_MAP } from "@/lib/constants";
import { useLabels } from "@/hooks/use-labels";
import { cn, hasCostEstimate, formatUsdEstimate, formatRelativeTime } from "@/lib/utils";
import type { AttentionItem, AttentionKind } from "./activity-model";

interface AttentionListProps {
  items: AttentionItem[];
  projectId: string;
  /** Opens the run behind an item, when it has one. */
  onSelectRun?: (executionId: string, taskId: string) => void;
}

/** How many before the list starts hiding things behind "show all". */
const COLLAPSED_COUNT = 6;

const VARIANT: Record<AttentionKind, "error" | "warning" | "info" | "default"> = {
  failed: "error",
  mergeFailure: "error",
  setupFailure: "error",
  retryLoop: "warning",
  longRunning: "warning",
  awaitingHuman: "info",
  stalled: "default",
};

/**
 * Zone one: what in this window wants a person.
 *
 * The one rule this component exists to enforce is that the error text is *on the page*. The old
 * timeline had every failure's message already — in a `title` attribute, which means it was only
 * ever read by someone who already suspected something was wrong and went hunting with a mouse.
 */
function AttentionList({ items, projectId, onSelectRun }: AttentionListProps) {
  const t = useTranslations("dashboard.activityPage");
  const labels = useLabels();
  const [expanded, setExpanded] = useState(false);

  if (items.length === 0) {
    return (
      <GlassCard className="text-center">
        <p className="text-sm text-gray-300">{t("attentionClear")}</p>
        <p className="mt-1 text-xs text-gray-400">{t("attentionClearHint")}</p>
      </GlassCard>
    );
  }

  const visible = expanded ? items : items.slice(0, COLLAPSED_COUNT);

  return (
    <GlassCard padding="none" className="overflow-hidden">
      <ul className="divide-y divide-white/[0.04]" data-testid="attention-list">
        {visible.map((item) => (
          <li key={item.id} className="px-4 py-3 hover:bg-white/[0.02]" data-attention-kind={item.kind}>
            <div className="flex flex-wrap items-center gap-2">
              <Badge variant={VARIANT[item.kind]} size="sm">
                {t(`attention.${item.kind}`)}
              </Badge>
              <Link
                href={`/p/${projectId}/tasks/${item.taskId}`}
                className="truncate text-sm text-gray-200 hover:text-primary-400"
                title={`${item.taskKey} · ${item.taskTitle}`}
              >
                <span className="font-mono text-xs text-gray-400">{item.taskKey}</span>{" "}
                {item.taskTitle}
              </Link>
              {item.lane && (
                <span className="inline-flex items-center gap-1 text-xs text-gray-400">
                  <span
                    className="inline-block h-2 w-2 rounded-sm"
                    style={{ backgroundColor: LANE_MAP[item.lane]?.color }}
                  />
                  {labels.lane(item.lane)}
                </span>
              )}
              <span className="ml-auto text-xs text-gray-400">{formatRelativeTime(item.at)}</span>
            </div>

            {item.detail && (
              <p
                className={cn(
                  "mt-1.5 line-clamp-2 font-mono text-xs",
                  item.kind === "failed" ? "text-red-400/90" : "text-amber-400/90"
                )}
                title={item.detail}
              >
                {item.detail}
              </p>
            )}

            <div className="mt-1.5 flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-gray-400">
              {item.count > 1 && <span>{t("attentionRuns", { count: item.count })}</span>}
              {item.agentName && <span>{item.agentName}</span>}
              {hasCostEstimate(item.cost) && <span>{formatUsdEstimate(item.cost)}</span>}
              {item.executionId && onSelectRun && (
                <button
                  type="button"
                  className="text-primary-400 hover:text-primary-300"
                  onClick={() => onSelectRun(item.executionId!, item.taskId)}
                >
                  {t("openRun")}
                </button>
              )}
            </div>
          </li>
        ))}
      </ul>

      {items.length > COLLAPSED_COUNT && (
        <div className="border-t border-white/[0.04] px-4 py-2">
          <Button size="sm" variant="ghost" onClick={() => setExpanded((v) => !v)}>
            {expanded ? t("showLess") : t("showAll", { count: items.length })}
          </Button>
        </div>
      )}
    </GlassCard>
  );
}

export { AttentionList };
export type { AttentionListProps };
