import { useState, useCallback, useEffect, useRef, useTransition } from "react";
import { useTranslations } from "use-intl";
import { TaskActions } from "./TaskActions";
import { ModelRecommendationBanner } from "./ModelRecommendationBanner";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { submitVerification } from "@/lib/actions";
import { TaskDetailEditable } from "./TaskDetailEditable";
import { PlanViewer } from "./PlanViewer";
import { AgentStream } from "./AgentStream";
import { ConversationThread } from "./ConversationThread";
import { TaskDiscussPanel } from "./TaskDiscussPanel";
import { AttachmentGallery } from "./AttachmentGallery";
import { TaskDocumentsCard } from "./TaskDocumentsCard";
import { TaskDocumentationCard } from "./TaskDocumentationCard";
import { TaskDependenciesCard } from "./TaskDependenciesCard";
import { TaskGroupPanel } from "./TaskGroupPanel";
import { ExecutionHistory } from "./ExecutionHistory";
import { ViewCodeChangesButton } from "./ViewCodeChangesButton";
import { Link } from "@/i18n/navigation";
import { GlassCard, Button, CopyableCommand, SegmentedControl } from "@/components/ui";
import { formatRelativeTime, checkoutCommand } from "@/lib/utils";
import { useTaskSignalR } from "@/hooks/use-task-signalr";
import { usePermissions } from "@/hooks/use-permissions";
import { useProject } from "@/hooks/use-project";
import { useAttachmentUpload } from "./use-attachment-upload";
import { LANE_KEYS } from "@/lib/constants";
import type { ApplyTaskChatResult, Milestone, ProjectAgent, ProjectRepo, Task, TaskPlan, TaskMessage, TaskAttachment, TaskExecution } from "@/lib/types";
import type { StreamEvent, LaneChangedEvent, StatusChangedEvent, TaskMergeFailedEvent, WorkspaceChangedEvent } from "@/hooks/use-task-signalr";
import type { LaneKey } from "@/lib/constants";
import { refreshData } from "@/lib/data-refresh";

interface TaskDetailLiveProps {
  task: Task;
  projectId: string;
  messages: TaskMessage[];
  plan: TaskPlan | null;
  attachments: TaskAttachment[];
  executions: TaskExecution[];
  /** Called instead of refreshData() when data needs reloading (e.g. in modal context) */
  onRefresh?: () => void;
  /** Fires with the live task whenever SignalR moves it — lets a host header mirror status/lane */
  onLiveTaskChange?: (task: Task) => void;
  /** Project milestones, forwarded to the detail header's picker. */
  milestones?: Milestone[];
  /** Project repos and agents, forwarded to the detail header's pickers. */
  repos?: ProjectRepo[];
  agents?: ProjectAgent[];
  /**
   * The host paints the task as soon as the task itself lands and fills the rest of the
   * props in as their own requests settle. This says the conversation is one of the ones
   * still outstanding, so the thread shows placeholders rather than "no messages yet".
   */
  messagesLoading?: boolean;
  /**
   * "page" (default) is the full two-column task page. "column" is the Code View's left rail:
   * actions + banners on top, the conversation filling the rest — no editable header, plan,
   * sidebar or activity feed, since the code pane next to it shows the work itself.
   */
  layout?: "page" | "column";
  /** Every stream event as it lands — the Code View feeds these to its workspace model. */
  onStreamEvent?: (event: StreamEvent) => void;
  /** The worker re-diffed the worktree (Code View live snapshot). */
  onWorkspaceChanged?: (event: WorkspaceChangedEvent) => void;
  /** Mirrors the "an agent is working right now" signal this component derives. */
  onAgentActiveChange?: (active: boolean) => void;
}

function TaskDetailLive({
  task,
  projectId,
  messages: initialMessages,
  plan,
  attachments,
  executions,
  onRefresh,
  onLiveTaskChange,
  milestones,
  repos,
  agents,
  messagesLoading = false,
  layout = "page",
  onStreamEvent,
  onWorkspaceChanged,
  onAgentActiveChange,
}: TaskDetailLiveProps) {
  const t = useTranslations("dashboard");
  const { currentProject } = useProject();
  const [localTask, setLocalTask] = useState(task);
  const [messages, setMessages] = useState(initialMessages);

  // Sync local task when parent re-fetches
  const [prevTask, setPrevTask] = useState(task);
  if (task !== prevTask) {
    setPrevTask(task);
    setLocalTask(task);
  }

  // Same for the history. The hosts that fetch client-side hand it over after the task
  // itself, so on those surfaces this is the only path by which the thread is ever
  // populated — a plain useState initializer would pin it empty forever.
  //
  // Merged, not replaced: SignalR can deliver a message before the list request that was
  // supposed to contain it comes back, and a straight overwrite would drop something the
  // user watched arrive. Anything already held and absent from the incoming list is kept
  // on the end. (A server-side wipe therefore does not clear the thread until the task is
  // reopened — the same as before this sync existed, and the cheaper mistake of the two.)
  const [prevInitialMessages, setPrevInitialMessages] = useState(initialMessages);
  if (initialMessages !== prevInitialMessages) {
    setPrevInitialMessages(initialMessages);
    setMessages((prev) => {
      if (prev.length === 0) return initialMessages;
      const incoming = new Set(initialMessages.map((m) => m.id));
      const extras = prev.filter((m) => !incoming.has(m.id));
      const next = extras.length > 0 ? [...initialMessages, ...extras] : initialMessages;
      // A refetch hands over a fresh array of the same messages; swapping state for it
      // would re-render the whole thread for nothing.
      return next.length === prev.length && next.every((m, i) => m.id === prev[i].id)
        ? prev
        : next;
    });
  }

  // Mirror live state up so a host header can show status/lane while scrolled deep
  useEffect(() => { onLiveTaskChange?.(localTask); }, [localTask, onLiveTaskChange]);

  // Lanes where an agent may be actively working (PM review, planning, coding,
  // QA, reviewing). InQueue counts too: between phases the task briefly re-queues
  // before the next agent claims it — dropping the indicator there makes an
  // ongoing run look stalled.
  const isAgentLane =
    localTask.currentLane === LANE_KEYS.READY ||
    localTask.currentLane === LANE_KEYS.PLANNING ||
    localTask.currentLane === LANE_KEYS.DEVELOPING ||
    localTask.currentLane === LANE_KEYS.TESTING ||
    localTask.currentLane === LANE_KEYS.CODE_REVIEW;
  // An interactive session lives its whole life in Developing/Active, so the lane+status rule
  // above marks it "working" from the moment it opens until it ends — the spinner never stops,
  // the activity dot pulses forever, and the conversation permanently claims the agent is
  // mid-run. None of that is true between turns, which is where a session spends most of its
  // time: waiting for someone to type.
  //
  // The conversation is the live signal. A turn is running exactly while the newest message is
  // the human's — the agent owes a reply. Once it answers (or the platform posts a system note,
  // e.g. a failed turn), the session is idle again. This needs no new plumbing: NewMessage
  // events already stream in, so it updates the instant either side speaks.
  const sessionIsLive = Boolean(
    localTask.isInteractive && localTask.interactiveSessionStartedAt
  );
  const sessionTurnRunning =
    sessionIsLive &&
    (messages[messages.length - 1]?.role === "user" ||
      // The wrap-up run has no reply to wait for, but it is genuinely working.
      Boolean(localTask.interactiveFinishRequestedAt));

  const isAgentActive = sessionIsLive
    ? sessionTurnRunning
    : isAgentLane && (localTask.status === "Active" || localTask.status === "InQueue");

  useEffect(() => { onAgentActiveChange?.(isAgentActive); }, [isAgentActive, onAgentActiveChange]);

  const isWaitingForAnswer = localTask.currentLane === LANE_KEYS.PENDING_QUESTIONS;
  const isWaitingVerification = localTask.status === "WaitingUserVerification";
  // Parked because the creator's runner is offline. It resumes on reconnect.
  const isWaitingForRunner = localTask.status === "WaitingForRunner";
  // Reset in flight: the worker is cancelling the run and wiping data. Resolves
  // via SignalR LaneChanged when the task lands back in Drafting.
  const isResetting = localTask.status === "Resetting";
  const [isApproving, startApproving] = useTransition();

  const handleNewMessage = useCallback((message: TaskMessage) => {
    // SignalR NewMessage events carry `messageId` instead of `id`
    const raw = message as TaskMessage & { messageId?: string };
    const normalized: TaskMessage = { ...message, id: message.id ?? raw.messageId ?? crypto.randomUUID() };
    setMessages((prev) => {
      if (prev.some((m) => m.id === normalized.id)) return prev;
      return [...prev, normalized];
    });
  }, []);

  const handleRefresh = useCallback(() => {
    if (onRefresh) {
      onRefresh();
    } else {
      refreshData();
    }
  }, [onRefresh]);

  const handleLaneChanged = useCallback((event: LaneChangedEvent) => {
    setLocalTask((prev) => ({
      ...prev,
      currentLane: event.toLane as LaneKey,
      status: (event.status ?? prev.status) as Task["status"],
    }));
    handleRefresh();
  }, [handleRefresh]);

  // "Discuss" (P8): a live chat with an agent about the task, next to the thread.
  const [conversationTab, setConversationTab] = useState<"thread" | "discuss">("thread");

  // An applied discussion moved the task — adopt the lane now, then reload the rest.
  const handleDiscussApplied = useCallback((result: ApplyTaskChatResult) => {
    setLocalTask((prev) => ({
      ...prev,
      currentLane: result.lane as LaneKey,
      status: result.status as Task["status"],
    }));
    handleRefresh();
  }, [handleRefresh]);

  const handleTaskMergeFailed = useCallback((event: TaskMergeFailedEvent) => {
    setLocalTask((prev) => ({ ...prev, mergeFailureReason: event.reason }));
  }, []);

  // Status-only transitions never fire LaneChanged (e.g. a QA pass parking the task
  // for verification stays in Testing) — without this the page misses the park.
  const handleStatusChanged = useCallback((event: StatusChangedEvent) => {
    setLocalTask((prev) => ({ ...prev, status: event.newStatus as Task["status"] }));
    handleRefresh();
  }, [handleRefresh]);

  // Execution-target switches return the fresh task — adopt it immediately so
  // the status badge/banner update without waiting on the refresh round trip.
  const handleTaskUpdated = useCallback((updated: Task) => {
    setLocalTask(updated);
    handleRefresh();
  }, [handleRefresh]);

  const handleApproveVerification = useCallback(() => {
    startApproving(async () => {
      await submitVerification(localTask.id, "approve");
      handleRefresh();
    });
  }, [localTask.id, handleRefresh]);

  const { streamEvents, isConnected } = useTaskSignalR({
    taskId: localTask.id,
    onNewMessage: handleNewMessage,
    onStreamEvent,
    onWorkspaceChanged,
    onPlanReviewed: handleRefresh,
    onLaneChanged: handleLaneChanged,
    onStatusChanged: handleStatusChanged,
    onTaskMergeFailed: handleTaskMergeFailed,
    // Refetch the full task — the event only carries blocker refs, not reasons/sources
    onTaskDependenciesChanged: handleRefresh,
    // A review verdict landed — the review card/PR state is server-rendered, refetch it
    onCodeReviewed: handleRefresh,
    // Non-transition changes (PR opened, branch created, session closed) — the fields may
    // all be null; the ping means "refetch to find out"
    onTaskUpdated: handleRefresh,
  });

  // ── Drag-and-drop attachments: drop files anywhere on the page ──────
  const { has } = usePermissions();
  const canAttach = has("TasksEdit");
  const { uploading: dropUploading, uploadFiles } = useAttachmentUpload(localTask.id, handleRefresh);
  const [isDraggingFiles, setIsDraggingFiles] = useState(false);
  // dragenter/dragleave fire for every child crossed, so track depth
  const dragDepth = useRef(0);

  const isFileDrag = (e: React.DragEvent) => e.dataTransfer.types.includes("Files");

  const handleDragEnter = useCallback((e: React.DragEvent) => {
    if (!canAttach || !isFileDrag(e)) return;
    e.preventDefault();
    dragDepth.current += 1;
    setIsDraggingFiles(true);
  }, [canAttach]);

  const handleDragOver = useCallback((e: React.DragEvent) => {
    if (!canAttach || !isFileDrag(e)) return;
    e.preventDefault();
  }, [canAttach]);

  const handleDragLeave = useCallback((e: React.DragEvent) => {
    if (!canAttach || !isFileDrag(e)) return;
    dragDepth.current -= 1;
    if (dragDepth.current <= 0) {
      dragDepth.current = 0;
      setIsDraggingFiles(false);
    }
  }, [canAttach]);

  const handleDrop = useCallback((e: React.DragEvent) => {
    if (!canAttach || !isFileDrag(e)) return;
    e.preventDefault();
    dragDepth.current = 0;
    setIsDraggingFiles(false);
    if (e.dataTransfer.files.length > 0) {
      uploadFiles(e.dataTransfer.files);
    }
  }, [canAttach, uploadFiles]);

  // Human-gate and status banners — shared by both layouts.
  const banners = (
    <>
    {/* PR Review link. Shown ONLY while the task waits on a human (WaitingCodeReview: an escalation, or —
        with auto-approve off — an agent approval awaiting confirmation). It used to render
        for every Code Review task, including while the reviewer agent was still working —
        which read as an instruction, and a user approved by hand in the middle of an agent
        review, colliding with its verdict seconds later. */}
    {localTask.currentLane === LANE_KEYS.CODE_REVIEW && localTask.prUrl
      && localTask.status === "WaitingCodeReview" && (
      <GlassCard padding="md" accentColor="#06b6d4">
        <div className="flex items-center justify-between">
          <div className="min-w-0">
            <h3 className="font-display text-sm font-semibold text-white">
              {t("taskDetailLive.prReadyForReview")}
            </h3>
            <p className="mt-1 text-xs text-gray-400 truncate">
              {localTask.prUrl}
            </p>
          </div>
          <Link href={`/p/${projectId}/tasks/${localTask.number}/review`}>
            <Button size="sm">
              <svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5">
                <circle cx="5" cy="3.5" r="1.5" />
                <circle cx="5" cy="12.5" r="1.5" />
                <circle cx="11" cy="12.5" r="1.5" />
                <path d="M5 5v6M11 11V6.5a2 2 0 00-2-2H7" strokeLinecap="round" />
              </svg>
              {t("taskDetailLive.reviewPr")}
            </Button>
          </Link>
        </div>
      </GlassCard>
    )}

    {/* Permanent code-change history — renders only once a snapshot is stored. Not in the
        Code View column: the code pane beside the chat IS that history, live, and this card
        cost a banner row of conversation space (its fullscreen lives on the tree panel now). */}
    {layout !== "column" && <ViewCodeChangesButton taskId={localTask.id} />}

    {/* Advisory cheaper-model suggestion from the PM review (renders only when present) */}
    <ModelRecommendationBanner
      task={localTask}
      onResolved={handleTaskUpdated}
    />

    {/* Waiting for answer banner */}
    {isWaitingForAnswer && (
      <GlassCard padding="md" accentColor="#f59e0b">
        <div className="flex items-center gap-3">
          <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="#f59e0b" strokeWidth="2" className="shrink-0">
            <circle cx="12" cy="12" r="10" />
            <path d="M9.09 9a3 3 0 015.83 1c0 2-3 3-3 3M12 17h.01" strokeLinecap="round" />
          </svg>
          <div className="min-w-0">
            <h3 className="font-display text-sm font-semibold text-white">
              {t("taskDetailLive.waitingForAnswer")}
            </h3>
            <p className="mt-0.5 text-xs text-gray-400">
              {t("taskDetailLive.waitingForAnswerHint")}
            </p>
          </div>
        </div>
      </GlassCard>
    )}

    {/* Waiting for runner banner — hybrid task parked, creator's runner offline */}
    {isWaitingForRunner && (
      <GlassCard padding="md" accentColor="#f59e0b">
        <div className="flex items-center gap-3">
          <svg width="20" height="20" viewBox="0 0 16 16" fill="none" stroke="#f59e0b" strokeWidth="1.5" className="shrink-0">
            <rect x="2.5" y="3" width="11" height="7.5" rx="1" />
            <path d="M1.5 13h13" strokeLinecap="round" />
          </svg>
          <div className="min-w-0">
            <h3 className="font-display text-sm font-semibold text-white">
              {t("taskDetailLive.waitingForRunner")}
            </h3>
            <p className="mt-0.5 text-xs text-gray-400">
              {t("taskDetailLive.waitingForRunnerHint")}
            </p>
          </div>
        </div>
      </GlassCard>
    )}

    {/* Reset in progress banner */}
    {isResetting && (
      <GlassCard padding="md" accentColor="#f59e0b">
        <div className="flex items-center gap-3">
          <svg width="20" height="20" viewBox="0 0 16 16" fill="none" stroke="#f59e0b" strokeWidth="1.5" className="shrink-0 animate-spin">
            <path d="M2.5 8a5.5 5.5 0 109.5-3.74M4.5 2.5v3h3" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
          <div className="min-w-0">
            <h3 className="font-display text-sm font-semibold text-white">
              {t("taskDetailLive.resetting")}
            </h3>
            <p className="mt-0.5 text-xs text-gray-400">
              {t("taskDetailLive.resettingHint")}
            </p>
          </div>
        </div>
      </GlassCard>
    )}

    {/* Manual verification banner */}
    {isWaitingVerification && (
      <GlassCard padding="md" accentColor="#22d3ee">
        <div className="flex items-start justify-between gap-3">
          <div className="flex items-start gap-3 min-w-0">
            <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="#22d3ee" strokeWidth="2" className="shrink-0 mt-0.5">
              <path d="M9 12l2 2 4-4" strokeLinecap="round" strokeLinejoin="round" />
              <circle cx="12" cy="12" r="10" />
            </svg>
            <div className="min-w-0">
              <h3 className="font-display text-sm font-semibold text-white">
                {t("taskDetailLive.verificationReady")}
              </h3>
              <p className="mt-0.5 text-xs text-gray-400">
                {t("taskDetailLive.verificationHint")}
              </p>
              {localTask.branchName && (
                <div className="mt-3">
                  <p className="mb-1.5 text-xs text-gray-400">
                    {t("taskDetailLive.verificationCheckout")}
                  </p>
                  <CopyableCommand command={checkoutCommand(localTask.branchName)} />
                </div>
              )}
            </div>
          </div>
          <RequirePermission permission="TasksExecute">
            <Button size="sm" onClick={handleApproveVerification} disabled={isApproving}>
              {isApproving
                ? t("taskDetailLive.verificationApproving")
                : t("taskDetailLive.verificationApprove")}
            </Button>
          </RequirePermission>
        </div>
      </GlassCard>
    )}
    </>
  );

  if (layout === "column") {
    return (
      <div
        className="relative flex h-full min-h-0 flex-col"
        onDragEnter={handleDragEnter}
        onDragOver={handleDragOver}
        onDragLeave={handleDragLeave}
        onDrop={handleDrop}
      >
        <div className="shrink-0 space-y-2 border-b border-white/[0.06] p-2">
          <TaskActions
            task={localTask}
            projectId={projectId}
            onActionComplete={onRefresh}
          />
          {banners}
        </div>
        <div className="min-h-0 flex-1">
          <ConversationThread
            messages={messages}
            taskId={localTask.id}
            agentWorking={isAgentActive}
            projectId={projectId}
            taskNumber={localTask.number}
            loading={messagesLoading}
          />
        </div>
        {isDraggingFiles && (
          <div className="pointer-events-none absolute inset-0 z-40 flex items-center justify-center border-2 border-dashed border-cyan-400/60 bg-cyan-500/[0.06] backdrop-blur-[2px]">
            <p className="rounded-xl border border-white/[0.08] bg-black/80 px-4 py-2 font-display text-sm font-semibold text-white">
              {t("taskDetailLive.dropFilesTitle")}
            </p>
          </div>
        )}
        {dropUploading && (
          <div className="absolute bottom-16 right-3 z-50 flex items-center gap-2 rounded-xl border border-white/[0.08] bg-black/80 px-3 py-2 shadow-2xl">
            <span className="h-3 w-3 animate-spin rounded-full border-2 border-cyan-400 border-t-transparent" />
            <span className="text-xs text-gray-300">{t("taskDetailLive.uploadingAttachments")}</span>
          </div>
        )}
      </div>
    );
  }

  return (
    <div
      className="relative"
      onDragEnter={handleDragEnter}
      onDragOver={handleDragOver}
      onDragLeave={handleDragLeave}
      onDrop={handleDrop}
    >
    <div className="grid gap-4 lg:gap-6 lg:grid-cols-[minmax(0,1fr)_340px]">
      {/* Main content — min-w-0 so long content wraps instead of widening the grid */}
      <div className="min-w-0 space-y-4 lg:space-y-6">
        {/* Actions */}
        <TaskActions
          task={localTask}
          projectId={projectId}
          onActionComplete={onRefresh}
        />

        {/* Task detail with inline editing */}
        <TaskDetailEditable task={localTask} projectId={projectId} onActionComplete={onRefresh} milestones={milestones} repos={repos} agents={agents} />

        {/* Plan */}
        {plan && (
          <PlanViewer plan={plan} taskId={localTask.id} taskLane={localTask.currentLane} taskStatus={localTask.status} />
        )}

        {banners}

        {/* Agent stream */}
        {(isAgentActive || streamEvents.length > 0) && (
          <AgentStream
            events={streamEvents}
            isActive={isAgentActive}
            showToolCalls={currentProject?.settings?.showAgentToolCalls ?? false}
          />
        )}

        {/* Conversation */}
        <GlassCard padding="none" className="flex flex-col">
          <div className="border-b border-white/[0.04] px-4 py-2.5 lg:px-5 lg:py-3 flex items-center justify-between">
            <SegmentedControl
              aria-label={t("taskDiscuss.tabsLabel")}
              value={conversationTab}
              onChange={setConversationTab}
              options={[
                { value: "thread", label: t("taskDetailLive.conversation") },
                { value: "discuss", label: t("taskDiscuss.tab"), title: t("taskDiscuss.tabHint") },
              ]}
            />
            {isConnected && (
              <span className="flex items-center gap-1.5 text-xs text-emerald-400/70">
                <span className="h-1.5 w-1.5 rounded-full bg-emerald-400" />
                {t("taskDetailLive.live")}
              </span>
            )}
          </div>
          {conversationTab === "discuss" ? (
            <TaskDiscussPanel
              taskId={localTask.id}
              projectId={projectId}
              lane={localTask.currentLane}
              onApplied={handleDiscussApplied}
            />
          ) : (
            <ConversationThread
              messages={messages}
              taskId={localTask.id}
              agentWorking={isAgentActive}
              projectId={projectId}
              taskNumber={localTask.number}
              loading={messagesLoading}
            />
          )}
        </GlassCard>
      </div>

      {/* Sidebar */}
      <div className="min-w-0 space-y-4">
        {/* Details */}
        <GlassCard padding="md">
          <h4 className="text-label mb-3">
            {t("taskDetailLive.details")}
          </h4>
          <div className="space-y-3 text-sm">
            <div className="flex justify-between">
              <span className="text-gray-400">{t("taskDetailLive.created")}</span>
              <span className="text-gray-300 text-xs">
                {formatRelativeTime(localTask.createdAt)}
              </span>
            </div>
            <div className="flex justify-between">
              <span className="text-gray-400">{t("taskDetailLive.started")}</span>
              <span className="text-gray-300 text-xs">
                {localTask.startedAt ? formatRelativeTime(localTask.startedAt) : "—"}
              </span>
            </div>
            {localTask.completedAt && (
              <div className="flex justify-between">
                <span className="text-gray-400">{t("taskDetailLive.completed")}</span>
                <span className="text-gray-300 text-xs">
                  {formatRelativeTime(localTask.completedAt)}
                </span>
              </div>
            )}
            <div className="flex justify-between">
              <span className="text-gray-400">{t("taskDetailLive.repository")}</span>
              <span className="text-gray-300 text-xs">
                {localTask.repoName ?? "—"}
              </span>
            </div>
            {localTask.queuePosition !== null && (
              <div className="flex justify-between">
                <span className="text-gray-400">{t("taskDetailLive.queuePosition")}</span>
                <span className="text-gray-300 text-xs">
                  #{localTask.queuePosition}
                </span>
              </div>
            )}
          </div>
        </GlassCard>

        {/* The other repositories' halves — the only place the shared contract and the merge
            gate are visible. Renders nothing for an ordinary single-repo task. */}
        <TaskGroupPanel
          groupId={localTask.groupId}
          projectId={projectId}
          canEdit={has("TasksEdit")}
          currentTaskId={localTask.id}
        />

        {/* Dependency graph — why agents may be skipping this task in the queue */}
        <TaskDependenciesCard task={localTask} projectId={projectId} onChanged={handleRefresh} />

        {/* Context documents (injected into agent prompts when the task runs) */}
        <TaskDocumentsCard task={localTask} projectId={projectId} />

        {/* Documents this task's work produced — renders only once there are any */}
        <TaskDocumentationCard taskId={localTask.id} projectId={projectId} />

        {/* Attachments */}
        <AttachmentGallery
          attachments={attachments}
          taskId={localTask.id}
          onUploaded={handleRefresh}
        />

        {/* Execution history */}
        <ExecutionHistory executions={executions} projectId={projectId} />
      </div>
    </div>

      {/* Drop-anywhere overlay */}
      {isDraggingFiles && (
        <div className="pointer-events-none absolute inset-0 z-40 flex items-center justify-center rounded-2xl border-2 border-dashed border-cyan-400/60 bg-cyan-500/[0.06] backdrop-blur-[2px]">
          <div className="rounded-xl border border-white/[0.08] bg-black/80 px-6 py-4 text-center shadow-2xl">
            <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="#22d3ee" strokeWidth="1.5" className="mx-auto mb-2">
              <path d="M12 16V4M8 8l4-4 4 4" strokeLinecap="round" strokeLinejoin="round" />
              <path d="M4 16v3a1 1 0 001 1h14a1 1 0 001-1v-3" strokeLinecap="round" />
            </svg>
            <p className="font-display text-sm font-semibold text-white">
              {t("taskDetailLive.dropFilesTitle")}
            </p>
            <p className="mt-1 text-xs text-gray-400">
              {t("taskDetailLive.dropFilesHint")}
            </p>
          </div>
        </div>
      )}

      {/* Upload progress for dropped files */}
      {dropUploading && (
        <div className="fixed bottom-4 right-4 z-50 flex items-center gap-2.5 rounded-xl border border-white/[0.08] bg-black/80 px-4 py-2.5 shadow-2xl">
          <span className="h-3 w-3 animate-spin rounded-full border-2 border-cyan-400 border-t-transparent" />
          <span className="text-xs text-gray-300">
            {t("taskDetailLive.uploadingAttachments")}
          </span>
        </div>
      )}
    </div>
  );
}

export { TaskDetailLive };
