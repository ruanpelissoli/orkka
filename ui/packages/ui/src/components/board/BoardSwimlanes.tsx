import { useCallback, useEffect, useState } from "react";
import { useTranslations } from "use-intl";
import { cn } from "@/lib/utils";
import { LANE_MAP } from "@/lib/constants";
import type { TaskSummary } from "@/lib/types";
import { LaneColumn } from "./LaneColumn";
import { storyColor } from "./TaskCard";
import { NO_STORY_SWIMLANE, storyPrState, type StorySwimlane } from "./story-swimlanes";

interface BoardSwimlanesProps {
  swimlanes: StorySwimlane[];
  /** The column headers, in board order — shown once above every swimlane. */
  columns: { key: string; label: string }[];
  onTaskClick?: (taskId: string) => void;
  activeTask: TaskSummary | null;
  milestoneNames: Record<string, string>;
  showProject: boolean;
  /** localStorage key for which swimlanes are collapsed (per board). */
  storageKey: string;
}

const COLUMN = "w-[240px] shrink-0";

function readCollapsed(key: string): Set<string> {
  if (typeof window === "undefined") return new Set();
  try {
    const raw = window.localStorage.getItem(key);
    const parsed: unknown = raw ? JSON.parse(raw) : [];
    return new Set(Array.isArray(parsed) ? parsed.filter((v): v is string => typeof v === "string") : []);
  } catch {
    return new Set();
  }
}

/**
 * The board grouped by Story: one collapsible row per story, its sub-tasks spread over the same
 * columns as the board in story order, then a row for everything that belongs to no story. A
 * story is one feature split into sub-tasks that build on each other on one branch — the row
 * makes "where is this feature" one glance instead of a hunt for matching chips.
 *
 * Each row's column is its own drop target (`{swimlane}:{lane}`) carrying the lane key, so a
 * card still moves back between columns inside its row; a row is not a drop target — which
 * story a card belongs to comes from the tracker.
 */
function BoardSwimlanes({
  swimlanes,
  columns,
  onTaskClick,
  activeTask,
  milestoneNames,
  showProject,
  storageKey,
}: BoardSwimlanesProps) {
  const t = useTranslations("dashboard.boardPage");
  const [collapsed, setCollapsed] = useState<Set<string>>(() => readCollapsed(storageKey));

  useEffect(() => {
    window.localStorage.setItem(storageKey, JSON.stringify([...collapsed]));
  }, [collapsed, storageKey]);

  const toggle = useCallback((id: string) => {
    setCollapsed((prev) => {
      const next = new Set(prev);
      if (!next.delete(id)) next.add(id);
      return next;
    });
  }, []);

  return (
    <div
      className="flex-1 min-h-0 overflow-auto pb-4 px-4 lg:px-6"
      style={{ scrollbarWidth: "thin" }}
      data-testid="board-swimlanes"
    >
      <div className="inline-flex min-w-full flex-col gap-3">
        {/* Column headers once, above every row. */}
        <div className="sticky top-0 z-10 flex gap-3 bg-gray-950/95 pb-1 backdrop-blur">
          {columns.map((c) => {
            const color = LANE_MAP[c.key as keyof typeof LANE_MAP]?.color ?? "var(--color-lane-drafting)";
            return (
              <div key={c.key} className={cn(COLUMN, "flex items-center gap-2 px-1")}>
                <span className="h-2.5 w-2.5 shrink-0 rounded-full" style={{ backgroundColor: color }} />
                <span className="text-label">{c.label}</span>
              </div>
            );
          })}
        </div>

        {swimlanes.map((row) => {
          const isStory = row.id !== NO_STORY_SWIMLANE;
          const name = isStory ? (row.storyKey ?? row.story?.title ?? "") : t("noStory");
          const open = !collapsed.has(row.id);
          return (
            <section
              key={row.id}
              className="rounded-2xl border border-white/[0.06] bg-white/[0.01]"
              style={isStory ? { borderLeft: `3px solid ${storyColor(row.id)}` } : undefined}
              data-testid="board-swimlane"
              data-swimlane={row.id}
            >
              <SwimlaneHeader row={row} name={name} open={open} onToggle={() => toggle(row.id)} />
              {open && (
                <div className="flex gap-3 px-2 pb-2">
                  {row.lanes.map((lane) => (
                    <div key={lane.key} className={COLUMN}>
                      <LaneColumn
                        laneKey={lane.key}
                        label={lane.label}
                        tasks={lane.tasks}
                        onTaskClick={onTaskClick}
                        activeDragLane={activeTask?.currentLane ?? null}
                        milestoneNames={milestoneNames}
                        showProject={showProject}
                        activeDragAllowsTesting={activeTask?.project?.hasTesting ?? true}
                        droppableId={`${row.id}:${lane.key}`}
                        hideHeader
                        hideStoryChip={isStory}
                        compact
                      />
                    </div>
                  ))}
                </div>
              )}
            </section>
          );
        })}
      </div>
    </div>
  );
}

function SwimlaneHeader({
  row,
  name,
  open,
  onToggle,
}: {
  row: StorySwimlane;
  name: string;
  open: boolean;
  onToggle: () => void;
}) {
  const t = useTranslations("dashboard.boardPage");
  const isStory = row.id !== NO_STORY_SWIMLANE;
  const story = row.story;
  const pr = storyPrState(story);
  const prUrl = story?.pullRequests[0]?.url;
  const prLabel = {
    none: t("storyNoPr"),
    draft: t("storyPrDraft"),
    ready: t("storyPrReady"),
    merged: t("storyPrMerged"),
    closed: t("storyPrClosed"),
  }[pr];

  return (
    <div className="flex flex-wrap items-center gap-2 px-3 py-2">
      <button
        type="button"
        onClick={onToggle}
        aria-expanded={open}
        aria-label={open ? t("collapseStory", { name }) : t("expandStory", { name })}
        className="flex min-w-0 items-center gap-2 text-left"
        data-testid="swimlane-toggle"
      >
        <svg
          width="12"
          height="12"
          viewBox="0 0 16 16"
          fill="none"
          stroke="currentColor"
          strokeWidth="1.5"
          className={cn("shrink-0 text-gray-400 transition-transform", open && "rotate-90")}
          aria-hidden
        >
          <path d="M6 4l4 4-4 4" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
        {isStory && row.storyKey && (
          <span className="font-mono text-xs" style={{ color: storyColor(row.id) }}>
            {row.storyKey}
          </span>
        )}
        <span className="truncate text-sm font-medium text-gray-200">
          {isStory ? (story?.title ?? "") : name}
        </span>
      </button>
      <span className="text-xs text-gray-400 tabular-nums" data-testid="swimlane-count">
        {isStory && story
          ? t("storyProgress", { done: story.doneCount, total: story.memberCount })
          : row.count}
      </span>
      {isStory && story && (
        prUrl ? (
          <a
            href={prUrl}
            target="_blank"
            rel="noreferrer"
            className="rounded-full border border-white/[0.08] px-1.5 py-0.5 text-label text-gray-300 hover:text-white"
            data-testid="swimlane-pr"
          >
            {prLabel}
          </a>
        ) : (
          <span className="rounded-full border border-white/[0.06] px-1.5 py-0.5 text-label text-gray-400" data-testid="swimlane-pr">
            {prLabel}
          </span>
        )
      )}
      {isStory && story?.url && row.storyKey && (
        <a
          href={story.url}
          target="_blank"
          rel="noreferrer"
          className="ml-auto text-xs text-gray-400 hover:text-gray-200"
          aria-label={t("storyOpenTicket", { key: row.storyKey })}
          title={t("storyOpenTicket", { key: row.storyKey })}
        >
          <svg width="12" height="12" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" aria-hidden>
            <path d="M9 3h4v4M13 3L7 9M11 9v4H3V5h4" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
        </a>
      )}
    </div>
  );
}

export { BoardSwimlanes };
