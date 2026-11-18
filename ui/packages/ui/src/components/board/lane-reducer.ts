import type { TaskSummary, BoardLaneResponse } from "@/lib/types";
import type { LaneChangedEvent } from "@/hooks/use-board-signalr";

/**
 * Applies a LaneChanged event to the board's lane state.
 *
 * Split out of TaskBoard so the tricky cases stay unit-testable — the board
 * drops cards silently when this gets them wrong, and a card that vanishes only
 * comes back on a hard refresh.
 *
 * Two cases the naive "filter from, append to" pass gets wrong:
 *  - `fromLane === toLane`: the server re-queues a task *in place* (Retry, and
 *    the agent claiming a task that plan approval already parked in Developing).
 *    A single map() pass matches the from-branch first and returns early, so the
 *    card is removed and never re-added.
 *  - `toLane` is not on the board: the Testing lane only exists when the project
 *    has QA or manual verification enabled. Removing without a destination would
 *    drop the card; leaving it where it is at least keeps it reachable.
 */
function applyLaneChange(
  lanes: BoardLaneResponse[],
  event: LaneChangedEvent,
  /** Task currently open in the modal — it stays read, everything else lights up */
  selectedTaskId: string | null
): BoardLaneResponse[] {
  const status = event.status as TaskSummary["status"];

  // Re-queued in place — treat it as the status change it actually is
  if (event.fromLane === event.toLane) {
    return lanes.map((lane) =>
      lane.key === event.toLane
        ? {
            ...lane,
            tasks: lane.tasks.map((t) =>
              t.id === event.taskId
                ? {
                    ...t,
                    status,
                    hasUnread: t.id !== selectedTaskId ? true : t.hasUnread,
                  }
                : t
            ),
          }
        : lane
    );
  }

  // Destination isn't rendered on this board — keep the card where it is
  if (!lanes.some((lane) => lane.key === event.toLane)) return lanes;

  // Find the task in existing lanes so we don't depend on the event carrying the full object
  let movedTask: TaskSummary | undefined;
  for (const lane of lanes) {
    movedTask = lane.tasks.find((t) => t.id === event.taskId);
    if (movedTask) break;
  }

  // Not on this board — filtered out, or the board loaded before the task existed. There is
  // nothing to move, and the removal below would still have run: the card would be deleted
  // from `fromLane` and never added to `toLane`, which is exactly the silent drop this file
  // exists to prevent. Reconciliation on reconnect is what brings an unknown task in.
  if (!movedTask) return lanes;

  return lanes.map((lane) => {
    if (lane.key === event.fromLane) {
      return { ...lane, tasks: lane.tasks.filter((t) => t.id !== event.taskId) };
    }
    if (lane.key === event.toLane) {
      const exists = lane.tasks.some((t) => t.id === event.taskId);
      if (exists) return lane;
      return {
        ...lane,
        tasks: [
          ...lane.tasks,
          {
            ...movedTask,
            currentLane: event.toLane as TaskSummary["currentLane"],
            status,
            hasUnread: event.taskId !== selectedTaskId,
          },
        ],
      };
    }
    return lane;
  });
}

export { applyLaneChange };
