import { useState, useCallback, useEffect, useMemo, useRef, useId } from "react";
import { useSearchParams, useUpdateSearchParams } from "@/lib/navigation";
import {
  DndContext,
  DragOverlay,
  pointerWithin,
  PointerSensor,
  TouchSensor,
  useSensor,
  useSensors,
  type DragStartEvent,
  type DragEndEvent,
} from "@dnd-kit/core";
import type {
  TaskSummary,
  TaskBoardResponse,
  Milestone,
  AgentStatsWindow,
  BoardMetricsResponse,
} from "@/lib/types";
import { LANES, LANE_KEYS } from "@/lib/constants";
import { cn } from "@/lib/utils";
import { LaneColumn } from "./LaneColumn";
import { laneLabel } from "./lane-label";
import { TaskCard } from "./TaskCard";
import { TaskDetailModal } from "./TaskDetailModal";
import { BoardMetricsStrip } from "./BoardMetricsStrip";
import { DoneSummaryCard } from "./DoneSummaryCard";
import { LaneSummaryCard } from "./LaneSummaryCard";
import { BOARD_FILTERS, type BoardFilterKey } from "./board-filters";
import { NO_EPIC_FILTER, distinctEpics, matchesEpicFilter } from "@/lib/external-ticket";
import { useLiveLanes, type WorkspaceBoardScope } from "./use-live-lanes";
import { moveTaskLane } from "@/lib/actions";
import { api } from "@/lib/api";
import { useClientApi } from "@/hooks/use-client-api";
import { usePermissions } from "@/hooks/use-permissions";
import { useBoardMetrics } from "@/hooks/use-board-metrics";
import { FilterChip, Select } from "@/components/ui";
import { StatsWindowSelector } from "@/components/team/StatsWindowSelector";
import { useTranslations } from "use-intl";

/**
 * Columns that get a cost summary pinned above their cards.
 *
 * A blacklist of the two human lanes rather than a whitelist of the agent ones: a lane added later
 * is far more likely to be somewhere agents run than not, and a whitelist would silently leave it
 * bare. Drafting and Ready are where a person writes and queues work — no run ever starts there,
 * so a card would only ever say "nothing".
 */
const UNSUMMARIZED_LANES = new Set<string>([LANE_KEYS.DRAFTING, LANE_KEYS.READY]);

interface TaskBoardProps {
  boardData: TaskBoardResponse;
  /** Route ref used in URLs — the project slug (or a legacy ULID) */
  projectId: string;
  /** Project ULID — SignalR board groups are keyed by it */
  projectUlid: string;
  /**
   * Milestones for the header filter. Grouping is a filter rather than a second board
   * axis on purpose — a milestone × lane grid would break the mobile board, which is
   * the whole "ship from your phone" story.
   */
  milestones?: Milestone[];
  /** Server-rendered metrics for the default window, so the strip has numbers on first paint. */
  initialMetrics?: BoardMetricsResponse | null;
  /**
   * The workspace board: every project of the workspace (or `workspace.project`). Cards carry
   * their project, so the board badges them (when showing every project), opens each in its own
   * project and keeps a card out of a Testing lane its project does not have. Metrics are per
   * project, so the strip and the lane summaries are left out.
   */
  workspace?: WorkspaceBoardScope;
}

function TaskBoard({
  boardData,
  projectId,
  projectUlid,
  milestones = [],
  initialMetrics = null,
  workspace,
}: TaskBoardProps) {
  const isWorkspace = workspace !== undefined;
  const workspaceProject = workspace ? workspace.project : undefined;
  const showProject = isWorkspace && !workspace?.project;
  const dndId = useId();
  const t = useTranslations("dashboard");
  const tCommon = useTranslations("common");
  const { has } = usePermissions();
  const canMoveLane = has("TasksExecute");
  const withToken = useClientApi();
  const [activeTask, setActiveTask] = useState<TaskSummary | null>(null);

  // ── Search / grid mode ──────────────────────────────────────────────
  const [searchInput, setSearchInput] = useState("");
  const [searchQuery, setSearchQuery] = useState(""); // debounced + trimmed
  const [includeMessages, setIncludeMessages] = useState(false);
  const [includeArchived, setIncludeArchived] = useState(false);
  const [rawSearchResults, setSearchResults] = useState<TaskSummary[] | null>(null);
  // Bumped by SignalR handlers so an active search re-runs after board changes
  const [searchVersion, setSearchVersion] = useState(0);
  const searchDebounceRef = useRef<ReturnType<typeof setTimeout>>(undefined);
  const searchActive = searchQuery.length > 0;
  // Which (query, options, version) the current results answer. Loading is derived —
  // "results are for something other than what is being asked" — so neither the reset
  // nor the spinner needs a synchronous setState inside the fetch effect.
  const searchKey = `${searchQuery}\u0000${includeMessages}\u0000${includeArchived}\u0000${searchVersion}`;
  const [settledSearchKey, setSettledSearchKey] = useState<string | null>(null);
  const searchResults = searchActive ? rawSearchResults : null;
  const searchLoading = searchActive && settledSearchKey !== searchKey;

  // ── Live lanes (server snapshot + SignalR deltas) ───────────────────
  const selectedTaskIdRef = useRef<string | null>(null);
  const bumpSearch = useCallback(() => setSearchVersion((v) => v + 1), []);
  const { lanes, setLanes, markRead } = useLiveLanes({
    boardData,
    projectId,
    projectUlid,
    selectedTaskIdRef,
    onStructuralChange: bumpSearch,
    // The same "Include archived" checkbox drives the lanes and the search. Two separate
    // archived toggles on one toolbar would be a puzzle, and a board that hides a card the
    // search below it happily returns reads as a bug.
    includeArchived,
    workspace,
  });

  // ── Milestone filter ────────────────────────────────────────────────
  // "" = all work (the default, and what the board did before milestones existed).
  const [milestoneFilter, setMilestoneFilter] = useState("");

  // Cards name their milestone, so the titles the filter already loaded double as a lookup.
  // Client-side rather than a field on the board DTO: this way a card built from a SignalR
  // delta gets the name too, without the event having to carry it.
  const milestoneNames = useMemo(
    () => Object.fromEntries(milestones.map((m) => [m.id, m.title])),
    [milestones]
  );

  // ── Epic filter ─────────────────────────────────────────────────────
  // Tasks imported from a tracker carry their ticket's epic. "" = all; NO_EPIC_FILTER = the
  // tasks made on the board. Composed with the milestone filter: both narrow what work is in
  // view, and the attention chips then count within that.
  const [epicFilter, setEpicFilter] = useState("");
  const epics = useMemo(() => distinctEpics(lanes.flatMap((lane) => lane.tasks)), [lanes]);

  const matchesMilestone = useCallback(
    (task: TaskSummary) =>
      (milestoneFilter === "" || (task.milestoneId ?? "") === milestoneFilter) &&
      matchesEpicFilter(task, epicFilter),
    [milestoneFilter, epicFilter]
  );

  // ── Metrics ─────────────────────────────────────────────────────────
  const [metricsWindow, setMetricsWindow] = useState<AgentStatsWindow>(
    initialMetrics?.window ?? "7d"
  );
  const { metrics, isLoading: metricsLoading } = useBoardMetrics({
    projectId,
    projectUlid,
    window: metricsWindow,
    initial: initialMetrics,
    enabled: !isWorkspace,
  });

  // ── Attention filters ───────────────────────────────────────────────
  // Additive (OR): turning on two chips asks for cards matching either, because "show me
  // everything that is wrong" is the actual question. They narrow the lanes in place rather
  // than switching to the flat grid — where a problem sits in the pipeline is half the answer.
  const [activeFilters, setActiveFilters] = useState<ReadonlySet<BoardFilterKey>>(new Set());
  const toggleFilter = useCallback((key: BoardFilterKey) => {
    setActiveFilters((prev) => {
      const next = new Set(prev);
      if (!next.delete(key)) next.add(key);
      return next;
    });
  }, []);

  const matchesFilters = useCallback(
    (task: TaskSummary) =>
      activeFilters.size === 0 ||
      BOARD_FILTERS.some((f) => activeFilters.has(f.key) && f.match(task)),
    [activeFilters]
  );

  // Counts describe the milestone-filtered board, not the whole project: a chip that offers 4
  // when the visible board can only ever show 1 sends you looking for three cards that are not
  // there. Counted before the chips themselves apply, so a chip never counts only itself.
  const milestoneLanes = useMemo(
    () =>
      milestoneFilter === "" && epicFilter === ""
        ? lanes
        : lanes.map((lane) => ({ ...lane, tasks: lane.tasks.filter(matchesMilestone) })),
    [lanes, milestoneFilter, epicFilter, matchesMilestone]
  );

  const filterCounts = useMemo(() => {
    const all = milestoneLanes.flatMap((lane) => lane.tasks);
    return Object.fromEntries(
      BOARD_FILTERS.map((f) => [f.key, all.filter(f.match).length])
    ) as Record<BoardFilterKey, number>;
  }, [milestoneLanes]);

  // Filtered for display only — `lanes` stays the full set so SignalR updates and
  // drag-and-drop keep working against tasks the filter is currently hiding.
  const displayLanes = useMemo(
    () =>
      activeFilters.size === 0
        ? milestoneLanes
        : milestoneLanes.map((lane) => ({ ...lane, tasks: lane.tasks.filter(matchesFilters) })),
    [milestoneLanes, activeFilters, matchesFilters]
  );

  const shownCount = useMemo(
    () => displayLanes.reduce((sum, lane) => sum + lane.tasks.length, 0),
    [displayLanes]
  );
  const totalCount = useMemo(
    () => lanes.reduce((sum, lane) => sum + lane.tasks.length, 0),
    [lanes]
  );

  const displaySearchResults = useMemo(
    () => (searchResults ?? []).filter(matchesMilestone),
    [searchResults, matchesMilestone]
  );

  useEffect(() => {
    if (searchDebounceRef.current) clearTimeout(searchDebounceRef.current);
    searchDebounceRef.current = setTimeout(() => {
      setSearchQuery(searchInput.trim());
    }, 300);
    return () => {
      if (searchDebounceRef.current) clearTimeout(searchDebounceRef.current);
    };
  }, [searchInput]);

  useEffect(() => {
    if (!searchQuery) return;
    let cancelled = false;
    withToken((token) =>
      workspaceProject !== undefined
        ? api.board.searchWorkspace(workspaceProject, searchQuery, { includeMessages, includeArchived }, token)
        : api.board.search(projectId, searchQuery, { includeMessages, includeArchived }, token)
    )
      .then((results) => {
        if (!cancelled) setSearchResults(results);
      })
      .catch(() => {
        if (!cancelled) setSearchResults([]);
      })
      .finally(() => {
        if (!cancelled) setSettledSearchKey(searchKey);
      });
    return () => {
      cancelled = true;
    };
  }, [searchQuery, includeMessages, includeArchived, searchKey, projectId, workspaceProject, withToken]);

  // The open task lives in the ?task= search param (friendly number or id). Only the query
  // changes, so the board stays mounted and keeps its SignalR-applied state — changing the
  // pathname (as the old /tasks/{n} swap did) would remount it.
  const searchParams = useSearchParams();
  const updateSearchParams = useUpdateSearchParams();
  const taskParam = searchParams.get("task");
  // A number names a task only within one project; the workspace board spans several, so it
  // names a task by its code (SHOP-12) instead.
  const selectedTask = useMemo(() => {
    if (!taskParam) return null;
    const matches = (t: TaskSummary) =>
      t.id === taskParam || (isWorkspace ? t.code === taskParam : String(t.number) === taskParam);
    for (const lane of lanes) {
      const match = lane.tasks.find(matches);
      if (match) return match;
    }
    // Grid results can include tasks absent from the lanes (e.g. archived)
    return (searchResults ?? []).find(matches) ?? null;
  }, [taskParam, lanes, searchResults, isWorkspace]);
  const selectedTaskId = selectedTask?.id ?? null;
  // The task detail loads its project's repos, milestones and agents, and links to its page.
  const selectedTaskProject = isWorkspace ? (selectedTask?.project?.slug ?? projectId) : projectId;

  // Ongoing tasks (queued or agent-active) in board order — powers the modal's
  // prev/next carousel so the user can flip through live work without closing it.
  const activeTaskIds = useMemo(
    () =>
      lanes.flatMap((lane) =>
        lane.tasks
          .filter((t) => t.status === "InQueue" || t.status === "Active")
          .map((t) => t.id)
      ),
    [lanes]
  );

  // Keep ref in sync for use in SignalR callbacks
  useEffect(() => {
    selectedTaskIdRef.current = selectedTaskId;
  }, [selectedTaskId]);

  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 8 } }),
    useSensor(TouchSensor, { activationConstraint: { delay: 200, tolerance: 5 } })
  );

  // ── Task modal ───────────────────────────────────────────────────────
  const handleTaskClick = useCallback((taskId: string) => {
    const task =
      lanes.flatMap((l) => l.tasks).find((t) => t.id === taskId) ??
      (searchResults ?? []).find((t) => t.id === taskId);
    markRead(taskId);
    // Prefer the friendly task number (the code on the workspace board) in the URL.
    const ref = task ? (isWorkspace ? task.code : String(task.number)) : taskId;
    updateSearchParams((p) => p.set("task", ref));
  }, [lanes, searchResults, markRead, updateSearchParams, isWorkspace]);

  const handleModalClose = useCallback(() => {
    updateSearchParams((p) => p.delete("task"));
  }, [updateSearchParams]);

  // ── Drag and drop ──────────────────────────────────────────────────
  const handleDragStart = useCallback((event: DragStartEvent) => {
    const task = event.active.data.current?.task as TaskSummary | undefined;
    if (task) setActiveTask(task);
  }, []);

  const handleDragEnd = useCallback(
    async (event: DragEndEvent) => {
      setActiveTask(null);
      // Moving lanes is a mutation — permission-gated (drag simply snaps back)
      if (!canMoveLane) return;
      const { active, over } = event;
      if (!over) return;

      const taskId = active.id as string;
      const targetLane = over.id as string;

      // Find which lane the task is currently in
      let sourceLane: string | null = null;
      for (const lane of lanes) {
        if (lane.tasks.some((t) => t.id === taskId)) {
          sourceLane = lane.key;
          break;
        }
      }

      if (!sourceLane || sourceLane === targetLane) return;

      // Only allow backward moves — forward moves are system-driven
      const sourceIndex = LANES.findIndex((l) => l.key === sourceLane);
      const targetIndex = LANES.findIndex((l) => l.key === targetLane);
      if (sourceIndex === -1 || targetIndex === -1 || targetIndex >= sourceIndex) return;

      // Optimistic update — move task between lanes
      const taskToMove = lanes
        .find((l) => l.key === sourceLane)
        ?.tasks.find((t) => t.id === taskId);
      if (!taskToMove) return;
      // On the workspace board the Testing lane is there when ANY project tests; a card whose
      // own project has no Testing lane cannot go into it.
      if (targetLane === LANE_KEYS.TESTING && taskToMove.project && !taskToMove.project.hasTesting) return;

      setLanes((prev) =>
        prev.map((lane) => {
          if (lane.key === sourceLane) {
            return { ...lane, tasks: lane.tasks.filter((t) => t.id !== taskId) };
          }
          if (lane.key === targetLane) {
            return { ...lane, tasks: [...lane.tasks, { ...taskToMove, currentLane: targetLane as TaskSummary["currentLane"] }] };
          }
          return lane;
        })
      );

      try {
        await moveTaskLane(taskId, targetLane);
      } catch {
        // Revert on error
        setLanes((prev) =>
          prev.map((lane) => {
            if (lane.key === targetLane) {
              return { ...lane, tasks: lane.tasks.filter((t) => t.id !== taskId) };
            }
            if (lane.key === sourceLane) {
              return { ...lane, tasks: [...lane.tasks, taskToMove] };
            }
            return lane;
          })
        );
      }
    },
    [lanes, setLanes, canMoveLane]
  );

  return (
    <>
      <DndContext
        id={dndId}
        sensors={sensors}
        collisionDetection={pointerWithin}
        onDragStart={handleDragStart}
        onDragEnd={handleDragEnd}
      >
        {/* How the project is moving. Above the filters because it describes the whole board,
            not the slice the filters are about to select. */}
        {!isWorkspace && <BoardMetricsStrip metrics={metrics} isLoading={metricsLoading} />}

        {/* Row 1 — scope and controls. Search, what work, what window. */}
        <div className="flex flex-wrap items-center gap-3 px-4 pb-2 lg:px-6">
          <div className="relative flex-1 min-w-[200px] max-w-md">
            <svg
              width="14"
              height="14"
              viewBox="0 0 16 16"
              fill="none"
              stroke="currentColor"
              strokeWidth="1.5"
              className="absolute left-3 top-1/2 -translate-y-1/2 text-gray-400"
            >
              <circle cx="7" cy="7" r="4.5" />
              <path d="M10.5 10.5L14 14" strokeLinecap="round" />
            </svg>
            <input
              value={searchInput}
              onChange={(e) => setSearchInput(e.target.value)}
              placeholder={t("boardSearch.placeholder")}
              className={cn(
                "w-full rounded-lg pl-9 pr-3 py-1.5 text-xs text-gray-300",
                "bg-white/[0.04] border border-white/[0.06]",
                "placeholder:text-gray-500",
                "focus:border-primary-500/50 focus:outline-none"
              )}
            />
          </div>
          <label className="flex items-center gap-1.5 cursor-pointer text-xs text-gray-400">
            <input
              type="checkbox"
              checked={includeMessages}
              onChange={(e) => setIncludeMessages(e.target.checked)}
              className="h-3.5 w-3.5 rounded border-white/[0.1] bg-white/[0.04] accent-primary-500"
            />
            {t("boardSearch.searchInMessages")}
          </label>
          <label className="flex items-center gap-1.5 cursor-pointer text-xs text-gray-400">
            <input
              type="checkbox"
              checked={includeArchived}
              onChange={(e) => setIncludeArchived(e.target.checked)}
              className="h-3.5 w-3.5 rounded border-white/[0.1] bg-white/[0.04] accent-primary-500"
            />
            {t("boardSearch.includeArchived")}
          </label>
          {milestones.length > 0 && (
            <div className="shrink-0">
              <Select
                size="sm"
                value={milestoneFilter}
                onChange={(e) => setMilestoneFilter(e.target.value)}
                aria-label={t("boardPage.milestoneFilter")}
                // Milestone titles are full sentences, so an auto-sized select widens to the
                // longest one and swallows the toolbar. Cap it and let the text ellipsize.
                className="max-w-[220px]"
              >
                <option value="">{t("boardPage.allWork")}</option>
                {milestones.map((m) => (
                  <option key={m.id} value={m.id}>
                    {m.title}
                  </option>
                ))}
              </Select>
            </div>
          )}
          {(epics.length > 0 || epicFilter !== "") && (
            <div className="shrink-0">
              <Select
                size="sm"
                value={epicFilter}
                onChange={(e) => setEpicFilter(e.target.value)}
                aria-label={t("boardPage.epicFilter")}
                className="max-w-[220px]"
                data-testid="board-epic-filter"
              >
                <option value="">{t("boardPage.allEpics")}</option>
                {epics.map((epic) => (
                  <option key={epic} value={epic}>
                    {epic}
                  </option>
                ))}
                <option value={NO_EPIC_FILTER}>{t("boardPage.notFromTracker")}</option>
              </Select>
            </div>
          )}
          {!isWorkspace && (
            <StatsWindowSelector
              value={metricsWindow}
              onChange={setMetricsWindow}
              className="shrink-0"
            />
          )}
        </div>

        {/* Row 2 — what is wrong. Separate from row 1 because these are not another way to
            narrow the board, they are the reasons someone opened it. */}
        {!searchActive && (
          <div className="flex flex-wrap items-center gap-1.5 px-4 pb-3 lg:px-6">
            {BOARD_FILTERS.map((f) => (
              <FilterChip
                key={f.key}
                label={t(`boardFilters.${f.key}` as `boardFilters.${BoardFilterKey}`)}
                count={filterCounts[f.key]}
                active={activeFilters.has(f.key)}
                color={f.color}
                onToggle={() => toggleFilter(f.key)}
              />
            ))}
            <span className="ml-auto text-xs text-gray-400">
              {t("boardFilters.showing", { shown: shownCount, total: totalCount })}
            </span>
          </div>
        )}

        {searchActive ? (
          searchResults !== null && displaySearchResults.length === 0 && !searchLoading ? (
            <div className="flex flex-1 items-center justify-center pb-4 px-4 lg:px-6">
              <p className="text-sm text-gray-400">{t("boardSearch.noResults")}</p>
            </div>
          ) : (
            <div
              className={cn(
                "grid flex-1 auto-rows-min grid-cols-1 gap-3 content-start overflow-y-auto pb-4 px-4 lg:px-6",
                "sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4",
                // Previous results stay visible (dimmed) while a refinement is in flight
                searchLoading && "opacity-60"
              )}
            >
              {displaySearchResults.map((task) => (
                <TaskCard
                  key={task.id}
                  task={task}
                  onTaskClick={handleTaskClick}
                  dragDisabled
                  milestoneName={task.milestoneId ? milestoneNames[task.milestoneId] : null}
                  showProject={showProject}
                />
              ))}
            </div>
          )
        ) : (
          <div
            className="flex flex-1 min-h-0 gap-3 overflow-x-auto pb-4 px-4 lg:px-6 snap-x snap-mandatory md:snap-none"
            style={{ scrollbarWidth: "thin" }}
          >
            {displayLanes.map((lane) => (
              <div key={lane.key} className="shrink-0 grow max-w-[340px]">
                <LaneColumn
                  laneKey={lane.key}
                  label={laneLabel(lane.key, tCommon, t, lane.label)}
                  tasks={lane.tasks}
                  onTaskClick={handleTaskClick}
                  activeDragLane={activeTask?.currentLane ?? null}
                  milestoneNames={milestoneNames}
                  metric={isWorkspace ? undefined : metrics?.lanes[lane.key]}
                  showProject={showProject}
                  activeDragAllowsTesting={activeTask?.project?.hasTesting ?? true}
                  pinned={
                    isWorkspace ? undefined :
                    // Done summarizes the outcome (what shipped, what it cost); every other
                    // agent lane summarizes its own column. Drafting and Ready are the human end
                    // of the board — nothing runs there, so there is nothing to summarize.
                    lane.key === LANE_KEYS.DONE ? (
                      <DoneSummaryCard metrics={metrics} />
                    ) : !UNSUMMARIZED_LANES.has(lane.key) ? (
                      <LaneSummaryCard metric={metrics?.lanes[lane.key]} />
                    ) : undefined
                  }
                />
              </div>
            ))}
          </div>
        )}

        {/* Drag overlay */}
        <DragOverlay>
          {activeTask && (
            <div className="opacity-90">
              <TaskCard
                task={activeTask}
                milestoneName={activeTask.milestoneId ? milestoneNames[activeTask.milestoneId] : null}
                showProject={showProject}
                atRest
              />
            </div>
          )}
        </DragOverlay>
      </DndContext>

      {/* Task detail modal */}
      <TaskDetailModal
        taskId={selectedTaskId}
        projectId={selectedTaskProject}
        onClose={handleModalClose}
        activeTaskIds={activeTaskIds}
        onNavigateToTask={handleTaskClick}
      />
    </>
  );
}

export { TaskBoard };
