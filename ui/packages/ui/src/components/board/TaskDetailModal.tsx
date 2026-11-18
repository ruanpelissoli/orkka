import { useState, useEffect, useCallback, useRef } from "react";
import { Link } from "@/i18n/navigation";
import { Modal, Badge } from "@/components/ui";
import { TaskDetailLive } from "@/components/task/TaskDetailLive";
import { InteractiveSessionBanner } from "@/components/task/InteractiveSessionBanner";
import { api } from "@/lib/api";
import { useClientApi } from "@/hooks/use-client-api";
import { LANE_MAP, TASK_STATUS_MAP } from "@/lib/constants";
import { useLabels } from "@/hooks/use-labels";
import type {
  Task, TaskMessage, TaskPlan, TaskAttachment, TaskExecution,
  ProjectRepo, Milestone, ProjectAgent,
} from "@/lib/types";
import { OrcaError } from "@/components/animations";
import { useTranslations } from "use-intl";

interface TaskDetailModalProps {
  taskId: string | null;
  projectId: string;
  onClose: () => void;
  /** Ongoing tasks (queued or agent-active) in board order — powers the prev/next carousel. */
  activeTaskIds?: string[];
  /** Swap the modal to another task — same shallow-URL update as clicking its card. */
  onNavigateToTask?: (taskId: string) => void;
}

function TaskDetailModalSkeleton() {
  return (
    <div className="grid gap-6 lg:grid-cols-[1fr_340px]">
      {/* Main content skeleton */}
      <div className="space-y-6">
        {/* Actions bar */}
        <div className="flex gap-2">
          <div className="h-8 w-28 rounded-lg bg-white/[0.04] shimmer" />
          <div className="h-8 w-20 rounded-lg bg-white/[0.04] shimmer" />
        </div>
        {/* Task detail card */}
        <div className="rounded-2xl border border-white/[0.06] bg-white/[0.03] p-6 space-y-4">
          <div className="h-7 w-3/4 rounded-lg bg-white/[0.06] shimmer" />
          <div className="flex gap-2">
            <div className="h-6 w-24 rounded-full bg-white/[0.04] shimmer" />
            <div className="h-6 w-20 rounded-full bg-white/[0.04] shimmer" />
            <div className="h-6 w-16 rounded-full bg-white/[0.04] shimmer" />
          </div>
          <div className="space-y-2">
            <div className="h-4 w-full rounded bg-white/[0.04] shimmer" />
            <div className="h-4 w-5/6 rounded bg-white/[0.04] shimmer" />
            <div className="h-4 w-2/3 rounded bg-white/[0.04] shimmer" />
          </div>
        </div>
        {/* Conversation skeleton */}
        <div className="rounded-2xl border border-white/[0.06] bg-white/[0.03] min-h-[300px]">
          <div className="border-b border-white/[0.04] px-5 py-3">
            <div className="h-4 w-28 rounded bg-white/[0.06] shimmer" />
          </div>
          <div className="p-5 space-y-4">
            <div className="flex gap-3">
              <div className="h-8 w-8 rounded-full bg-white/[0.04] shimmer shrink-0" />
              <div className="h-16 flex-1 rounded-xl bg-white/[0.04] shimmer" />
            </div>
            <div className="flex gap-3 justify-end">
              <div className="h-12 w-2/3 rounded-xl bg-white/[0.04] shimmer" />
              <div className="h-8 w-8 rounded-full bg-white/[0.04] shimmer shrink-0" />
            </div>
          </div>
        </div>
      </div>
      {/* Sidebar skeleton */}
      <div className="space-y-4">
        <div className="rounded-2xl border border-white/[0.06] bg-white/[0.03] p-4 space-y-3">
          <div className="h-3 w-16 rounded bg-white/[0.06] shimmer" />
          <div className="space-y-2">
            {Array.from({ length: 4 }).map((_, i) => (
              <div key={i} className="flex justify-between">
                <div className="h-3 w-20 rounded bg-white/[0.04] shimmer" />
                <div className="h-3 w-24 rounded bg-white/[0.04] shimmer" />
              </div>
            ))}
          </div>
        </div>
        <div className="rounded-2xl border border-white/[0.06] bg-white/[0.03] p-4 h-32 shimmer" />
      </div>
    </div>
  );
}

function TaskDetailModal({
  taskId,
  projectId,
  onClose,
  activeTaskIds,
  onNavigateToTask,
}: TaskDetailModalProps) {
  const t = useTranslations("dashboard");
  const labels = useLabels();
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(false);
  const [task, setTask] = useState<Task | null>(null);
  // Live mirror from TaskDetailLive (SignalR) — keeps the sticky header current
  // while the user is scrolled deep into the chat/plan
  const [liveTask, setLiveTask] = useState<Task | null>(null);
  const [messages, setMessages] = useState<TaskMessage[]>([]);
  const [plan, setPlan] = useState<TaskPlan | null>(null);
  const [attachments, setAttachments] = useState<TaskAttachment[]>([]);
  const [executions, setExecutions] = useState<TaskExecution[]>([]);
  // Project-level lists the header's pickers need. The modal used to omit them, so a task
  // opened from the board showed no repo, milestone, or agent picker at all — and a task
  // filed against the wrong repo was unfixable without navigating to the full page.
  const [repos, setRepos] = useState<ProjectRepo[]>([]);
  const [milestones, setMilestones] = useState<Milestone[]>([]);
  const [agents, setAgents] = useState<ProjectAgent[]>([]);
  // The conversation is the one late arrival with a visible empty state of its own.
  const [messagesLoading, setMessagesLoading] = useState(true);

  const withToken = useClientApi();

  // Reads are fired concurrently but land independently, so a response belonging to a task
  // the user has already flipped past must not overwrite the current one. Every fetch takes
  // a ticket; a setter only runs while its ticket is still the newest.
  const requestRef = useRef(0);

  const fetchTaskData = useCallback((id: string) => {
    const request = ++requestRef.current;
    const fresh = () => requestRef.current === request;

    // Direct API calls — genuinely parallel.
    //
    // These used to be one Promise.all, so the modal painted nothing until the slowest of
    // eight requests came back and any one stall held the whole thing at the skeleton. Only
    // the task itself gates the render now; the rest fill their own cards in as they land.
    // Each read owns its failure too: a picker or a card missing is not a broken task.
    return withToken(async (token) => {
      const secondary = [
        api.messages.list(id, token)
          .then((v) => { if (fresh()) setMessages(v); })
          .catch(() => {})
          .finally(() => { if (fresh()) setMessagesLoading(false); }),
        api.plans.latest(id, token)
          .then((v) => { if (fresh()) setPlan(v); })
          .catch(() => {}),
        api.attachments.list(id, token)
          .then((v) => { if (fresh()) setAttachments(v); })
          .catch(() => {}),
        api.executions.list(id, token)
          .then((v) => { if (fresh()) setExecutions(v); })
          .catch(() => {}),
        api.repos.list(projectId, token)
          .then((v) => { if (fresh()) setRepos(v); })
          .catch(() => {}),
        api.milestones.list(projectId, token)
          .then((res) => { if (fresh()) setMilestones(res.milestones); })
          .catch(() => {}),
        api.agents.list(projectId, token)
          .then((v) => { if (fresh()) setAgents(v); })
          .catch(() => {}),
      ];
      // Fired in the same tick as the seven above — awaiting it does not serialize them.
      const t = await api.tasks.get(id, token).catch(() => null);
      if (!fresh()) return;
      if (!t) {
        setError(true);
        setLoading(false);
        return;
      }
      setError(false);
      setTask(t);
      setLoading(false);
      void Promise.allSettled(secondary);
    }).catch(() => {
      // Only the token handoff itself reaches here — every read above swallows its own.
      if (fresh()) {
        setError(true);
        setLoading(false);
      }
    });
  }, [withToken, projectId]);

  // Re-fetch all data (used after plan review, actions, etc.)
  const handleRefresh = useCallback(() => {
    if (!taskId) return;
    setError(false);
    fetchTaskData(taskId);
  }, [taskId, fetchTaskData]);

  // A new task (or none) resets the task-scoped state during render, before the fetch effect
  // below runs. Clearing it here rather than inside the fetch is what lets handleRefresh update
  // the cards in place instead of blanking them and filling them back in. The project-scoped
  // lists (repos, milestones, agents) survive a flip between tasks for the same reason — they
  // cannot have gone stale in the meantime.
  const [prevTaskId, setPrevTaskId] = useState(taskId);
  if (taskId !== prevTaskId) {
    setPrevTaskId(taskId);
    setLoading(true);
    setError(false);
    setLiveTask(null);
    setTask(null);
    if (taskId) {
      setMessages([]);
      setMessagesLoading(true);
      setPlan(null);
      setAttachments([]);
      setExecutions([]);
    }
  }

  useEffect(() => {
    if (!taskId) {
      // Retire any read still in flight for the task that was closed.
      requestRef.current++;
      return;
    }
    fetchTaskData(taskId);
  }, [taskId, fetchTaskData]);

  // ── Ongoing-task carousel ────────────────────────────────────────────
  // Flip between queued/agent-active tasks without closing the modal —
  // header arrows or ←/→ keys (ignored while typing in the chat input).
  const activeCount = activeTaskIds?.length ?? 0;
  const activeIndex = taskId && activeTaskIds ? activeTaskIds.indexOf(taskId) : -1;
  // Viewing the sole ongoing task (or none exist) leaves nowhere to go
  const canCycle = activeCount > 1 || (activeCount === 1 && activeIndex === -1);

  const navigateActive = useCallback(
    (direction: 1 | -1) => {
      if (!activeTaskIds || activeTaskIds.length === 0 || !onNavigateToTask) return;
      const idx = taskId ? activeTaskIds.indexOf(taskId) : -1;
      // From a non-ongoing task, enter the cycle at its nearest end
      const next =
        idx === -1
          ? direction === 1
            ? 0
            : activeTaskIds.length - 1
          : (idx + direction + activeTaskIds.length) % activeTaskIds.length;
      const target = activeTaskIds[next];
      if (target !== taskId) onNavigateToTask(target);
    },
    [activeTaskIds, taskId, onNavigateToTask]
  );

  useEffect(() => {
    if (taskId === null) return;
    function handleKey(e: KeyboardEvent) {
      if (e.key !== "ArrowLeft" && e.key !== "ArrowRight") return;
      if (e.metaKey || e.ctrlKey || e.altKey || e.shiftKey) return;
      const el = e.target as HTMLElement | null;
      if (
        el &&
        (el.tagName === "INPUT" ||
          el.tagName === "TEXTAREA" ||
          el.tagName === "SELECT" ||
          el.isContentEditable)
      )
        return;
      e.preventDefault();
      navigateActive(e.key === "ArrowRight" ? 1 : -1);
    }
    window.addEventListener("keydown", handleKey);
    return () => window.removeEventListener("keydown", handleKey);
  }, [taskId, navigateActive]);

  const headerTask = liveTask ?? task;
  const headerLane = headerTask ? LANE_MAP[headerTask.currentLane] : null;
  const headerStatus = headerTask ? TASK_STATUS_MAP[headerTask.status] : null;

  return (
    <Modal
      open={taskId !== null}
      onClose={onClose}
      fullScreenMobile
      className="md:max-w-5xl md:max-h-[90vh]"
    >
      {/* Sticky header inside scroll area */}
      <div className="sticky top-0 z-20 flex items-center justify-between border-b border-white/[0.06] bg-gray-900/95 backdrop-blur-sm py-2.5 -mx-6 -mt-4 mb-6 px-6">
        <div className="flex items-center gap-3">
          <h2 className="font-display text-sm font-semibold text-gray-300">
            {t("taskDetailModal.title")}
          </h2>
          {taskId && (
            <Link
              href={`/p/${projectId}/tasks/${task?.number ?? taskId}`}
              className="text-xs text-gray-400 hover:text-gray-300 transition-colors font-mono"
            >
              {t("taskDetailModal.openFullPage")}
            </Link>
          )}
        </div>
        <div className="flex items-center gap-2">
          {activeCount > 0 && (
            <div
              className="mr-1 flex items-center gap-0.5"
              title={t("taskDetailModal.ongoingTasks")}
            >
              <button
                onClick={() => navigateActive(-1)}
                disabled={!canCycle}
                aria-label={t("taskDetailModal.prevActiveTask")}
                className="rounded-lg p-1.5 text-gray-400 transition-colors hover:bg-white/[0.06] hover:text-gray-200 disabled:opacity-30 disabled:hover:bg-transparent disabled:hover:text-gray-400"
              >
                <svg width="14" height="14" viewBox="0 0 16 16" fill="none">
                  <path
                    d="M10 3L5 8l5 5"
                    stroke="currentColor"
                    strokeWidth="1.5"
                    strokeLinecap="round"
                    strokeLinejoin="round"
                  />
                </svg>
              </button>
              <span className="font-mono text-xs tabular-nums text-gray-400 select-none">
                {activeIndex === -1 ? "–" : activeIndex + 1}/{activeCount}
              </span>
              <button
                onClick={() => navigateActive(1)}
                disabled={!canCycle}
                aria-label={t("taskDetailModal.nextActiveTask")}
                className="rounded-lg p-1.5 text-gray-400 transition-colors hover:bg-white/[0.06] hover:text-gray-200 disabled:opacity-30 disabled:hover:bg-transparent disabled:hover:text-gray-400"
              >
                <svg width="14" height="14" viewBox="0 0 16 16" fill="none">
                  <path
                    d="M6 3l5 5-5 5"
                    stroke="currentColor"
                    strokeWidth="1.5"
                    strokeLinecap="round"
                    strokeLinejoin="round"
                  />
                </svg>
              </button>
            </div>
          )}
          {headerStatus && (
            <Badge
              size="sm"
              dot
              dotColor={headerStatus.color}
              style={{
                backgroundColor: `${headerStatus.color}10`,
                color: headerStatus.color,
                borderColor: `${headerStatus.color}20`,
              }}
            >
              {labels.taskStatus(headerStatus.key)}
            </Badge>
          )}
          {headerLane && (
            <Badge
              size="sm"
              style={{
                backgroundColor: `${headerLane.color}10`,
                color: headerLane.color,
                borderColor: `${headerLane.color}20`,
              }}
            >
              {labels.lane(headerLane.key)}
            </Badge>
          )}
          <button
            onClick={onClose}
            className="rounded-lg p-1.5 text-gray-400 transition-colors hover:bg-white/[0.06] hover:text-gray-200"
          >
            <svg width="16" height="16" viewBox="0 0 16 16" fill="none">
              <path
                d="M12 4L4 12M4 4l8 8"
                stroke="currentColor"
                strokeWidth="1.5"
                strokeLinecap="round"
              />
            </svg>
          </button>
        </div>
      </div>

      {/* Content */}
      {error ? (
        <div className="flex flex-col items-center justify-center py-12 text-center">
          <OrcaError
            size="md"
            title={t("taskDetailModal.errorTitle")}
            description={t("taskDetailModal.errorDescription")}
          />
          <button
            onClick={handleRefresh}
            className="mt-4 rounded-lg bg-white/[0.06] px-4 py-2 text-xs font-medium text-gray-300 transition-colors hover:bg-white/[0.1]"
          >
            {t("taskDetailModal.retry")}
          </button>
        </div>
      ) : loading || !task ? (
        <TaskDetailModalSkeleton />
      ) : (
        <>
          {/* The one affordance a session has that an ordinary task does not. Without it here,
              a session opened from the board could be talked to but never finished — the button
              existed only on the full page, which is not where people open tasks from.
              Reads headerTask so the live SignalR mirror flips it to "wrapping up" immediately.
              The banner self-guards: an ordinary task renders nothing. */}
          <InteractiveSessionBanner
            task={headerTask ?? task}
            className="mb-4"
            // The drawer's task is state it fetched itself, so a route refresh cannot update it.
            onFinished={handleRefresh}
          />
          <TaskDetailLive
            task={task}
            projectId={projectId}
            messages={messages}
            plan={plan}
            attachments={attachments}
            executions={executions}
            onRefresh={handleRefresh}
            onLiveTaskChange={setLiveTask}
            repos={repos}
            milestones={milestones}
            agents={agents}
            messagesLoading={messagesLoading}
          />
        </>
      )}
    </Modal>
  );
}

export { TaskDetailModal };
