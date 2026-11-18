import { useEffect, useState, useTransition } from "react";
import { useTranslations } from "use-intl";
import { Badge, Button, Modal, useToast } from "@/components/ui";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { useClientApi } from "@/hooks/use-client-api";
import { api, extractErrorMessage } from "@/lib/api";
import { retryTask } from "@/lib/actions";
import { LANE_MAP } from "@/lib/constants";
import { useLabels } from "@/hooks/use-labels";
import { cn, formatUsdEstimate, formatTokenCount } from "@/lib/utils";
import { formatDuration } from "@/components/team/AgentOpsStats";
import type { TaskExecution } from "@/lib/types";

interface RunDrawerProps {
  /** The run to show, with the task that owns it. Null closes the drawer. */
  target: { executionId: string; taskId: string } | null;
  onClose: () => void;
}

const STATUS_VARIANT: Record<string, "success" | "error" | "primary" | "default"> = {
  Completed: "success",
  Failed: "error",
  Running: "primary",
  Cancelled: "default",
};

/**
 * One run, opened from a bar or an attention row: what the agent did, what it touched, what it
 * cost, and a way to run it again.
 *
 * The whole point of the drawer is that the Activity page stops being a dead end. Everything shown
 * here already existed — `GET /api/tasks/{id}/executions` has returned the agent log and the file
 * list since the task page was built — it simply had no reachable entry point from a chart.
 */
function RunDrawer({ target, onClose }: RunDrawerProps) {
  const t = useTranslations("dashboard.activityPage");
  const labels = useLabels();
  const withToken = useClientApi();
  const { toast } = useToast();

  // The loaded run (or error) is stored with the target it belongs to, so a new target reads as
  // loading until its own answer arrives and a closed drawer reads as empty — nothing to reset.
  const [loaded, setLoaded] = useState<{
    key: string;
    run: TaskExecution | null;
    error: string | null;
  } | null>(null);
  const [isRetrying, startRetry] = useTransition();

  const taskId = target?.taskId ?? null;
  const executionId = target?.executionId ?? null;
  const targetKey = taskId && executionId ? `${taskId}\u001f${executionId}` : null;
  const current = loaded && loaded.key === targetKey ? loaded : null;
  const run = current?.run ?? null;
  const error = current?.error ?? null;
  const isLoading = targetKey !== null && current === null;

  useEffect(() => {
    if (!taskId || !executionId || !targetKey) return;

    let cancelled = false;

    void (async () => {
      try {
        const executions = await withToken((token) => api.executions.list(taskId, token));
        if (cancelled) return;
        // The endpoint is per task, not per run; the id picks the one that was clicked.
        setLoaded({
          key: targetKey,
          run: executions.find((e) => e.id === executionId) ?? null,
          error: null,
        });
      } catch (e) {
        if (!cancelled) setLoaded({ key: targetKey, run: null, error: extractErrorMessage(e) });
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [taskId, executionId, targetKey, withToken]);

  const handleRetry = () => {
    if (!taskId) return;
    startRetry(async () => {
      try {
        await retryTask(taskId);
        toast(t("retryQueued"), "success");
        onClose();
      } catch (e) {
        toast(extractErrorMessage(e), "error");
      }
    });
  };

  const lane = run ? LANE_MAP[run.lane] : undefined;
  const roleLabel = run?.agentRole ? labels.agentRole(run.agentRole) : undefined;

  return (
    <Modal open={Boolean(target)} onClose={onClose} title={t("runTitle")}>
      <div className="space-y-4" data-testid="run-drawer">
        {isLoading ? (
          <div className="space-y-2">
            {[0, 1, 2].map((i) => (
              <div key={i} className="h-6 animate-pulse rounded bg-white/[0.04]" />
            ))}
          </div>
        ) : error ? (
          <p className="text-sm text-red-400">{error}</p>
        ) : !run ? (
          <p className="text-sm text-gray-400">{t("runGone")}</p>
        ) : (
          <>
            <div className="flex flex-wrap items-center gap-2">
              <Badge variant={STATUS_VARIANT[run.status] ?? "default"} size="sm">
                {t.has(run.status.toLowerCase()) ? t(run.status.toLowerCase()) : run.status}
              </Badge>
              {lane && (
                <span className="inline-flex items-center gap-1 text-xs text-gray-400">
                  <span
                    className="inline-block h-2 w-2 rounded-sm"
                    style={{ backgroundColor: lane.color }}
                  />
                  {labels.lane(run.lane)}
                </span>
              )}
              {run.agentName && (
                <span className="text-xs text-gray-400">
                  {run.agentName}
                  {roleLabel && <span className="ml-1 text-gray-400">· {roleLabel}</span>}
                </span>
              )}
            </div>

            <dl className="grid grid-cols-2 gap-3 sm:grid-cols-4">
              <Stat label={t("duration")} value={formatDuration(durationSeconds(run))} />
              <Stat label={t("spend")} value={formatUsdEstimate(run.cost)} />
              {/* `billed` carries the cache split; `tokensUsed` counts neither read nor write
                  cache, so on a resumed session it reports a fraction of the real volume. */}
              <Stat
                label={t("tokens")}
                value={formatTokenCount(run.billed?.totalTokens ?? run.tokensUsed)}
              />
              <Stat label={t("filesChanged")} value={String(run.filesChanged.length)} />
            </dl>

            {run.errorMessage && (
              <p className="rounded-lg border border-red-500/20 bg-red-500/[0.06] p-3 font-mono text-xs text-red-400">
                {run.errorMessage}
              </p>
            )}

            {run.filesChanged.length > 0 && (
              <div>
                <p className="text-label mb-1.5">{t("filesChanged")}</p>
                <ul className="max-h-40 space-y-0.5 overflow-y-auto rounded-lg bg-white/[0.02] p-2">
                  {run.filesChanged.map((file) => (
                    <li key={file.path} className="flex items-center gap-2 font-mono text-xs">
                      <span className={cn("w-12 shrink-0 uppercase", actionClass(file.action))}>
                        {file.action}
                      </span>
                      <span className="truncate text-gray-300" title={file.path}>
                        {file.path}
                      </span>
                    </li>
                  ))}
                </ul>
              </div>
            )}

            {run.agentLog && (
              <div>
                <p className="text-label mb-1.5">{t("agentLog")}</p>
                <pre className="max-h-64 overflow-auto rounded-lg bg-white/[0.02] p-3 font-mono text-xs whitespace-pre-wrap text-gray-300">
                  {run.agentLog}
                </pre>
              </div>
            )}

            <RequirePermission permission="TasksExecute">
              <div className="flex items-center gap-2 border-t border-white/[0.06] pt-3">
                <Button size="sm" loading={isRetrying} disabled={isRetrying} onClick={handleRetry}>
                  {t("retryTask")}
                </Button>
                <p className="text-xs text-gray-400">{t("retryHint")}</p>
              </div>
            </RequirePermission>
          </>
        )}
      </div>
    </Modal>
  );
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <dt className="text-label">{label}</dt>
      <dd className="text-xs text-gray-200">{value}</dd>
    </div>
  );
}

/** The list endpoint sends timestamps rather than a duration, unlike the project one. */
function durationSeconds(run: TaskExecution): number | null {
  if (!run.startedAt || !run.completedAt) return null;
  return (new Date(run.completedAt).getTime() - new Date(run.startedAt).getTime()) / 1000;
}

function actionClass(action: string): string {
  const normalized = action.toLowerCase();
  if (normalized.startsWith("a")) return "text-emerald-400";
  if (normalized.startsWith("d")) return "text-red-400";
  return "text-amber-400";
}

export { RunDrawer };
export type { RunDrawerProps };
