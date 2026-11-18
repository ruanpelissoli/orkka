import { useEffect, useState } from "react";
import { useTranslations } from "use-intl";
import { cn } from "@/lib/utils";
import { GlassCard, Badge, Button, Markdown, Collapsible, Term } from "@/components/ui";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { reviewPlan } from "@/lib/actions";
import { api } from "@/lib/api";
import { useClientApi } from "@/hooks/use-client-api";
import { TaskForecastSummary } from "@/components/forecast";
import { LANE_KEYS } from "@/lib/constants";
import { useLabels } from "@/hooks/use-labels";
import type { TaskPlan, TaskForecast } from "@/lib/types";

interface PlanViewerProps {
  plan: TaskPlan;
  taskId: string;
  taskLane?: string;
  taskStatus?: string;
}

const STATUS_BADGE: Record<string, { variant: "warning" | "success" | "error" | "default" }> = {
  Draft: { variant: "default" },
  WaitingPlanApproval: { variant: "warning" },
  Approved: { variant: "success" },
  Rejected: { variant: "error" },
};

function PlanViewer({ plan, taskId, taskLane, taskStatus }: PlanViewerProps) {
  const t = useTranslations("dashboard.planViewer");
  const tCommon = useTranslations("common");
  const labels = useLabels();
  const withToken = useClientApi();
  // The moment a human decides to spend money is the highest-value place to show what it
  // costs. Fetched with surface=PlanApproval so calibration scores it separately from the
  // estimate shown at creation — they are different predictions.
  const [forecast, setForecast] = useState<TaskForecast | null>(null);
  const [fileExpanded, setFileExpanded] = useState<Record<number, boolean>>({});
  const [rejecting, setRejecting] = useState(false);
  const [feedback, setFeedback] = useState("");

  const awaitingReview = plan.status === "WaitingPlanApproval" || taskLane === LANE_KEYS.PLAN_READY || taskStatus === "WaitingPlanApproval";
  const showActions = awaitingReview;

  const effectiveStatus = awaitingReview && plan.status === "Draft" ? "WaitingPlanApproval" : plan.status;
  const statusBadge = STATUS_BADGE[effectiveStatus] ?? STATUS_BADGE.Draft;
  const statusLabel = labels.planStatus(effectiveStatus in STATUS_BADGE ? effectiveStatus : "Draft");

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const result = await withToken((token) =>
          api.tasks.forecast(taskId, "PlanApproval", token)
        );
        if (!cancelled) setForecast(result);
      } catch {
        // No forecast renders as the "not enough history yet" empty state.
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [withToken, taskId]);

  // Collapse plan content by default when approved or rejected
  const isResolved = effectiveStatus === "Approved" || effectiveStatus === "Rejected";
  const [collapsed, setCollapsed] = useState(isResolved);

  return (
    <GlassCard padding="md" className="lg:p-7" accentColor="#8b5cf6">
      {/* Header */}
      <div
        className={cn(
          "flex items-center justify-between",
          !collapsed && "mb-4",
        )}
      >
        <div className="flex items-center gap-2">
          <h3 className="font-display text-base font-semibold text-white">
            <Term k="plan">{t("title")}</Term>
          </h3>
          <Badge variant={statusBadge.variant} size="sm" dot>
            {statusLabel}
          </Badge>
          <span className="text-xs text-gray-400">
            v{plan.version}
          </span>
        </div>
        {isResolved && (
          <button
            onClick={() => setCollapsed((prev) => !prev)}
            className="flex items-center gap-1 text-xs text-gray-400 hover:text-gray-300 transition-colors"
          >
            {collapsed ? t("show") : t("hide")}
            <svg
              width="12"
              height="12"
              viewBox="0 0 16 16"
              fill="none"
              className={cn(
                "transition-transform",
                !collapsed && "rotate-180"
              )}
            >
              <path d="M4 6l4 4 4-4" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
            </svg>
          </button>
        )}
      </div>

      {/* Collapsible content */}
      <Collapsible open={!collapsed}>
        <>
          {/* Summary */}
          <Markdown className="mb-4">{plan.content.summary}</Markdown>

          {/* Approach */}
          <div className="mb-4">
            <h4 className="text-label mb-2">
              {t("approach")}
            </h4>
            <div className="rounded-xl bg-white/[0.02] p-4">
              <Markdown className="text-xs text-gray-400">
                {plan.content.approach}
              </Markdown>
            </div>
          </div>

          {/* Files changed */}
          {plan.content.filesChanged && plan.content.filesChanged.length > 0 && (
            <div className="mb-6">
              <h4 className="text-label mb-2">
                {t("filesTitle", { count: plan.content.filesChanged.length })}
              </h4>
              <div className="space-y-1">
                {plan.content.filesChanged.map((file, i) => (
                  <div key={i}>
                    <button
                      onClick={() =>
                        setFileExpanded((prev) => ({ ...prev, [i]: !prev[i] }))
                      }
                      className="flex w-full items-center gap-2 rounded-lg px-3 py-2 text-left text-xs transition-colors hover:bg-white/[0.03]"
                    >
                      <span
                        className={cn(
                          "shrink-0 rounded px-1.5 py-0.5 text-xs font-semibold",
                          file.action === "create" && "bg-emerald-500/10 text-emerald-400",
                          file.action === "modify" && "bg-amber-500/10 text-amber-400",
                          file.action === "delete" && "bg-red-500/10 text-red-400",
                        )}
                      >
                        {file.action.charAt(0).toUpperCase()}
                      </span>
                      <span className="font-mono text-gray-300 truncate">
                        {file.path}
                      </span>
                      <svg
                        width="12"
                        height="12"
                        viewBox="0 0 16 16"
                        fill="none"
                        className={cn(
                          "ml-auto shrink-0 text-gray-400 transition-transform",
                          fileExpanded[i] && "rotate-180"
                        )}
                      >
                        <path d="M4 6l4 4 4-4" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
                      </svg>
                    </button>
                    <Collapsible open={!!fileExpanded[i]}>
                      <div className="ml-10 mb-2 text-xs text-gray-400">
                        {file.description}
                      </div>
                    </Collapsible>
                  </div>
                ))}
              </div>
            </div>
          )}

          {/* Actions */}
          {showActions && (
            <RequirePermission permission="PlansReview">
            <div className="space-y-3">
              <TaskForecastSummary forecast={forecast} />
              {rejecting ? (
                <div className="space-y-2">
                  <textarea
                    value={feedback}
                    onChange={(e) => setFeedback(e.target.value)}
                    placeholder={t("rejectPlaceholder")}
                    className="w-full rounded-xl bg-white/[0.03] border border-white/[0.06] px-4 py-2.5 text-sm text-gray-100 placeholder:text-gray-500 focus:outline-none focus:border-primary-500/50 resize-none"
                    rows={3}
                  />
                  <div className="flex gap-2">
                    <Button
                      variant="danger"
                      size="sm"
                      onClick={() => reviewPlan(taskId, plan.id, "Rejected", feedback)}
                    >
                      {t("rejectPlan")}
                    </Button>
                    <Button
                      variant="ghost"
                      size="sm"
                      onClick={() => {
                        setRejecting(false);
                        setFeedback("");
                      }}
                    >
                      {tCommon("actions.cancel")}
                    </Button>
                  </div>
                </div>
              ) : (
                <div className="flex gap-2">
                  <Button
                    variant="success"
                    size="md"
                    onClick={() => reviewPlan(taskId, plan.id, "Approved", undefined)}
                    className="flex-1"
                  >
                    {t("approvePlan")}
                  </Button>
                  <Button
                    variant="ghost"
                    size="md"
                    onClick={() => setRejecting(true)}
                  >
                    {t("requestChanges")}
                  </Button>
                </div>
              )}
            </div>
            </RequirePermission>
          )}
        </>
      </Collapsible>
    </GlassCard>
  );
}

export { PlanViewer };
