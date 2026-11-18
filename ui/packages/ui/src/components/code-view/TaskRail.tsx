import { useMemo, useState } from "react";
import { useTranslations } from "use-intl";
import { cn } from "@/lib/utils";
import { Badge } from "@/components/ui";
import { OrcaStatus } from "@/components/animations";
import { TASK_STATUS_MAP, LANE_MAP } from "@/lib/constants";
import { useLabels } from "@/hooks/use-labels";
import type { BoardLaneResponse, TaskSummary } from "@/lib/types";
import { buildRailModel, isAgentActive } from "./rail-model";

interface TaskRailProps {
  lanes: BoardLaneResponse[];
  selectedTaskId: string | null;
  onSelect: (taskId: string) => void;
  /** Hide the selected task from the list (it is pinned above). */
  hideSelected?: boolean;
  className?: string;
}

/** Statuses where a human is holding things up — the card names it. */
const ATTENTION_KEYS: Record<string, string> = {
  WaitingAnswer: "attention.answer",
  WaitingUserVerification: "attention.verify",
  WaitingCodeReview: "attention.review",
  WaitingQaReview: "attention.qa",
  Blocked: "attention.blocked",
  OutOfTokens: "attention.tokens",
  WaitingForRunner: "attention.runner",
  ReasoningTimeout: "attention.timeout",
};

function TaskRailCard({
  task,
  selected,
  onSelect,
}: {
  task: TaskSummary;
  selected: boolean;
  onSelect: (id: string) => void;
}) {
  const t = useTranslations("dashboard.codeView");
  const active = isAgentActive(task);
  // Merge failure before status — see the note in TaskCard: a task sent back out of Done
  // is in WaitingCodeReview, and "needs your review" would describe the wrong thing.
  const attentionKey = task.isBlocked
    ? ATTENTION_KEYS.Blocked
    : task.mergeFailureReason
      ? "attention.merge"
      : ATTENTION_KEYS[task.status];
  const labels = useLabels();
  const laneLabel = labels.lane(task.currentLane);
  const status = TASK_STATUS_MAP[task.status];

  return (
    <button
      type="button"
      onClick={() => onSelect(task.id)}
      aria-current={selected ? "true" : undefined}
      className={cn(
        "group flex w-full items-start gap-2.5 border-l-2 px-3 py-2.5 text-left transition-colors",
        selected
          ? "border-primary-400/80 bg-primary-500/[0.08]"
          : "border-transparent hover:bg-white/[0.04]"
      )}
    >
      <OrcaStatus lane={task.currentLane} size="xs" showHalo={false} className="mt-0.5 shrink-0" />
      <span className="min-w-0 flex-1">
        <span className="flex items-center gap-2">
          <span className="font-mono text-xs text-gray-400">{task.code}</span>
          {task.hasUnread && !selected && (
            <span className="h-1.5 w-1.5 rounded-full bg-primary-400" aria-label={t("rail.unread")} />
          )}
          {task.repoName && (
            <span className="flex min-w-0 items-center gap-1 text-xs text-gray-400">
              <span
                className="h-1.5 w-1.5 shrink-0 rounded-full"
                style={{ backgroundColor: task.repoColor ?? "var(--color-gray-500)" }}
                aria-hidden="true"
              />
              <span className="truncate">{task.repoName}</span>
            </span>
          )}
        </span>
        <span className="mt-0.5 line-clamp-2 block text-sm leading-snug text-gray-100">{task.title}</span>
        <span className="mt-1.5 flex flex-wrap items-center gap-1.5">
          <Badge size="sm" dot dotColor={LANE_MAP[task.currentLane]?.color}>
            {laneLabel}
          </Badge>
          {active && (
            <span className="text-xs text-cyan-400">{t("rail.working")}</span>
          )}
          {!active && attentionKey && (
            <span className="text-xs text-amber-400">{t(attentionKey)}</span>
          )}
          {!active && !attentionKey && status && task.currentLane !== "Done" && (
            <span className="text-xs text-gray-400">{labels.taskStatus(task.status)}</span>
          )}
        </span>
      </span>
    </button>
  );
}

function TaskRail({ lanes, selectedTaskId, onSelect, hideSelected = false, className }: TaskRailProps) {
  const t = useTranslations("dashboard.codeView");
  const rail = useMemo(() => buildRailModel(lanes), [lanes]);
  const [doneOpen, setDoneOpen] = useState(false);

  const active = hideSelected ? rail.active.filter((x) => x.id !== selectedTaskId) : rail.active;
  const done = hideSelected ? rail.done.filter((x) => x.id !== selectedTaskId) : rail.done;

  if (rail.active.length === 0 && rail.done.length === 0) {
    return (
      <div className={cn("p-4 text-center text-xs text-gray-400", className)}>{t("rail.empty")}</div>
    );
  }

  return (
    <div className={cn("flex flex-col", className)}>
      {active.length > 0 && (
        <div className="px-3 pb-1 pt-2 text-label">{t("rail.inFlight", { count: active.length })}</div>
      )}
      {active.map((task) => (
        <TaskRailCard key={task.id} task={task} selected={task.id === selectedTaskId} onSelect={onSelect} />
      ))}

      {(done.length > 0 || rail.doneHidden > 0) && (
        <div className="mt-2 border-t border-white/[0.06]">
          <button
            type="button"
            onClick={() => setDoneOpen((o) => !o)}
            aria-expanded={doneOpen}
            className="flex w-full items-center gap-1.5 px-3 py-2 text-left text-label hover:text-gray-200"
          >
            <svg
              width="10"
              height="10"
              viewBox="0 0 16 16"
              fill="none"
              stroke="currentColor"
              strokeWidth="2"
              aria-hidden="true"
              className={cn("shrink-0 transition-transform", !doneOpen && "-rotate-90")}
            >
              <path d="M4 6l4 4 4-4" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
            {t("rail.recentlyDone", { count: done.length })}
          </button>
          {doneOpen &&
            done.map((task) => (
              <TaskRailCard key={task.id} task={task} selected={task.id === selectedTaskId} onSelect={onSelect} />
            ))}
          {doneOpen && rail.doneHidden > 0 && (
            <p className="px-3 pb-2 text-xs text-gray-400">{t("rail.doneHidden", { count: rail.doneHidden })}</p>
          )}
        </div>
      )}
    </div>
  );
}

export { TaskRail, TaskRailCard };
