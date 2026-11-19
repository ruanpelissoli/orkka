import { useEffect } from "react";
import { useSignalRConnection } from "@/components/providers/SignalRProvider";
import { trackGroupJoin, trackGroupLeave } from "@/lib/signalr";
import type {
  AutopilotStatus,
  BlockingTaskRef,
  MilestonePlanFailureCode,
  MilestonePlanJobStatus,
  MilestonePlanStep,
} from "@/lib/types";

interface LaneChangedEvent {
  taskId: string;
  taskTitle: string;
  fromLane: string;
  toLane: string;
  status: string;
  timestamp: string;
}

interface StatusChangedEvent {
  taskId: string;
  newStatus: string;
  timestamp: string;
}

interface TaskUpdatedEvent {
  taskId: string;
  title?: string | null;
  priority?: string | null;
  status?: string | null;
  currentLane?: string | null;
  isArchived?: boolean | null;
  timestamp: string;
}

interface TaskDeletedEvent {
  taskId: string;
  timestamp: string;
}

interface TaskCreatedEvent {
  taskId: string;
  number: number;
  code: string;
  title: string;
  priority: string;
  type: string;
  currentLane: string;
  status: string;
  repoId: string;
  repoName: string | null;
  repoColor: string | null;
  tags: string[] | null;
  createdAt: string;
}

/** reason=null means a previously reported merge failure cleared */
interface TaskMergeFailedEvent {
  taskId: string;
  reason: string | null;
  timestamp: string;
}

/** Carries the task's resulting set of unmet blockers â€” replace, never merge */
interface TaskDependenciesChangedEvent {
  taskId: string;
  blockedBy: BlockingTaskRef[];
  timestamp: string;
}

/** Server-truncated preview of the newest agent message on a task (card preview) */
interface TaskLastMessageChangedEvent {
  taskId: string;
  role: string;
  preview: string;
  messageType: string;
  agentName: string | null;
  createdAt: string;
}

/**
 * Milestone created, edited, auto-transitioned, or deleted. Counts are the server's
 * freshly computed rollup â€” replace, never merge. `deleted` carries zeroed counts.
 */
interface MilestoneChangedEvent {
  milestoneId: string;
  status: string | null;
  total: number;
  done: number;
  inFlight: number;
  needsAttention: number;
  notStarted: number;
  deleted: boolean;
  timestamp: string;
}

/**
 * Autopilot started, stopped, or moved its counters. Carries the whole status â€” replace,
 * never merge.
 *
 * Most of these come from the Worker, not from anyone clicking: a session that drains or hits
 * its cap at 3am has to flip the toggle for whoever is watching, or it sits visually "on"
 * until the page is reloaded.
 */
interface AutopilotChangedEvent {
  projectId: string;
  status: AutopilotStatus;
  at: string;
}

/**
 * A queued milestone-planning run moved: claimed, changed phase, finished, or failed.
 *
 * Carries no plan — the proposal is thousands of characters and has to survive a refresh
 * anyway, so completion is a signal to re-read the job. What it does carry is the phase, which
 * is what turns an indefinite spinner into something a user waits out instead of abandoning.
 */
interface MilestonePlanChangedEvent {
  jobId: string;
  projectId: string;
  milestoneId: string | null;
  status: MilestonePlanJobStatus;
  step: MilestonePlanStep | null;
  failureCode: MilestonePlanFailureCode | null;
  errorMessage: string | null;
  startedAt: string | null;
  timestamp: string;
}

interface UseBoardSignalROptions {
  /** The project whose board group to join (its ULID). */
  projectId?: string;
  /**
   * Several projects at once — the workspace board joins the board group of every project it
   * shows, each through the hub's own JoinBoard check. One set of handlers either way: a hook
   * per project would register every handler once per project and run each event N times.
   */
  projectIds?: readonly string[];
  onLaneChanged?: (event: LaneChangedEvent) => void;
  onStatusChanged?: (event: StatusChangedEvent) => void;
  onTaskUpdated?: (event: TaskUpdatedEvent) => void;
  onTaskCreated?: (event: TaskCreatedEvent) => void;
  onTaskDeleted?: (event: TaskDeletedEvent) => void;
  onTaskMergeFailed?: (event: TaskMergeFailedEvent) => void;
  onTaskDependenciesChanged?: (event: TaskDependenciesChangedEvent) => void;
  onTaskLastMessageChanged?: (event: TaskLastMessageChangedEvent) => void;
  onMilestoneChanged?: (event: MilestoneChangedEvent) => void;
  onMilestonePlanChanged?: (event: MilestonePlanChangedEvent) => void;
  onAutopilotChanged?: (event: AutopilotChangedEvent) => void;
  /**
   * Fired once the board group is joined, including after a reconnect.
   *
   * Deltas only describe what happened while this component was listening. Anything that moved
   * beforehand — during navigation, a sleeping laptop, a dropped socket, or the five seconds
   * between a two-phase reset being requested and the worker finalising it — is invisible to
   * them. The subscriber uses this to reconcile against the server once.
   */
  onConnected?: () => void;
}

function useBoardSignalR({
  projectId,
  projectIds,
  onLaneChanged,
  onStatusChanged,
  onTaskUpdated,
  onTaskCreated,
  onTaskDeleted,
  onTaskMergeFailed,
  onTaskDependenciesChanged,
  onTaskLastMessageChanged,
  onMilestoneChanged,
  onMilestonePlanChanged,
  onAutopilotChanged,
  onConnected,
}: UseBoardSignalROptions) {
  const { connection, isConnected } = useSignalRConnection();
  // A stable key for the set of groups, so a new array with the same ids does not resubscribe.
  const groupKey = (projectIds ?? (projectId ? [projectId] : [])).join(",");

  useEffect(() => {
    if (!connection || !isConnected) return;
    const ids = groupKey ? groupKey.split(",") : [];
    // No group, no handlers: handlers are per connection, not per group, so registering them
    // here would deliver another board's events (e.g. a disabled metrics strip on the workspace
    // board would refetch on every one of them).
    if (ids.length === 0) return;

    // Reconcile only AFTER the group is joined. Refetching first would leave a window in
    // which a change lands between the fetch and the subscription, and is then never
    // delivered — the exact gap this callback exists to close.
    //
    // Join is idempotent and always sent (reconnects need the re-join); Leave is
    // ref-counted because the board and the autopilot toggle share one connection, and one
    // unmounting must not kick the other out of the group.
    for (const id of ids) trackGroupJoin(`board:${id}`);
    Promise.all(ids.map((id) => connection.invoke("JoinBoard", id)))
      .then(() => onConnected?.())
      .catch(() => {
        // SignalR may not be available
      });

    const handleLaneChanged = (event: LaneChangedEvent) => {
      onLaneChanged?.(event);
    };
    const handleStatusChanged = (event: StatusChangedEvent) => {
      onStatusChanged?.(event);
    };
    const handleTaskUpdated = (event: TaskUpdatedEvent) => {
      onTaskUpdated?.(event);
    };
    const handleTaskCreated = (event: TaskCreatedEvent) => {
      onTaskCreated?.(event);
    };
    const handleTaskDeleted = (event: TaskDeletedEvent) => {
      onTaskDeleted?.(event);
    };
    const handleTaskMergeFailed = (event: TaskMergeFailedEvent) => {
      onTaskMergeFailed?.(event);
    };
    const handleTaskDependenciesChanged = (event: TaskDependenciesChangedEvent) => {
      onTaskDependenciesChanged?.(event);
    };
    const handleTaskLastMessageChanged = (event: TaskLastMessageChangedEvent) => {
      onTaskLastMessageChanged?.(event);
    };
    const handleMilestoneChanged = (event: MilestoneChangedEvent) => {
      onMilestoneChanged?.(event);
    };
    const handleMilestonePlanChanged = (event: MilestonePlanChangedEvent) => {
      onMilestonePlanChanged?.(event);
    };
    const handleAutopilotChanged = (event: AutopilotChangedEvent) => {
      onAutopilotChanged?.(event);
    };

    connection.on("LaneChanged", handleLaneChanged);
    connection.on("StatusChanged", handleStatusChanged);
    connection.on("TaskUpdated", handleTaskUpdated);
    connection.on("TaskCreated", handleTaskCreated);
    connection.on("TaskDeleted", handleTaskDeleted);
    connection.on("TaskMergeFailed", handleTaskMergeFailed);
    connection.on("TaskDependenciesChanged", handleTaskDependenciesChanged);
    connection.on("TaskLastMessageChanged", handleTaskLastMessageChanged);
    connection.on("MilestoneChanged", handleMilestoneChanged);
    connection.on("MilestonePlanChanged", handleMilestonePlanChanged);
    connection.on("AutopilotChanged", handleAutopilotChanged);

    return () => {
      connection.off("LaneChanged", handleLaneChanged);
      connection.off("StatusChanged", handleStatusChanged);
      connection.off("TaskUpdated", handleTaskUpdated);
      connection.off("TaskCreated", handleTaskCreated);
      connection.off("TaskDeleted", handleTaskDeleted);
      connection.off("TaskMergeFailed", handleTaskMergeFailed);
      connection.off("TaskDependenciesChanged", handleTaskDependenciesChanged);
      connection.off("TaskLastMessageChanged", handleTaskLastMessageChanged);
      connection.off("MilestoneChanged", handleMilestoneChanged);
      connection.off("MilestonePlanChanged", handleMilestonePlanChanged);
      connection.off("AutopilotChanged", handleAutopilotChanged);
      for (const id of ids) {
        if (trackGroupLeave(`board:${id}`)) {
          connection.invoke("LeaveBoard", id).catch(() => {});
        }
      }
    };
  }, [connection, isConnected, groupKey, onLaneChanged, onStatusChanged, onTaskUpdated, onTaskCreated, onTaskDeleted, onTaskMergeFailed, onTaskDependenciesChanged, onTaskLastMessageChanged, onMilestoneChanged, onMilestonePlanChanged, onAutopilotChanged, onConnected]);
}

export { useBoardSignalR };
export type { LaneChangedEvent, StatusChangedEvent, TaskUpdatedEvent, TaskCreatedEvent, TaskDeletedEvent, TaskMergeFailedEvent, TaskDependenciesChangedEvent, TaskLastMessageChangedEvent, MilestoneChangedEvent, MilestonePlanChangedEvent, AutopilotChangedEvent };

