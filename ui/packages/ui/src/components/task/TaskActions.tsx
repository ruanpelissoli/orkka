import { useState, useTransition } from "react";
import { Button, useToast } from "@/components/ui";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import {
  submitTask,
  deleteTask,
  archiveTask,
  unarchiveTask,
  blockTask,
  retryTask,
  resetTask,
} from "@/lib/actions";
import { LANE_KEYS } from "@/lib/constants";
import type { Task } from "@/lib/types";
import { useTranslations } from "use-intl";
import { useRouter } from "@/i18n/navigation";

interface TaskActionsProps {
  task: Task;
  projectId: string;
  /** Called after a mutation completes (e.g. for modal re-fetch) */
  onActionComplete?: () => void;
}

function TaskActions({
  task,
  projectId,
  onActionComplete,
}: TaskActionsProps) {
  const t = useTranslations("dashboard");
  const { toast } = useToast();
  const router = useRouter();
  const [isPending, startTransition] = useTransition();
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);
  const [showResetConfirm, setShowResetConfirm] = useState(false);

  const isDrafting = task.currentLane === LANE_KEYS.DRAFTING;
  const isDone = task.currentLane === LANE_KEYS.DONE;
  const isResetting = task.status === "Resetting";
  // Merged code can't be un-merged, so a merged task can't be reset.
  const canReset = !isDrafting && !task.isMerged && !task.isArchived;

  function handleAction(action: () => Promise<unknown>) {
    startTransition(async () => {
      try {
        const result = await action();
        // Actions that return { error } (e.g. submit pre-flight blockers like
        // "no AI provider connected") surface it — the rest keep the old contract.
        if (
          result !== null &&
          typeof result === "object" &&
          "error" in result &&
          typeof (result as { error: unknown }).error === "string"
        ) {
          toast((result as { error: string }).error, "error");
          return;
        }
        onActionComplete?.();
      } catch {
        // The page refetches on refreshData(); a failed call leaves it as it was
      }
    });
  }

  return (
    <div className="space-y-1.5">
    <div className="flex flex-wrap items-center gap-2">
      {/* Submit — only in Drafting */}
      {isDrafting && (
        <RequirePermission permission="TasksExecute">
          <Button
            size="sm"
            disabled={isPending}
            loading={isPending}
            onClick={() => handleAction(() => submitTask(task.id))}
          >
            <svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2">
              <path d="M14 2L7 9M14 2l-5 14-2-6-6-2 14-5z" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
            {t("taskActions.submitToQueue")}
          </Button>
        </RequirePermission>
      )}

      {/* Block / Re-queue — not in Drafting or Done. Re-queue goes through /retry, which
          puts the task back InQueue in the lane it stopped in so an agent picks it up
          again — what the label promises. /unblock only restores the lane's RESTING
          status, and for Code Review that is "waiting for a human", which strands a task
          whose reviewer agent died mid-run. Re-running work is TasksExecute, not
          TasksEdit, so the two branches guard differently. */}
      {/* WaitingCodeReview counts as re-queueable: every review escalation (no reviewer,
          setup failure, unparseable verdict, cycle cap) parks there, and without this
          button a task with no PR had no review UI, no approve, and no way forward. */}
      {!isDrafting && !isDone && (
        (task.isBlocked || task.status === "Blocked" || task.status === "WaitingCodeReview") ? (
          <RequirePermission permission="TasksExecute">
            <Button
              size="sm"
              disabled={isPending || isResetting}
              loading={isPending}
              onClick={() => handleAction(() => retryTask(task.id))}
            >
              <svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5">
                <path d="M13.5 8a5.5 5.5 0 11-1.6-3.9M13.5 1.5V5H10" strokeLinecap="round" strokeLinejoin="round" />
              </svg>
              {t("taskActions.requeue")}
            </Button>
          </RequirePermission>
        ) : (
          <RequirePermission permission="TasksEdit">
            <Button
              variant="ghost"
              size="sm"
              disabled={isPending || isResetting}
              onClick={() => handleAction(() => blockTask(task.id))}
            >
              <svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5">
                <circle cx="8" cy="8" r="6" />
                <path d="M3.75 3.75l8.5 8.5" />
              </svg>
              {t("taskActions.block")}
            </Button>
          </RequirePermission>
        )
      )}

      {/* Reset to Drafting — wipes chat/plans/worktree, cancels a running agent */}
      {canReset && (
        <RequirePermission permission="TasksExecute">
          {isResetting ? (
            <Button variant="ghost" size="sm" disabled loading>
              {t("taskActions.resetting")}
            </Button>
          ) : showResetConfirm ? (
            <div className="flex items-center gap-1.5">
              <Button
                variant="danger"
                size="sm"
                disabled={isPending}
                onClick={() => {
                  setShowResetConfirm(false);
                  handleAction(() => resetTask(task.id));
                }}
              >
                {t("taskActions.confirmReset")}
              </Button>
              <Button
                variant="ghost"
                size="sm"
                onClick={() => setShowResetConfirm(false)}
              >
                {t("taskActions.cancel")}
              </Button>
            </div>
          ) : (
            <Button
              variant="ghost"
              size="sm"
              disabled={isPending}
              onClick={() => setShowResetConfirm(true)}
            >
              <svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5">
                <path d="M2.5 8a5.5 5.5 0 109.5-3.74M4.5 2.5v3h3" strokeLinecap="round" strokeLinejoin="round" />
              </svg>
              {t("taskActions.reset")}
            </Button>
          )}
        </RequirePermission>
      )}

      {/* Archive / Unarchive */}
      <RequirePermission permission="TasksEdit">
        {task.isArchived ? (
          <Button
            variant="ghost"
            size="sm"
            disabled={isPending}
            onClick={() => handleAction(() => unarchiveTask(task.id))}
          >
            {t("taskActions.unarchive")}
          </Button>
        ) : (
          !isDrafting && (
            <Button
              variant="ghost"
              size="sm"
              disabled={isPending || isResetting}
              onClick={() => handleAction(() => archiveTask(task.id))}
            >
              {t("taskActions.archive")}
            </Button>
          )
        )}
      </RequirePermission>

      {/* Delete — only in Drafting */}
      {isDrafting && (
        <RequirePermission permission="TasksDelete">
        {showDeleteConfirm ? (
          <div className="flex items-center gap-1.5">
            <Button
              variant="danger"
              size="sm"
              disabled={isPending}
              onClick={() =>
                handleAction(async () => {
                  router.push(await deleteTask(task.id, projectId));
                })
              }
            >
              {t("taskActions.confirmDelete")}
            </Button>
            <Button
              variant="ghost"
              size="sm"
              onClick={() => setShowDeleteConfirm(false)}
            >
              {t("taskActions.cancel")}
            </Button>
          </div>
        ) : (
          <Button
            variant="ghost"
            size="sm"
            onClick={() => setShowDeleteConfirm(true)}
          >
            <svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5">
              <path d="M2 4h12M5 4V2.5a.5.5 0 01.5-.5h5a.5.5 0 01.5.5V4M6.5 7v4.5M9.5 7v4.5M3.5 4l.5 9.5a1 1 0 001 .5h6a1 1 0 001-.5L12.5 4" strokeLinecap="round" />
            </svg>
            {t("taskActions.delete")}
          </Button>
        )}
        </RequirePermission>
      )}
    </div>
    </div>
  );
}

export { TaskActions };
