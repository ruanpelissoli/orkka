import { useState, useCallback, useRef } from "react";
import { useRouter } from "@/i18n/navigation";
import { PrReviewPanel } from "@/components/pr/PrReviewPanel";
import { useTaskSignalR } from "@/hooks/use-task-signalr";
import { LANE_KEYS } from "@/lib/constants";
import type { Task } from "@/lib/types";
import type { LaneKey } from "@/lib/constants";
import type { LaneChangedEvent } from "@/hooks/use-task-signalr";
import { refreshData } from "@/lib/data-refresh";

interface PrReviewLiveProps {
  task: Task;
  projectId: string;
  /** The task's repo is a remote — its work is published as a forge PR, not merged. */
  repoIsRemote?: boolean;
}

function PrReviewLive({ task, projectId, repoIsRemote }: PrReviewLiveProps) {
  const router = useRouter();
  const [localTask, setLocalTask] = useState(task);

  // A refetched task replaces the live copy (adjusted during render).
  const [prevTask, setPrevTask] = useState(task);
  if (task !== prevTask) {
    setPrevTask(task);
    setLocalTask(task);
  }

  const handleRefresh = useCallback(() => {
    refreshData();
  }, []);

  const handleLaneChanged = useCallback(
    (event: LaneChangedEvent) => {
      setLocalTask((prev) => ({ ...prev, currentLane: event.toLane as LaneKey }));
      if (event.toLane !== LANE_KEYS.CODE_REVIEW) {
        router.push(`/p/${projectId}/tasks/${task.number}`);
      }
    },
    [router, projectId, task.number],
  );

  // Ref for PrReviewPanel refresh via SignalR
  const prPanelRef = useRef<HTMLDivElement>(null);
  const refreshPrPanel = useCallback(() => {
    const el = prPanelRef.current?.querySelector("[data-pr-panel]") as
      | (HTMLDivElement & { __prRefresh?: () => void })
      | null;
    el?.__prRefresh?.();
  }, []);

  // Status drives the review controls: they appear the moment the task starts waiting on
  // a human, without a reload — the escalation case is exactly when someone is watching.
  const handleStatusChanged = useCallback((event: { newStatus: string }) => {
    setLocalTask((prev) => ({ ...prev, status: event.newStatus as Task["status"] }));
  }, []);

  useTaskSignalR({
    taskId: task.id,
    onLaneChanged: handleLaneChanged,
    onStatusChanged: handleStatusChanged,
    onPrCommentAdded: refreshPrPanel,
    onPrReviewSubmitted: refreshPrPanel,
  });

  return (
    <div ref={prPanelRef}>
      <PrReviewPanel
        taskId={localTask.id}
        taskStatus={localTask.status}
        projectId={projectId}
        onRefresh={handleRefresh}
        repoIsRemote={repoIsRemote}
      />
    </div>
  );
}

export { PrReviewLive };
