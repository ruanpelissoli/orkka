import { useState, useTransition } from "react";
import { useTranslations } from "use-intl";
import { GlassCard, Button } from "@/components/ui";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { finishInteractiveSession } from "@/lib/actions";
import type { Task } from "@/lib/types";
import { refreshData } from "@/lib/data-refresh";

const SESSION_ACCENT = "#10b981";

/**
 * Header for a live interactive session: what branch it is working on, and the one control that
 * ends it.
 *
 * Sits above the task detail rather than inside it because everything below — the conversation
 * thread, the agent stream, the timeline — already works for a session unchanged. A session is a
 * Developing phase whose turns a human triggers, so the only genuinely new affordance is
 * finishing it.
 */
export function InteractiveSessionBanner({
  task,
  className,
  onFinished,
}: {
  task: Task;
  className?: string;
  /**
   * Refetch for surfaces whose task is client-side state rather than server-rendered props —
   * the board drawer. `refreshData()` refetches the page queries, which does nothing for data the
   * modal fetched itself, so without this the drawer never saw the finish land.
   */
  onFinished?: () => void;
}) {
  const t = useTranslations("dashboard.interactiveSession");
  const [pending, startTransition] = useTransition();
  const [error, setError] = useState<string | null>(null);
  // Latched locally the moment the server accepts the request. Deriving "wrapping up" purely
  // from the task meant the button re-enabled as soon as the action resolved and stayed
  // clickable until fresh data arrived — so the session could be finished over and over. The
  // request is recorded server-side and idempotent, but the UI should not invite the repeat.
  const [requested, setRequested] = useState(false);

  // Only a LIVE session gets a banner. A finished one is an ordinary task from here on, and its
  // branch, PR and review are already shown by the detail below.
  if (!task.isInteractive || !task.interactiveSessionStartedAt) return null;

  const wrappingUp = requested || Boolean(task.interactiveFinishRequestedAt);

  function handleFinish() {
    setError(null);
    startTransition(async () => {
      const result = await finishInteractiveSession(task.id);
      if (result.error) {
        setError(result.error);
        return;
      }

      setRequested(true);
      // Both, deliberately: the full page reads the task from its page query (refreshData) and
      // the drawer holds it in state (onFinished). Each surface ignores the one that is not its
      // mechanism, and neither knew to update before.
      onFinished?.();
      refreshData();
    });
  }

  return (
    <GlassCard padding="md" accentColor={SESSION_ACCENT} className={className}>
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <span
              className="inline-block h-2 w-2 shrink-0 rounded-full"
              style={{ backgroundColor: SESSION_ACCENT }}
              aria-hidden="true"
            />
            <h3 className="font-display text-sm font-semibold text-white">
              {wrappingUp ? t("wrappingUp") : t("live")}
            </h3>
          </div>
          <p className="mt-0.5 text-xs text-gray-400">
            {task.branchName
              ? t("workingOn", { branch: task.branchName })
              : t("preparing")}
          </p>
          <p className="mt-1 text-xs text-gray-400">
            {wrappingUp ? t("wrappingUpHint") : t("liveHint")}
          </p>
          {error && (
            <p className="mt-2 text-xs text-red-400" role="alert">
              {error}
            </p>
          )}
        </div>

        <RequirePermission permission="TasksExecute">
          <Button
            variant="secondary"
            onClick={handleFinish}
            // Disabled once the request is recorded: the worker still has to commit and push,
            // and a second press would only look like a second session ending.
            disabled={pending || wrappingUp}
          >
            {pending || wrappingUp ? t("finishing") : t("finish")}
          </Button>
        </RequirePermission>
      </div>
    </GlassCard>
  );
}
