import { useState, useCallback, useEffect, useRef, type MutableRefObject } from "react";
import type { TaskSummary, TaskBoardResponse, BoardLaneResponse } from "@/lib/types";
import { normalizeBoardLaneKey } from "@/lib/constants";
import { api } from "@/lib/api";
import { useClientApi } from "@/hooks/use-client-api";
import { useBoardSignalR } from "@/hooks/use-board-signalr";
import type {
  LaneChangedEvent,
  StatusChangedEvent,
  TaskUpdatedEvent,
  TaskCreatedEvent,
  TaskDeletedEvent,
  TaskMergeFailedEvent,
  TaskDependenciesChangedEvent,
  TaskLastMessageChangedEvent,
} from "@/hooks/use-board-signalr";
import { applyLaneChange } from "./lane-reducer";

/** The server keys board lanes in snake_case; SignalR events and lane mutations use
 * enum names. Normalize once on receipt so every downstream comparison matches. */
function normalizeLanes(lanes: BoardLaneResponse[]): BoardLaneResponse[] {
  return lanes.map((lane) => ({ ...lane, key: normalizeBoardLaneKey(lane.key) }));
}

interface UseLiveLanesOptions {
  boardData: TaskBoardResponse;
  /** Route ref used in URLs — the project slug (or a legacy ULID) */
  projectId: string;
  /** Project ULID — SignalR board groups are keyed by it */
  projectUlid: string;
  /**
   * The task currently open in the viewer. Events for it never bump the unread dot —
   * the person is already looking at it. A ref, so SignalR callbacks read the latest
   * value without resubscribing on every selection change.
   */
  selectedTaskIdRef: MutableRefObject<string | null>;
  /** Fires after any structural change (lane move, create, delete, edit) — a search
   * or derived view can re-run. Not fired for message previews or dependency updates. */
  onStructuralChange?: () => void;
  /**
   * Show archived tasks in their lanes. Off by default — archiving is what clears a finished
   * task off the board, and the two-week auto-archive would otherwise be indistinguishable
   * from deletion. Flipping it refetches, because the server-rendered snapshot was built
   * under the old answer.
   */
  includeArchived?: boolean;
}

/**
 * The live lane model behind both board views (kanban and Code View): the server-rendered
 * snapshot, patched by SignalR deltas, reconciled once on (re)connect. Extracted from
 * TaskBoard so the two views can never drift in how they interpret an event.
 */
function useLiveLanes({
  boardData,
  projectId,
  projectUlid,
  selectedTaskIdRef,
  onStructuralChange,
  includeArchived = false,
}: UseLiveLanesOptions) {
  const withToken = useClientApi();
  const [lanes, setLanes] = useState<BoardLaneResponse[]>(() => normalizeLanes(boardData.lanes));

  // Page refetches (refreshData() after a mutation) deliver fresh lanes — apply them
  // instead of keeping the initial load's snapshot (adjusted during render, not in an effect)
  const [appliedBoardData, setAppliedBoardData] = useState(boardData);
  if (boardData !== appliedBoardData) {
    setAppliedBoardData(boardData);
    setLanes(normalizeLanes(boardData.lanes));
  }

  /**
   * Re-sync the board once the live connection is established.
   *
   * The page's snapshot is only true as of the moment it was loaded, and the SignalR
   * deltas only describe what happened after this component subscribed. Anything that moved in
   * between is lost until a hard refresh — which is not a rare edge: a two-phase reset finalises
   * on the worker's sweep about five seconds AFTER the action refreshed the page, so the loaded
   * snapshot is stale by construction, and nothing refreshes again.
   *
   * A reconnect after sleep or a dropped socket has the same hole. One fetch on connect closes
   * all of them, and it is cheap — the board endpoint is a single query.
   */
  const reconcile = useCallback(() => {
    withToken((token) => api.board.get(projectId, token, includeArchived))
      .then((fresh) => setLanes(normalizeLanes(fresh.lanes)))
      .catch(() => {
        // Keep what we have — the deltas still apply, and a failed reconcile must never
        // blank a board the user is reading.
      });
  }, [projectId, withToken, includeArchived]);

  // Toggling the archived filter changes what the board is a view OF, and no SignalR delta
  // describes that — only a refetch can. Skipped on the first run: the server-rendered
  // snapshot already answers the default (archived hidden), and refetching it would make
  // every board load hit the API twice.
  const archivedFilterApplied = useRef(includeArchived);
  useEffect(() => {
    if (archivedFilterApplied.current === includeArchived) return;
    archivedFilterApplied.current = includeArchived;
    reconcile();
  }, [includeArchived, reconcile]);

  const handleLaneChanged = useCallback((event: LaneChangedEvent) => {
    setLanes((prev) => applyLaneChange(prev, event, selectedTaskIdRef.current));
    onStructuralChange?.();
  }, [selectedTaskIdRef, onStructuralChange]);

  const handleStatusChanged = useCallback((event: StatusChangedEvent) => {
    setLanes((prev) =>
      prev.map((lane) => ({
        ...lane,
        tasks: lane.tasks.map((t) =>
          t.id === event.taskId
            ? {
                ...t,
                status: event.newStatus as TaskSummary["status"],
                isBlocked: event.newStatus === "Blocked",
                hasUnread: t.id !== selectedTaskIdRef.current ? true : t.hasUnread,
              }
            : t
        ),
      }))
    );
  }, [selectedTaskIdRef]);

  const handleTaskUpdated = useCallback((event: TaskUpdatedEvent) => {
    setLanes((prev) => {
      // Archived tasks leave the board — same as deletion from the viewer's perspective.
      // Unless the viewer asked to see them, in which case the card stays and re-renders
      // as archived: dropping it would contradict the filter they just switched on.
      if (event.isArchived) {
        return prev.map((lane) => ({
          ...lane,
          tasks: includeArchived
            ? lane.tasks.map((t) => (t.id === event.taskId ? { ...t, isArchived: true } : t))
            : lane.tasks.filter((t) => t.id !== event.taskId),
        }));
      }
      return prev.map((lane) => ({
        ...lane,
        tasks: lane.tasks.map((t) => {
          if (t.id !== event.taskId) return t;
          return {
            ...t,
            ...(event.title != null && { title: event.title }),
            ...(event.priority != null && { priority: event.priority as TaskSummary["priority"] }),
            ...(event.status != null && { status: event.status as TaskSummary["status"] }),
            hasUnread: t.id !== selectedTaskIdRef.current ? true : t.hasUnread,
          };
        }),
      }));
    });
    onStructuralChange?.();
  }, [selectedTaskIdRef, onStructuralChange, includeArchived]);

  const handleTaskMergeFailed = useCallback((event: TaskMergeFailedEvent) => {
    setLanes((prev) =>
      prev.map((lane) => ({
        ...lane,
        tasks: lane.tasks.map((t) =>
          t.id === event.taskId ? { ...t, mergeFailureReason: event.reason } : t
        ),
      }))
    );
  }, []);

  // Replace the card's blocker set with the event's — informational, no unread bump
  const handleTaskDependenciesChanged = useCallback((event: TaskDependenciesChangedEvent) => {
    setLanes((prev) =>
      prev.map((lane) => ({
        ...lane,
        tasks: lane.tasks.map((t) =>
          t.id === event.taskId ? { ...t, blockedBy: event.blockedBy } : t
        ),
      }))
    );
  }, []);

  // Newest agent message preview — informational, no unread bump (agents stream many
  // chunks; the dot would stay permanently lit) and no structural-change bump (would cause
  // refetch storms while an agent streams).
  const handleTaskLastMessageChanged = useCallback((event: TaskLastMessageChangedEvent) => {
    setLanes((prev) =>
      prev.map((lane) => ({
        ...lane,
        tasks: lane.tasks.map((t) =>
          t.id === event.taskId
            ? {
                ...t,
                lastAgentMessage: {
                  preview: event.preview,
                  messageType: event.messageType,
                  agentName: event.agentName,
                  createdAt: event.createdAt,
                },
              }
            : t
        ),
      }))
    );
  }, []);

  const handleTaskDeleted = useCallback((event: TaskDeletedEvent) => {
    setLanes((prev) =>
      prev.map((lane) => ({
        ...lane,
        tasks: lane.tasks.filter((t) => t.id !== event.taskId),
      }))
    );
    onStructuralChange?.();
  }, [onStructuralChange]);

  const handleTaskCreated = useCallback((event: TaskCreatedEvent) => {
    setLanes((prev) =>
      prev.map((lane) => {
        if (lane.key !== event.currentLane) return lane;
        if (lane.tasks.some((t) => t.id === event.taskId)) return lane;
        const newTask: TaskSummary = {
          id: event.taskId,
          number: event.number,
          code: event.code,
          title: event.title,
          priority: event.priority as TaskSummary["priority"],
          type: event.type as TaskSummary["type"],
          status: event.status as TaskSummary["status"],
          currentLane: event.currentLane as TaskSummary["currentLane"],
          branchName: null,
          prUrl: null,
          queuePosition: null,
          tags: event.tags ?? [],
          repoId: event.repoId,
          repoName: event.repoName,
          repoColor: event.repoColor,
          assigneeId: null,
          assigneeName: null,
          assigneeAvatarUrl: null,
          isBlocked: false,
          blockedBy: [],
          mergeFailureReason: null,
          hasUnread: true,
          createdAt: event.createdAt,
          lastAgentMessage: null,
        };
        return { ...lane, tasks: [...lane.tasks, newTask] };
      })
    );
    onStructuralChange?.();
  }, [onStructuralChange]);

  useBoardSignalR({
    projectId: projectUlid,
    onLaneChanged: handleLaneChanged,
    onStatusChanged: handleStatusChanged,
    onTaskUpdated: handleTaskUpdated,
    onTaskCreated: handleTaskCreated,
    onTaskDeleted: handleTaskDeleted,
    onTaskMergeFailed: handleTaskMergeFailed,
    onTaskDependenciesChanged: handleTaskDependenciesChanged,
    onTaskLastMessageChanged: handleTaskLastMessageChanged,
    onConnected: reconcile,
  });

  /** Clear a card's unread flag — call when the viewer opens the task. */
  const markRead = useCallback((taskId: string) => {
    setLanes((prev) =>
      prev.map((lane) => ({
        ...lane,
        tasks: lane.tasks.map((t) => (t.id === taskId ? { ...t, hasUnread: false } : t)),
      }))
    );
  }, []);

  return { lanes, setLanes, reconcile, markRead };
}

export { useLiveLanes, normalizeLanes };
