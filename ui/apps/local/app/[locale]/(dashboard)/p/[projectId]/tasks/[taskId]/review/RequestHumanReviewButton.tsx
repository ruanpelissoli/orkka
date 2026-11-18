import { useState, useTransition } from "react";
import { useTranslations } from "use-intl";
import { Button } from "@/components/ui";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { moveTaskLane } from "@/lib/actions";
import type { TaskStatusKey } from "@/lib/constants";
import { refreshData } from "@/lib/data-refresh";

interface RequestHumanReviewButtonProps {
  taskId: string;
  status: TaskStatusKey;
}

/**
 * Escape hatch for a task that has a PR but was sent back to the developer by the
 * reviewer agent: parks it in Code Review as a human gate (`WaitingCodeReview`, no agent),
 * which is exactly the state the review-cycle cap escalates to — so the Approve/Merge
 * controls appear immediately. Implemented as the existing lane move, not a new endpoint.
 *
 * Not offered while an agent is actively working: moving the task under a live run would
 * let that run's completion fight the human gate.
 */
export function RequestHumanReviewButton({ taskId, status }: RequestHumanReviewButtonProps) {
  const t = useTranslations("dashboard.reviewPage");
  const [isPending, startTransition] = useTransition();
  const [error, setError] = useState<string | null>(null);

  if (status === "Active") {
    return (
      <p className="mt-4 text-xs text-gray-400">
        {t("activeHint")}
      </p>
    );
  }

  function handleClick() {
    setError(null);
    startTransition(async () => {
      try {
        await moveTaskLane(taskId, "CodeReview");
        refreshData();
      } catch (e) {
        setError(e instanceof Error ? e.message : t("openError"));
      }
    });
  }

  return (
    <RequirePermission permission="TasksExecute">
      <div className="mt-5 flex flex-col items-center gap-2">
        <Button size="sm" onClick={handleClick} loading={isPending}>
          {t("reviewMyself")}
        </Button>
        <p className="max-w-sm text-xs text-gray-400">
          {t("reviewMyselfHint")}
        </p>
        {error && <p className="text-xs text-red-400">{error}</p>}
      </div>
    </RequirePermission>
  );
}
