import { useEffect, useCallback, useState } from "react";
import { useSignalRConnection } from "@/components/providers/SignalRProvider";
import { trackGroupJoin, trackGroupLeave } from "@/lib/signalr";
import type { TaskMessage, PrComment, BlockingTaskRef } from "@/lib/types";

interface PrCommentAddedEvent {
  taskId: string;
  comment: PrComment;
}

interface PrReviewSubmittedEvent {
  taskId: string;
  reviewId: number;
  state: string;
  authorLogin: string;
}

interface StreamEvent {
  type:
    | "AgentThinking"
    | "ToolUsage"
    | "ToolUse"
    | "CodeChange"
    | "Error"
    | "Progress"
    // Interactive sessions: a command is waiting on a human, and the ruling that released it.
    // Content is the command line for the first and the outcome name for the second; the ids
    // ride in metadata.
    | "ApprovalRequested"
    | "ApprovalSettled";
  content: string;
  timestamp: string;
  metadata?: Record<string, unknown>;
}

interface PlanReviewedEvent {
  taskId: string;
  planId: string;
  status: string;
}

interface LaneChangedEvent {
  taskId: string;
  taskTitle: string;
  fromLane: string;
  toLane: string;
  status: string;
  timestamp: string;
}

/** Status-only transitions (the lane didn't change, e.g. Testing → WaitingUserVerification) */
interface StatusChangedEvent {
  taskId: string;
  newStatus: string;
  timestamp: string;
}

/** reason=null means a previously reported merge failure cleared */
interface TaskMergeFailedEvent {
  taskId: string;
  reason: string | null;
  timestamp: string;
}

/** Carries the task's resulting set of unmet blockers — replace, never merge */
interface TaskDependenciesChangedEvent {
  taskId: string;
  blockedBy: BlockingTaskRef[];
  timestamp: string;
}

/** A code review verdict landed (approve / request_changes). */
interface CodeReviewedEvent {
  taskId: string;
  decision: string;
  summary?: string | null;
  timestamp: string;
}

interface WorkspaceChangedFile {
  path: string;
  status: string;
  additions: number;
  deletions: number;
}

/**
 * The worker re-diffed a running task's working tree and stored it as the task's live
 * code-change snapshot. Carries the file list and stats; the diff itself is fetched by
 * `snapshotId`. `revision` increases per capture so a late event can be dropped.
 */
interface WorkspaceChangedEvent {
  taskId: string;
  snapshotId: string;
  revision: number;
  branchName: string;
  baseBranch: string;
  fileCount: number;
  additions: number;
  deletions: number;
  isTruncated: boolean;
  files: WorkspaceChangedFile[];
  timestamp: string;
}

/**
 * Something about the task changed that isn't a lane/status transition (PR opened, branch
 * created, session closed, title edited). All-null fields mean "refetch to find out".
 */
interface TaskUpdatedEvent {
  taskId: string;
  title?: string | null;
  priority?: string | null;
  status?: string | null;
  currentLane?: string | null;
  isArchived?: boolean | null;
  timestamp: string;
}

interface UseTaskSignalROptions {
  taskId: string;
  onNewMessage?: (message: TaskMessage) => void;
  onStreamEvent?: (event: StreamEvent) => void;
  onPlanReviewed?: (event: PlanReviewedEvent) => void;
  onLaneChanged?: (event: LaneChangedEvent) => void;
  onStatusChanged?: (event: StatusChangedEvent) => void;
  /**
   * Reserved: the API has no emitter for `PrCommentAdded` / `PrReviewSubmitted` yet
   * (see TaskHub.cs) — these callbacks never fire today. Refresh PR panels on
   * `CodeReviewed` or on focus instead of relying on them.
   */
  onPrCommentAdded?: (event: PrCommentAddedEvent) => void;
  onPrReviewSubmitted?: (event: PrReviewSubmittedEvent) => void;
  onTaskMergeFailed?: (event: TaskMergeFailedEvent) => void;
  onTaskDependenciesChanged?: (event: TaskDependenciesChangedEvent) => void;
  onCodeReviewed?: (event: CodeReviewedEvent) => void;
  onTaskUpdated?: (event: TaskUpdatedEvent) => void;
  onWorkspaceChanged?: (event: WorkspaceChangedEvent) => void;
}

function useTaskSignalR({
  taskId,
  onNewMessage,
  onStreamEvent,
  onPlanReviewed,
  onLaneChanged,
  onStatusChanged,
  onPrCommentAdded,
  onPrReviewSubmitted,
  onTaskMergeFailed,
  onTaskDependenciesChanged,
  onCodeReviewed,
  onWorkspaceChanged,
  onTaskUpdated,
}: UseTaskSignalROptions) {
  const { connection, isConnected } = useSignalRConnection();
  const [streamEvents, setStreamEvents] = useState<StreamEvent[]>([]);

  useEffect(() => {
    if (!connection || !isConnected) return;

    // Join is idempotent server-side and always sent (a reconnect needs the re-join);
    // Leave is ref-counted so a second subscriber on the same task (e.g. the approval
    // panel) unmounting doesn't kick this one out of the group.
    trackGroupJoin(`task:${taskId}`);
    connection.invoke("JoinTask", taskId).catch(() => {});

    const handleNewMessage = (message: TaskMessage) => {
      onNewMessage?.(message);
    };

    const handleStreamEvent = (event: StreamEvent) => {
      setStreamEvents((prev) => [...prev, event]);
      onStreamEvent?.(event);
    };

    const handlePlanReviewed = (event: PlanReviewedEvent) => {
      onPlanReviewed?.(event);
    };

    const handleLaneChanged = (event: LaneChangedEvent) => {
      // Clear old stream events when the task moves to a new lane
      setStreamEvents([]);
      onLaneChanged?.(event);
    };

    const handleStatusChanged = (event: StatusChangedEvent) => {
      onStatusChanged?.(event);
    };

    const handlePrCommentAdded = (event: PrCommentAddedEvent) => {
      onPrCommentAdded?.(event);
    };

    const handlePrReviewSubmitted = (event: PrReviewSubmittedEvent) => {
      onPrReviewSubmitted?.(event);
    };

    const handleTaskMergeFailed = (event: TaskMergeFailedEvent) => {
      onTaskMergeFailed?.(event);
    };

    const handleTaskDependenciesChanged = (event: TaskDependenciesChangedEvent) => {
      onTaskDependenciesChanged?.(event);
    };

    const handleWorkspaceChanged = (event: WorkspaceChangedEvent) => {
      onWorkspaceChanged?.(event);
    };

    const handleCodeReviewed = (event: CodeReviewedEvent) => {
      onCodeReviewed?.(event);
    };

    const handleTaskUpdated = (event: TaskUpdatedEvent) => {
      onTaskUpdated?.(event);
    };

    connection.on("NewMessage", handleNewMessage);
    connection.on("StreamEvent", handleStreamEvent);
    connection.on("PlanReviewed", handlePlanReviewed);
    connection.on("LaneChanged", handleLaneChanged);
    connection.on("StatusChanged", handleStatusChanged);
    connection.on("PrCommentAdded", handlePrCommentAdded);
    connection.on("PrReviewSubmitted", handlePrReviewSubmitted);
    connection.on("TaskMergeFailed", handleTaskMergeFailed);
    connection.on("TaskDependenciesChanged", handleTaskDependenciesChanged);
    connection.on("CodeReviewed", handleCodeReviewed);
    connection.on("WorkspaceChanged", handleWorkspaceChanged);
    connection.on("TaskUpdated", handleTaskUpdated);

    return () => {
      connection.off("NewMessage", handleNewMessage);
      connection.off("StreamEvent", handleStreamEvent);
      connection.off("PlanReviewed", handlePlanReviewed);
      connection.off("LaneChanged", handleLaneChanged);
      connection.off("StatusChanged", handleStatusChanged);
      connection.off("PrCommentAdded", handlePrCommentAdded);
      connection.off("PrReviewSubmitted", handlePrReviewSubmitted);
      connection.off("TaskMergeFailed", handleTaskMergeFailed);
      connection.off("TaskDependenciesChanged", handleTaskDependenciesChanged);
      connection.off("CodeReviewed", handleCodeReviewed);
      connection.off("WorkspaceChanged", handleWorkspaceChanged);
      connection.off("TaskUpdated", handleTaskUpdated);
      if (trackGroupLeave(`task:${taskId}`)) {
        connection.invoke("LeaveTask", taskId).catch(() => {});
      }
    };
  }, [connection, isConnected, taskId, onNewMessage, onStreamEvent, onPlanReviewed, onLaneChanged, onStatusChanged, onPrCommentAdded, onPrReviewSubmitted, onTaskMergeFailed, onTaskDependenciesChanged, onCodeReviewed, onTaskUpdated, onWorkspaceChanged]);

  const clearStreamEvents = useCallback(() => setStreamEvents([]), []);

  return { streamEvents, clearStreamEvents, isConnected };
}

export { useTaskSignalR };
export type { StreamEvent, PlanReviewedEvent, LaneChangedEvent, StatusChangedEvent, PrCommentAddedEvent, PrReviewSubmittedEvent, TaskMergeFailedEvent, TaskDependenciesChangedEvent, CodeReviewedEvent, TaskUpdatedEvent, WorkspaceChangedEvent, WorkspaceChangedFile };
