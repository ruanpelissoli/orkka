import { useCallback, useEffect, useRef, useState } from "react";
import { useTranslations } from "use-intl";
import { api } from "@/lib/api";
import { useClientApi } from "@/hooks/use-client-api";
import { TaskDetailLive } from "@/components/task/TaskDetailLive";
import { InteractiveSessionBanner } from "@/components/task/InteractiveSessionBanner";
import { InteractiveApprovalPanel } from "@/components/task/InteractiveApprovalPanel";
import type { StreamEvent, WorkspaceChangedEvent } from "@/hooks/use-task-signalr";
import type {
  Task,
  TaskMessage,
  TaskPlan,
  TaskAttachment,
  TaskExecution,
  PendingApproval,
} from "@/lib/types";
import { PinnedTaskHeader } from "./PinnedTaskHeader";
import type { WorkspaceTotals } from "./workspace-model";

interface CodeViewTaskPanelProps {
  taskId: string;
  projectId: string;
  totals: WorkspaceTotals;
  agentActive: boolean;
  agentName: string | null;
  onTaskLoaded: (task: Task | null) => void;
  onLiveTaskChange: (task: Task) => void;
  onStreamEvent: (event: StreamEvent) => void;
  onWorkspaceChanged: (event: WorkspaceChangedEvent) => void;
  onAgentActiveChange: (active: boolean) => void;
  onDeselect: () => void;
}

/** Everything the panel reads alongside the task, each part arriving on its own. */
interface TaskParts {
  messages: TaskMessage[];
  plan: TaskPlan | null;
  attachments: TaskAttachment[];
  executions: TaskExecution[];
  approvals: PendingApproval[];
}

const EMPTY_PARTS: TaskParts = {
  messages: [],
  plan: null,
  attachments: [],
  executions: [],
  approvals: [],
};

interface PanelState {
  /** Which task this state describes — anything tagged with another one is stale. */
  taskId: string;
  task: Task | null;
  parts: TaskParts;
  /** The conversation is the one part with a visible empty state of its own. */
  messagesLoading: boolean;
  error: boolean;
}

/**
 * The left column once a task is selected: pinned header, session banner / approvals, then
 * `TaskDetailLive` in its column layout (actions, banners, conversation). Fetches its own
 * data the way the board modal does — the Code View never leaves the board route, so nothing
 * here can be server-rendered.
 */
function CodeViewTaskPanel({
  taskId,
  projectId,
  totals,
  agentActive,
  agentName,
  onTaskLoaded,
  onLiveTaskChange,
  onStreamEvent,
  onWorkspaceChanged,
  onAgentActiveChange,
  onDeselect,
}: CodeViewTaskPanelProps) {
  const t = useTranslations("dashboard.codeView");
  const withToken = useClientApi();
  const [state, setState] = useState<PanelState>({
    taskId,
    task: null,
    parts: EMPTY_PARTS,
    messagesLoading: true,
    error: false,
  });
  // State tagged with another task must never render under a new header.
  const current: PanelState =
    state.taskId === taskId
      ? state
      : { taskId, task: null, parts: EMPTY_PARTS, messagesLoading: true, error: false };
  const [liveTask, setLiveTask] = useState<Task | null>(null);

  // Reads land independently, so a response belonging to a task the user has already
  // clicked past must not be applied. Every load takes a ticket; a setter only runs while
  // its ticket is still the newest.
  const requestRef = useRef(0);

  /** Applies one part's response once it arrives, provided the panel is still on that task. */
  const patchParts = useCallback(
    (id: string, apply: (parts: TaskParts) => TaskParts) =>
      setState((prev) => (prev.taskId === id ? { ...prev, parts: apply(prev.parts) } : prev)),
    []
  );

  // The six reads used to sit behind one Promise.all, so the panel showed its skeleton until
  // the slowest of them returned. Only the task gates the render now; the conversation,
  // plan, attachments, history and approvals fill in as they land, and each owns its own
  // failure rather than taking the panel down with it.
  const load = useCallback(
    async (id: string) => {
      const request = ++requestRef.current;
      const fresh = () => requestRef.current === request;

      // Tag the state for this task before anything is in flight, so the part responses
      // below have something to match against. Reloading the task already on screen keeps
      // its state, which is what lets refresh() update the cards in place rather than
      // blanking them and filling them back in.
      setState((prev) =>
        prev.taskId === id
          ? prev
          : { taskId: id, task: null, parts: EMPTY_PARTS, messagesLoading: true, error: false }
      );

      const failed = () => {
        if (!fresh()) return;
        setState((prev) =>
          prev.taskId === id ? { ...prev, task: null, messagesLoading: false, error: true } : prev
        );
        onTaskLoaded(null);
      };

      try {
        await withToken(async (token) => {
          const secondary = [
            api.messages
              .list(id, token)
              .then((v) => { if (fresh()) patchParts(id, (p) => ({ ...p, messages: v })); })
              .catch(() => {})
              .finally(() => {
                if (!fresh()) return;
                setState((prev) => (prev.taskId === id ? { ...prev, messagesLoading: false } : prev));
              }),
            api.plans
              .latest(id, token)
              .then((v) => { if (fresh()) patchParts(id, (p) => ({ ...p, plan: v })); })
              .catch(() => {}),
            api.attachments
              .list(id, token)
              .then((v) => { if (fresh()) patchParts(id, (p) => ({ ...p, attachments: v })); })
              .catch(() => {}),
            api.executions
              .list(id, token)
              .then((v) => { if (fresh()) patchParts(id, (p) => ({ ...p, executions: v })); })
              .catch(() => {}),
            api.tasks
              .listApprovals(id, token)
              .then((v) => { if (fresh()) patchParts(id, (p) => ({ ...p, approvals: v })); })
              .catch(() => {}),
          ];
          // Fired in the same tick as the five above — awaiting it does not serialize them.
          const task = await api.tasks.get(id, token).catch(() => null);
          if (!fresh()) return;
          if (!task) {
            failed();
            return;
          }
          setState((prev) => (prev.taskId === id ? { ...prev, task, error: false } : prev));
          onTaskLoaded(task);
          void Promise.allSettled(secondary);
        });
      } catch {
        // Only the token handoff itself reaches here — every read above swallows its own.
        failed();
      }
    },
    [withToken, onTaskLoaded, patchParts]
  );

  // Deferred to a microtask so no setState is reachable from the effect body itself; the
  // taskId tag on the stored state guards staleness.
  useEffect(() => {
    const id = taskId;
    let cancelled = false;
    queueMicrotask(() => {
      if (!cancelled) void load(id);
    });
    return () => {
      cancelled = true;
    };
  }, [taskId, load]);

  const refresh = useCallback(() => void load(taskId), [load, taskId]);

  const handleLiveTaskChange = useCallback(
    (task: Task) => {
      setLiveTask(task);
      onLiveTaskChange(task);
    },
    [onLiveTaskChange]
  );

  const headerTask = liveTask && liveTask.id === taskId ? liveTask : current.task;

  if (current.error) {
    return (
      <div className="flex h-full flex-col">
        <div className="flex flex-1 flex-col items-center justify-center gap-3 p-6 text-center">
          <p className="text-sm text-gray-400">{t("panel.loadFailed")}</p>
          <button
            type="button"
            onClick={refresh}
            className="rounded-lg border border-white/[0.08] px-3 py-1.5 text-xs text-gray-200 hover:bg-white/[0.06]"
          >
            {t("panel.retry")}
          </button>
        </div>
      </div>
    );
  }

  if (!current.task || !headerTask) {
    return (
      <div className="flex h-full flex-col">
        <div className="shrink-0 space-y-2 border-b border-white/[0.06] px-3 py-3">
          <div className="h-3 w-16 rounded bg-white/[0.06] shimmer" />
          <div className="h-4 w-4/5 rounded bg-white/[0.06] shimmer" />
          <div className="h-3 w-1/2 rounded bg-white/[0.04] shimmer" />
        </div>
        <div className="flex-1 space-y-3 p-3">
          <div className="h-14 w-4/5 rounded-xl bg-white/[0.04] shimmer" />
          <div className="ml-auto h-10 w-2/3 rounded-xl bg-white/[0.04] shimmer" />
          <div className="h-16 w-4/5 rounded-xl bg-white/[0.04] shimmer" />
        </div>
      </div>
    );
  }

  const { task, parts } = current;

  return (
    <div className="flex h-full min-h-0 flex-col">
      <PinnedTaskHeader
        task={headerTask}
        projectId={projectId}
        agentActive={agentActive}
        agentName={agentName}
        totals={totals}
        onDeselect={onDeselect}
      />
      <div className="shrink-0 empty:hidden">
        <InteractiveSessionBanner task={headerTask} className="m-2" onFinished={refresh} />
        <InteractiveApprovalPanel
          taskId={taskId}
          initialApprovals={parts.approvals}
          className="m-2"
        />
      </div>
      <div className="min-h-0 flex-1">
        <TaskDetailLive
          task={task}
          projectId={projectId}
          messages={parts.messages}
          plan={parts.plan}
          attachments={parts.attachments}
          executions={parts.executions}
          messagesLoading={current.messagesLoading}
          onRefresh={refresh}
          onLiveTaskChange={handleLiveTaskChange}
          layout="column"
          onStreamEvent={onStreamEvent}
          onWorkspaceChanged={onWorkspaceChanged}
          onAgentActiveChange={onAgentActiveChange}
        />
      </div>
    </div>
  );
}

export { CodeViewTaskPanel };
