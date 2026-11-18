import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useSearchParams, useUpdateSearchParams } from "@/lib/navigation";
import { useTranslations } from "use-intl";
import { cn } from "@/lib/utils";
import { SplitPane } from "@/components/ui";
import { useLiveLanes } from "@/components/board/use-live-lanes";
import { useIsDesktop } from "@/hooks/use-media-query";
import type { StreamEvent } from "@/hooks/use-task-signalr";
import type { Task, TaskBoardResponse } from "@/lib/types";
import { TaskRail } from "./TaskRail";
import { CodeViewTaskPanel } from "./CodeViewTaskPanel";
import { CodePane } from "./CodePane";
import { useLiveWorkspace } from "./use-live-workspace";
import { buildRailModel } from "./rail-model";

interface CodeViewProps {
  boardData: TaskBoardResponse;
  /** Route ref used in URLs — the project slug (or a legacy ULID) */
  projectId: string;
  /** Project ULID — SignalR board groups are keyed by it */
  projectUlid: string;
}

type MobileTab = "tasks" | "chat" | "code";

/**
 * The board as a workbench: in-flight tasks on the left, the selected task pinned with its
 * conversation beneath, and the code the agent is changing on the right. Shares the live lane
 * model with the kanban (`useLiveLanes`) and the `?task=` selection contract, so switching
 * views never loses where you were.
 */
function CodeView({ boardData, projectId, projectUlid }: CodeViewProps) {
  const t = useTranslations("dashboard.codeView");
  const isDesktop = useIsDesktop();
  // Phone/tablet: which of the three panes is showing.
  const [mobileTab, setMobileTab] = useState<MobileTab>("tasks");

  // ── Live lanes + selection ──────────────────────────────────────────
  const selectedTaskIdRef = useRef<string | null>(null);
  const { lanes, markRead } = useLiveLanes({ boardData, projectId, projectUlid, selectedTaskIdRef });

  const searchParams = useSearchParams();
  const setParams = useUpdateSearchParams();
  const taskParam = searchParams.get("task");
  const fileParam = searchParams.get("file");

  const selectedSummary = useMemo(() => {
    if (!taskParam) return null;
    for (const lane of lanes) {
      const match = lane.tasks.find((x) => x.id === taskParam || String(x.number) === taskParam);
      if (match) return match;
    }
    return null;
  }, [taskParam, lanes]);
  const selectedTaskId = selectedSummary?.id ?? null;

  useEffect(() => {
    selectedTaskIdRef.current = selectedTaskId;
  }, [selectedTaskId]);

  const selectTask = useCallback(
    (taskId: string) => {
      const number = lanes.flatMap((l) => l.tasks).find((x) => x.id === taskId)?.number;
      markRead(taskId);
      setParams((p) => {
        p.set("task", String(number ?? taskId));
        p.delete("file");
      });
      setMobileTab("chat");
    },
    [lanes, markRead, setParams]
  );

  const deselectTask = useCallback(() => {
    setParams((p) => {
      p.delete("task");
      p.delete("file");
    });
    setMobileTab("tasks");
  }, [setParams]);

  const selectPath = useCallback((path: string | null) => {
    setParams((p) => {
      if (path) p.set("file", path);
      else p.delete("file");
    });
  }, [setParams]);

  // ── Selected task: loaded detail + live mirror ──────────────────────
  const [loadedTask, setLoadedTask] = useState<Task | null>(null);
  const [liveTask, setLiveTask] = useState<Task | null>(null);
  const task = liveTask && liveTask.id === selectedTaskId ? liveTask : loadedTask && loadedTask.id === selectedTaskId ? loadedTask : null;

  const [agentActive, setAgentActive] = useState(false);
  const [agentName, setAgentName] = useState<string | null>(null);

  const handleTaskLoaded = useCallback((loaded: Task | null) => {
    setLoadedTask(loaded);
  }, []);
  const handleLiveTaskChange = useCallback((live: Task) => setLiveTask(live), []);
  const handleAgentActiveChange = useCallback((active: boolean) => setAgentActive(active), []);

  // ── Workspace model (baseline + live tool effects) ──────────────────
  const laneForBaseline = task?.currentLane ?? selectedSummary?.currentLane ?? "";
  const { model, totals, loadState, ingest, applyWorkspaceChanged } = useLiveWorkspace({
    taskId: selectedTaskId,
    prUrl: task?.prUrl ?? selectedSummary?.prUrl ?? null,
    // A lane change (Developing → Testing) is when snapshots get captured — re-baseline.
    baselineKey: laneForBaseline,
  });

  const handleStreamEvent = useCallback(
    (event: StreamEvent) => {
      const name = event.metadata?.agentName;
      if (typeof name === "string" && name) setAgentName(name);
      ingest(event);
    },
    [ingest]
  );

  const effectiveAgentActive = selectedTaskId ? agentActive : false;
  const railCount = useMemo(() => buildRailModel(lanes).active.length, [lanes]);

  const [othersOpen, setOthersOpen] = useState(false);

  const leftColumn = selectedTaskId ? (
    <div className="flex h-full min-h-0 flex-col">
      <div className="min-h-0 flex-1">
        <CodeViewTaskPanel
          key={selectedTaskId}
          taskId={selectedTaskId}
          projectId={projectId}
          totals={totals}
          agentActive={effectiveAgentActive}
          agentName={agentName}
          onTaskLoaded={handleTaskLoaded}
          onLiveTaskChange={handleLiveTaskChange}
          onStreamEvent={handleStreamEvent}
          onWorkspaceChanged={applyWorkspaceChanged}
          onAgentActiveChange={handleAgentActiveChange}
          onDeselect={deselectTask}
        />
      </div>
      {railCount > 1 && (
        <div className={cn("shrink-0 border-t border-white/[0.06]", othersOpen && "max-h-[40%] overflow-y-auto")}>
          <button
            type="button"
            onClick={() => setOthersOpen((o) => !o)}
            aria-expanded={othersOpen}
            className="sticky top-0 z-10 flex w-full items-center gap-1.5 bg-surface-raised px-3 py-2 text-left text-label hover:text-gray-200"
          >
            <svg
              width="10"
              height="10"
              viewBox="0 0 16 16"
              fill="none"
              stroke="currentColor"
              strokeWidth="2"
              aria-hidden="true"
              className={cn("shrink-0 transition-transform", !othersOpen && "-rotate-90")}
            >
              <path d="M4 6l4 4 4-4" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
            {t("rail.otherTasks", { count: railCount - 1 })}
          </button>
          {othersOpen && (
            <TaskRail lanes={lanes} selectedTaskId={selectedTaskId} onSelect={selectTask} hideSelected />
          )}
        </div>
      )}
    </div>
  ) : (
    <div className="h-full overflow-y-auto">
      <TaskRail lanes={lanes} selectedTaskId={null} onSelect={selectTask} />
    </div>
  );

  const codePane = (
    <CodePane
      task={task}
      projectId={projectId}
      model={model}
      totals={totals}
      loadState={loadState}
      agentActive={effectiveAgentActive}
      selectedPath={fileParam}
      onSelectPath={selectPath}
    />
  );

  if (isDesktop) {
    return (
      <div className="flex min-h-0 flex-1 flex-col border-t border-white/[0.06]">
        <SplitPane
          first={<div className="h-full min-h-0 border-r border-white/[0.06] bg-surface-raised/40">{leftColumn}</div>}
          second={codePane}
          defaultSize={400}
          minSize={320}
          maxSize={760}
          storageKey="orkka.codeview.split"
          dividerLabel={t("layout.resize")}
        />
      </div>
    );
  }

  // Phone / tablet: one pane at a time. The selected task's panel stays mounted across tab
  // switches (CSS-hidden, not unmounted) so its SignalR subscription and chat scroll survive.
  const tabs: { key: MobileTab; label: string; disabled?: boolean }[] = [
    { key: "tasks", label: t("tabs.tasks") },
    { key: "chat", label: t("tabs.chat"), disabled: !selectedTaskId },
    { key: "code", label: t("tabs.code"), disabled: !selectedTaskId },
  ];
  const activeTab: MobileTab = selectedTaskId ? mobileTab : "tasks";

  return (
    <div className="flex min-h-0 flex-1 flex-col border-t border-white/[0.06]">
      <div role="tablist" className="flex shrink-0 border-b border-white/[0.06]">
        {tabs.map((tab) => (
          <button
            key={tab.key}
            role="tab"
            type="button"
            aria-selected={activeTab === tab.key}
            disabled={tab.disabled}
            onClick={() => setMobileTab(tab.key)}
            className={cn(
              "flex-1 border-b-2 px-3 py-2 text-xs font-medium transition-colors",
              activeTab === tab.key
                ? "border-primary-400 text-white"
                : "border-transparent text-gray-400 hover:text-gray-200 disabled:opacity-40"
            )}
          >
            {tab.label}
          </button>
        ))}
      </div>
      <div className={cn("min-h-0 flex-1", activeTab !== "tasks" && "hidden")}>
        <div className="h-full overflow-y-auto">
          <TaskRail lanes={lanes} selectedTaskId={selectedTaskId} onSelect={selectTask} />
        </div>
      </div>
      {selectedTaskId && (
        <div className={cn("min-h-0 flex-1", activeTab !== "chat" && "hidden")}>
          <CodeViewTaskPanel
            key={selectedTaskId}
            taskId={selectedTaskId}
            projectId={projectId}
            totals={totals}
            agentActive={effectiveAgentActive}
            agentName={agentName}
            onTaskLoaded={handleTaskLoaded}
            onLiveTaskChange={handleLiveTaskChange}
            onStreamEvent={handleStreamEvent}
            onWorkspaceChanged={applyWorkspaceChanged}
            onAgentActiveChange={handleAgentActiveChange}
            onDeselect={deselectTask}
          />
        </div>
      )}
      <div className={cn("min-h-0 flex-1", activeTab !== "code" && "hidden")}>{codePane}</div>
    </div>
  );
}

export { CodeView };
