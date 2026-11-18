import { useDraggable } from "@dnd-kit/core";
import { AnimatePresence, motion } from "framer-motion";
import { cn } from "@/lib/utils";
import { collapse, listItem, SPRING_LAYOUT } from "@/lib/motion";
import { Badge, PriorityBadge } from "@/components/ui";
import { ExternalTicketBadge } from "@/components/task/ExternalTicketBadge";
import { stripTicketKey } from "@/lib/external-ticket";
import { LANE_KEYS, TASK_STATUS_MAP, type LaneKey } from "@/lib/constants";
import { useLabels } from "@/hooks/use-labels";
import { OrcaStatus } from "@/components/animations";
import type { TaskSummary } from "@/lib/types";
import { useTranslations } from "use-intl";

interface TaskCardProps {
  task: TaskSummary;
  onTaskClick?: (taskId: string) => void;
  /** Grid (search) mode renders cards outside droppable lanes — dragging is meaningless there */
  dragDisabled?: boolean;
  /**
   * Title of the task's milestone, resolved by the board from the milestone list it already
   * loads. Absent (rather than empty) for a backlog task, and for a milestone created after
   * that list was fetched — the line is simply not rendered then.
   */
  milestoneName?: string | null;
  /**
   * Render at rest, with no entry animation. The drag overlay mounts a fresh
   * copy of this card the instant you pick one up; without this it would fade
   * and scale in from nothing under the cursor on every drag.
   */
  atRest?: boolean;
}

function TaskTypeIcon({ type }: { type: string }) {
  const iconClass = "w-3.5 h-3.5 shrink-0";
  switch (type) {
    case "BugFix":
      return (
        <svg className={cn(iconClass, "text-red-400")} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5">
          <circle cx="8" cy="8" r="3" />
          <path d="M8 1v4M8 11v4M1 8h4M11 8h4" strokeLinecap="round" />
        </svg>
      );
    case "Refactor":
      return (
        <svg className={cn(iconClass, "text-cyan-400")} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5">
          <path d="M2 8h5l-2-3M14 8H9l2 3" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      );
    case "Improvement":
      return (
        <svg className={cn(iconClass, "text-amber-400")} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5">
          <path d="M8 2l1.5 3 3.5.5-2.5 2.5.5 3.5L8 10l-3 1.5.5-3.5L3 5.5 6.5 5z" strokeLinejoin="round" />
        </svg>
      );
    default: // Feature
      return (
        <svg className={cn(iconClass, "text-violet-400")} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5">
          <path d="M8 2a4 4 0 014 4c0 1.5-.8 2.5-1.5 3.5S9 11 9 12H7c0-1-.8-2-1.5-3S4 7.5 4 6a4 4 0 014-4z" />
          <path d="M6.5 14h3" strokeLinecap="round" />
        </svg>
      );
  }
}

/**
 * Statuses where the pipeline is stalled until a human acts. The card gets a
 * tinted border and a pulsing call-to-action row naming what is needed.
 *
 * Literal hex rather than `var(--color-…)` because these are composed with an
 * alpha suffix (`${color}55`) for the border and glow, which custom properties
 * cannot do by string concatenation. Every value is a palette colour: the lane
 * tokens for lane-shaped waits, --color-warning/--color-error otherwise.
 */
/** A stable colour per story (from its group id), shared by every card of that story. */
const STORY_COLORS = ["#a78bfa", "#38bdf8", "#f472b6", "#34d399", "#fbbf24", "#fb923c", "#60a5fa", "#c084fc"];
function storyColor(groupId: string): string {
  let hash = 0;
  for (let i = 0; i < groupId.length; i++) hash = (hash * 31 + groupId.charCodeAt(i)) | 0;
  return STORY_COLORS[Math.abs(hash) % STORY_COLORS.length];
}

const ATTENTION_STATUSES: Record<string, { labelKey: string; color: string }> = {
  WaitingAnswer: { labelKey: "attentionAnswer", color: "#f59e0b" }, // lane-questions
  WaitingPlanApproval: { labelKey: "attentionPlan", color: "#a78bfa" }, // lane-plan-ready
  WaitingCodeReview: { labelKey: "attentionCode", color: "#06b6d4" }, // lane-review
  WaitingQaReview: { labelKey: "attentionQa", color: "#fbbf24" }, // lane-testing
  WaitingUserVerification: { labelKey: "attentionVerify", color: "#06b6d4" }, // accent-cyan
  Blocked: { labelKey: "blocked", color: "#ef4444" }, // error
  OutOfTokens: { labelKey: "attentionTokens", color: "#f59e0b" }, // warning
  WaitingForRunner: { labelKey: "attentionRunner", color: "#f59e0b" }, // warning
  ReasoningTimeout: { labelKey: "attentionTimeout", color: "#f59e0b" }, // warning
};

function TaskCard({ task, onTaskClick, dragDisabled, milestoneName, atRest }: TaskCardProps) {
  const t = useTranslations("dashboard");
  const labels = useLabels();
  const {
    attributes,
    listeners,
    setNodeRef,
    transform,
    isDragging,
  } = useDraggable({ id: task.id, data: { task }, disabled: dragDisabled });

  const status = TASK_STATUS_MAP[task.status];
  const isAgentActive =
    task.currentLane === LANE_KEYS.PLANNING || task.currentLane === LANE_KEYS.DEVELOPING;

  // A merge failure outranks the status label. A task the merge sweep sends back out of
  // Done carries WaitingCodeReview, so status-first rendered "needs code review" on work
  // whose review had already passed — naming the wrong problem and hiding the real one.
  // Same precedence the inbox applies (InboxRanking.ClassifyKind checks merge state first).
  const attention = task.isBlocked
    ? ATTENTION_STATUSES.Blocked
    : (task.mergeFailureReason
      ? { labelKey: "mergeFailed", color: "#f59e0b" }
      : ATTENTION_STATUSES[task.status]);

  const style = {
    ...(transform
      ? { transform: `translate(${transform.x}px, ${transform.y}px)` }
      : {}),
    ...(attention && !isDragging
      ? {
          borderColor: `${attention.color}55`,
          backgroundColor: `${attention.color}0a`,
          boxShadow: `0 0 12px ${attention.color}20`,
        }
      : {}),
  };

  function handleClick() {
    if (!isDragging && onTaskClick) {
      onTaskClick(task.id);
    }
  }

  return (
    <motion.div
      ref={setNodeRef}
      style={style}
      // `layout` is what closes the gap when a card leaves this lane and what
      // slides the cards below it up — the part that makes an agent moving a
      // task read as movement rather than a repaint. It is switched off while
      // dragging because dnd-kit is already driving `transform` on this node,
      // and two systems animating the same property fight visibly.
      layout={!isDragging && !atRest}
      layoutDependency={task.currentLane}
      variants={listItem}
      initial={atRest ? false : "hidden"}
      animate="visible"
      exit="exit"
      transition={SPRING_LAYOUT}
      {...attributes}
      {...listeners}
      className={cn(
        // Deliberately quieter than a GlassCard. The attention states below are already loud —
        // tinted border, glow, pulsing banner — but they only read as loud against something
        // recessive. When every card sat at the panel's own 0.03/0.06, a card that needed a
        // human looked like all the others until you read it.
        "group relative rounded-xl border border-white/[0.04] bg-white/[0.02] backdrop-blur-sm",
        "transition-all duration-200",
        dragDisabled ? "cursor-pointer" : "cursor-grab active:cursor-grabbing",
        "hover:bg-white/[0.06] hover:border-white/[0.1]",
        "hover:shadow-[0_4px_20px_rgba(0,0,0,0.3)]",
        "hover:-translate-y-0.5",
        isAgentActive && !attention && "animate-agent-glow",
        // Only rendered at all when the viewer switched "Include archived" on. Faded so the
        // lane still reads as live work at a glance; the status badge already says "Archived".
        task.isArchived && "opacity-50 hover:opacity-100",
        isDragging && [
          "z-50 scale-[1.03] rotate-[1deg]",
          "shadow-[0_12px_40px_rgba(0,0,0,0.5)]",
          "border-primary-500/30",
        ]
      )}
    >
      {/* Left accent — repo color */}
      <div
        className="absolute left-0 top-2 bottom-2 w-[2px] rounded-full"
        style={{ backgroundColor: task.repoColor ?? "#6b7280" }}
      />

      {/* Unread indicator */}
      {task.hasUnread && (
        <span className="absolute top-2.5 right-2.5 h-2 w-2 rounded-full bg-primary-500 shadow-[0_0_8px_rgba(59,130,246,0.5)] z-10" />
      )}

      <div
        role="button"
        tabIndex={0}
        className="block p-3 pl-4"
        onClick={handleClick}
        onKeyDown={(e) => {
          if (e.key === "Enter" || e.key === " ") {
            e.preventDefault();
            handleClick();
          }
        }}
      >
        {/* Needs-your-attention banner — the pipeline is stalled until the human
            acts. Animated in because it usually appears on a card already on
            screen, mid-run: without a transition the row simply materialises and
            shoves the rest of the card down. */}
        <AnimatePresence initial={false}>
          {attention && (
            <motion.div
              key="attention"
              variants={collapse}
              initial="hidden"
              animate="visible"
              exit="hidden"
              // The height animation lives on this wrapper and the spacing on the
              // child, so the measured "auto" height already includes the margin
              // and the card below does not jump at the end of the transition.
              className="overflow-hidden"
              title={task.mergeFailureReason ?? undefined}
            >
              <div className="mb-2 flex items-center gap-1.5">
                <span className="relative flex h-2 w-2 shrink-0">
                  <span
                    className="absolute inline-flex h-full w-full animate-ping rounded-full opacity-75"
                    style={{ backgroundColor: attention.color }}
                  />
                  <span
                    className="relative inline-flex h-2 w-2 rounded-full"
                    style={{ backgroundColor: attention.color }}
                  />
                </span>
                <span className="text-label" style={{ color: attention.color }}>
                  {t(`taskCard.${attention.labelKey}`)}
                </span>
              </div>
            </motion.div>
          )}
        </AnimatePresence>

        {/* Repo name, cross-repo marker, and the tracker ticket the task came from */}
        {(task.repoName || task.groupId || task.externalKey) && (
          <div className="mb-1.5 flex flex-wrap items-center gap-1">
            {task.externalKey && (
              <ExternalTicketBadge externalKey={task.externalKey} externalUrl={task.externalUrl} />
            )}
            {/* The tracker status move failed for good — the ticket no longer matches the work. */}
            {task.externalSyncError && (
              <span
                className="inline-flex items-center text-amber-400"
                title={task.externalSyncError}
                aria-label={task.externalSyncError}
                role="img"
                data-testid="external-sync-error"
              >
                <svg width="12" height="12" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" aria-hidden>
                  <path d="M8 1.5l6.5 11.5H1.5L8 1.5z" strokeLinejoin="round" />
                  <path d="M8 6.5v3M8 11.5h.01" strokeLinecap="round" />
                </svg>
              </span>
            )}
            {task.repoName && (
              <Badge
                size="sm"
                dot
                dotColor={task.repoColor ?? undefined}
                className="text-gray-400"
              >
                {task.repoName}
              </Badge>
            )}
            {/* Cross-repo marker. Load-bearing on the board rather than decorative: a member
                that is Done with an open PR looks stalled, and this is the only hint that its
                pull request is deliberately being held for its other half. */}
            {/* A Story member: the story's key and its place in it. Every card of one story
                shares the chip's colour, so a story reads as one piece of work across lanes. */}
            {task.groupId && task.storyKey ? (
              <span
                className="inline-flex items-center gap-1 rounded-full border px-1.5 py-0.5 text-label"
                style={{ borderColor: `${storyColor(task.groupId)}66`, color: storyColor(task.groupId) }}
                title={t("taskGroup.storyChipTitle", {
                  key: task.storyKey,
                  order: task.storyOrder ?? 0,
                  count: task.storyCount ?? 0,
                })}
                data-testid="story-chip"
              >
                {t("taskGroup.storyChip", {
                  key: task.storyKey,
                  order: task.storyOrder ?? 0,
                  count: task.storyCount ?? 0,
                })}
              </span>
            ) : task.groupId ? (
              <Badge size="sm" className="text-amber-300/80">
                {t("taskGroup.crossRepo")}
              </Badge>
            ) : null}
          </div>
        )}

        {/* Title row with type icon */}
        <div className="flex items-start gap-1.5">
          <TaskTypeIcon type={task.type} />
          <p className="text-sm font-medium text-gray-200 line-clamp-2">
            <span className="mr-1.5 font-mono text-xs text-gray-400">{task.code}</span>
            {/* The key already shows as the ticket badge — don't say it twice. */}
            {stripTicketKey(task.title, task.externalKey)}
          </p>
        </div>

        {/* Milestone — which ship this card belongs to. The board's milestone filter is off by
            default, so without this a card gives no hint of where it sits in the roadmap. */}
        {milestoneName && (
          <div className="mt-1 flex items-center gap-1 pl-5" title={milestoneName}>
            <svg className="h-3 w-3 shrink-0 text-gray-400" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5">
              <path d="M4 14V2.5h7.5l-1.5 2.5 1.5 2.5H4" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
            <span className="min-w-0 truncate text-xs text-gray-400">{milestoneName}</span>
          </div>
        )}

        {/* Status + Priority row */}
        <div className="mt-2 flex flex-wrap items-center gap-1.5">
          {status && (
            <Badge
              size="sm"
              style={{
                color: status.color,
                borderColor: `${status.color}33`,
                backgroundColor: `${status.color}15`,
              }}
            >
              {labels.taskStatus(task.status)}
            </Badge>
          )}
          {task.priority && (
            <PriorityBadge priority={task.priority} />
          )}
        </div>

        {/* Tags + Branch */}
        {((task.tags && task.tags.length > 0) || task.branchName) && (
          <div className="mt-2 flex flex-wrap items-center gap-1.5">
            {task.tags?.slice(0, 2).map((tag) => (
              <Badge key={tag} size="sm" variant="primary">{tag}</Badge>
            ))}
            {(task.tags?.length ?? 0) > 2 && (
              <Badge size="sm">+{task.tags!.length - 2}</Badge>
            )}
            {task.branchName && (
              <span className="truncate font-mono text-xs text-gray-400 max-w-[120px]">
                {task.branchName}
              </span>
            )}
          </div>
        )}

        {/* Waiting on dependencies — the task is held (agents skip it, forward moves are
            rejected) until every blocker is done AND merged. The tooltip names each blocker
            and its state so the user knows exactly what must finish first. */}
        {task.blockedBy?.length > 0 && task.currentLane !== LANE_KEYS.DONE && (
          <div
            className="mt-2 flex items-center gap-1.5"
            title={[
              t("taskCard.blockedTooltip"),
              ...task.blockedBy.map((b) =>
                b.status === "Completed" && !b.isMerged
                  ? t("taskCard.blockerAwaitingMerge", { code: b.code })
                  : t("taskCard.blockerNotDone", {
                      code: b.code,
                      status: labels.taskStatus(b.status),
                    })
              ),
            ].join("\n")}
          >
            <span className="inline-flex items-center gap-1 rounded-md border border-amber-500/25 bg-amber-500/10 px-1.5 py-0.5">
              <svg className="h-3 w-3 shrink-0 text-amber-400" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5">
                <path d="M6.5 9.5a3 3 0 004.24 0l2.12-2.12a3 3 0 00-4.24-4.24l-1.06 1.06" strokeLinecap="round" strokeLinejoin="round" />
                <path d="M9.5 6.5a3 3 0 00-4.24 0L3.14 8.62a3 3 0 004.24 4.24l1.06-1.06" strokeLinecap="round" strokeLinejoin="round" />
              </svg>
              <span className="text-xs font-medium text-amber-400">
                {task.blockedBy[0].status === "Completed" && !task.blockedBy[0].isMerged
                  ? t("taskCard.waitingOnMerge", { code: task.blockedBy[0].code })
                  : t("taskCard.waitingOn", { code: task.blockedBy[0].code })}
              </span>
              {task.blockedBy.length > 1 && (
                <span className="text-xs text-amber-400/70">
                  {t("taskCard.waitingOnMore", { count: task.blockedBy.length - 1 })}
                </span>
              )}
            </span>
          </div>
        )}

        {task.queuePosition !== null && task.currentLane === LANE_KEYS.READY && (
          <div className="mt-1.5">
            <span className="text-xs text-gray-400">
              {t("taskCard.queuePosition", { position: task.queuePosition })}
            </span>
          </div>
        )}

        {/* Last agent message preview — server-truncated; updates live via SignalR */}
        {task.lastAgentMessage && (
          <div className="mt-2 flex items-start gap-1.5" title={task.lastAgentMessage.preview}>
            <svg
              className="mt-0.5 h-3 w-3 shrink-0 text-gray-400"
              viewBox="0 0 16 16"
              fill="none"
              stroke="currentColor"
              strokeWidth="1.5"
            >
              <path
                d="M8 1.5l1.2 3.3 3.3 1.2-3.3 1.2L8 10.5 6.8 7.2 3.5 6l3.3-1.2z"
                strokeLinejoin="round"
              />
              <path d="M12.5 10.5l.6 1.6 1.6.6-1.6.6-.6 1.6-.6-1.6-1.6-.6 1.6-.6z" strokeLinejoin="round" />
            </svg>
            <p className="min-w-0 text-xs leading-snug text-gray-400 line-clamp-2">
              {task.lastAgentMessage.agentName && (
                <span
                  className={cn(
                    "font-medium",
                    task.lastAgentMessage.messageType === "Question"
                      ? "text-amber-400/90"
                      : task.lastAgentMessage.messageType === "Error"
                        ? "text-red-400/90"
                        : "text-gray-400"
                  )}
                >
                  {task.lastAgentMessage.agentName}:{" "}
                </span>
              )}
              {task.lastAgentMessage.preview}
            </p>
          </div>
        )}

        {/* Agent active indicator — the orca dives while the agent works, so the
            card shows what is happening without needing to be read. */}
        {isAgentActive && !attention && (
          <div className="mt-2 flex items-center gap-1.5">
            {/* "sm", not "xs": at 20px the silhouette stops reading as an orca
                and just looks like a smudge next to the label. */}
            <OrcaStatus lane={task.currentLane as LaneKey} size="sm" />
            <span className="text-xs text-emerald-400/80">
              {t("taskCard.working")}
            </span>
          </div>
        )}
      </div>
    </motion.div>
  );
}

export { TaskCard };
