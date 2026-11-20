import { useDroppable } from "@dnd-kit/core";
import { AnimatePresence } from "framer-motion";
import { useTranslations } from "use-intl";
import { cn } from "@/lib/utils";
import { LANES, LANE_KEYS, LANE_MAP } from "@/lib/constants";
import { Term } from "@/components/ui/Term";
import { Tooltip } from "@/components/ui/Tooltip";
import { LANE_GLOSSARY } from "@/components/ui/glossary";
import { formatDuration } from "@/components/team/AgentOpsStats";
import { TaskCard } from "./TaskCard";
import { needsYou } from "./board-filters";
import type { BoardLaneMetric, TaskSummary } from "@/lib/types";

interface LaneColumnProps {
  laneKey: string;
  label: string;
  tasks: TaskSummary[];
  onTaskClick?: (taskId: string) => void;
  /** The lane key of the task currently being dragged, or null if no drag active */
  activeDragLane?: string | null;
  /** Pinned above the cards — the Done lane's summary. Scrolls with the column, by design:
   * it is a footnote on the work below it, not a second header. */
  pinned?: React.ReactNode;
  /** Milestone id → title, so each card can name the ship it belongs to */
  milestoneNames?: Record<string, string>;
  /** Agent time spent in this lane over the metrics window. Absent for lanes no agent runs in. */
  metric?: BoardLaneMetric;
  /** False while dragging a card whose project has no Testing lane — on the workspace board the
   * lane is there because another project tests, and is no drop target for this card. */
  activeDragAllowsTesting?: boolean;
  /** Cards name their project — the workspace board showing every project. */
  showProject?: boolean;
  /** The drop target's id when one lane shows more than once (a column per swimlane); drops
   * still name the lane through the droppable's data. Defaults to the lane key. */
  droppableId?: string;
  /** Leave the header out — swimlanes put one header row above every story's columns. */
  hideHeader?: boolean;
  /** Cards leave out their story chip (they sit in their story's swimlane). */
  hideStoryChip?: boolean;
  /** No minimum height: a swimlane row is as tall as its cards. */
  compact?: boolean;
}

function LaneColumn({
  laneKey,
  label,
  tasks,
  onTaskClick,
  activeDragLane,
  pinned,
  milestoneNames,
  metric,
  activeDragAllowsTesting = true,
  showProject = false,
  droppableId,
  hideHeader = false,
  hideStoryChip = false,
  compact = false,
}: LaneColumnProps) {
  const t = useTranslations("dashboard.boardMetrics");
  const { setNodeRef, isOver } = useDroppable({ id: droppableId ?? laneKey, data: { laneKey } });
  const lane = LANE_MAP[laneKey as keyof typeof LANE_MAP];
  const color = lane?.color ?? "var(--color-lane-drafting)";
  // Lane names are configurable per project, but the keys are fixed — so the hover
  // explaining what happens in this column stays right even when the label is custom.
  const glossaryKey = LANE_GLOSSARY[laneKey];
  const isDone = laneKey === LANE_KEYS.DONE;

  // A lane no agent runs in has no agent time to report, so it reports what it does have: how
  // much of the work parked here is waiting on a person. Showing "0m" instead would read as
  // "instant" rather than "not a thing agents do here".
  const waitingOnYou = metric ? 0 : tasks.filter(needsYou).length;

  // Determine if this lane is a valid backward drop target
  const isValidTarget = (() => {
    if (!activeDragLane) return false;
    if (laneKey === LANE_KEYS.TESTING && !activeDragAllowsTesting) return false;
    const sourceIndex = LANES.findIndex((l) => l.key === activeDragLane);
    const thisIndex = LANES.findIndex((l) => l.key === laneKey);
    return thisIndex !== -1 && sourceIndex !== -1 && thisIndex < sourceIndex;
  })();
  const isDragging = activeDragLane !== null && activeDragLane !== undefined;
  const showDropHighlight = isOver && isValidTarget;

  return (
    <div
      className={cn(
        "flex h-full min-w-[280px] flex-col rounded-2xl md:min-w-[200px] lg:min-w-[210px]",
        "snap-start scroll-ml-4 md:snap-align-none",
      )}
    >
      {/* Lane header */}
      {!hideHeader && (
      <div className="mb-3 flex items-center gap-2 px-1">
        <span
          className="h-2.5 w-2.5 rounded-full shrink-0"
          style={{
            backgroundColor: color,
            boxShadow: `0 0 8px ${color}50`,
          }}
        />
        <span className="text-label">
          {glossaryKey ? <Term k={glossaryKey}>{label}</Term> : label}
        </span>
        <span className="ml-auto text-xs text-gray-400">
          {tasks.length}
        </span>
        {metric && (
          // Named agent time, not dwell time — the column cannot say how long cards sit here,
          // because nothing records that. The tooltip says so rather than letting the number
          // be read as the more obvious meaning.
          <Tooltip content={t("laneMetricTooltip", { runs: metric.runs })}>
            <span className="shrink-0 text-xs text-gray-400 tabular-nums">
              {t("laneAvg", { duration: formatDuration(metric.avgAgentSeconds) })}
            </span>
          </Tooltip>
        )}
        {waitingOnYou > 0 && (
          <span className="shrink-0 rounded-full border border-amber-500/20 bg-amber-500/10 px-1.5 py-0.5 text-xs font-medium text-amber-400 tabular-nums">
            {t("waitingOnYou", { count: waitingOnYou })}
          </span>
        )}
      </div>
      )}

      {/* Drop zone */}
      <div
        ref={setNodeRef}
        className={cn(
          "flex-1 space-y-2 overflow-y-auto rounded-xl p-2",
          compact ? "min-h-[56px]" : "min-h-[120px]",
          "border border-transparent transition-all duration-200",
          showDropHighlight && "border-dashed bg-white/[0.02]",
          isDragging && !isValidTarget && "opacity-40",
        )}
        style={{
          scrollbarWidth: "thin",
          // The terminal column reads as a different kind of place: work that has landed, not
          // work in flight. A wash of its own lane colour is enough to say so without adding
          // any chrome the other columns do not have.
          backgroundColor: isDone && !showDropHighlight ? `${color}0a` : undefined,
          borderColor: showDropHighlight ? `${color}40` : isDone ? `${color}22` : undefined,
          boxShadow: showDropHighlight ? `inset 0 0 30px ${color}08` : undefined,
        }}
      >
        {pinned}
        {/* A task changing lane unmounts here and mounts in the destination, so
            this is what turns an agent-driven move into a visible one: the card
            eases out of this column while the cards below it close the gap.
            `popLayout` takes the exiting card out of flow immediately, so that
            gap closes during the exit rather than after it. */}
        <AnimatePresence mode="popLayout" initial={false}>
          {tasks.map((task) => (
            <TaskCard
              key={task.id}
              task={task}
              onTaskClick={onTaskClick}
              milestoneName={task.milestoneId ? milestoneNames?.[task.milestoneId] : null}
              showProject={showProject}
              hideStoryChip={hideStoryChip}
            />
          ))}
        </AnimatePresence>
      </div>
    </div>
  );
}

export { LaneColumn };
