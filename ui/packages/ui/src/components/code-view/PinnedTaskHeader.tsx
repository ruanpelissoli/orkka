import { useTranslations } from "use-intl";
import { Link } from "@/i18n/navigation";
import { Badge } from "@/components/ui";
import { OrcaStatus } from "@/components/animations";
import { LANE_MAP, TASK_STATUS_MAP } from "@/lib/constants";
import { useLabels } from "@/hooks/use-labels";
import type { Task } from "@/lib/types";
import type { WorkspaceTotals } from "./workspace-model";

interface PinnedTaskHeaderProps {
  task: Task;
  projectId: string;
  agentActive: boolean;
  /** Name of the agent currently streaming, when known (from stream event metadata). */
  agentName: string | null;
  totals: WorkspaceTotals;
  onDeselect: () => void;
}

/**
 * The selected task, fixed at the top of the left column: what it is, who is on it, where it
 * stands, and how much code has moved. Everything else about the task lives in the chat below
 * or on the full task page (linked).
 */
function PinnedTaskHeader({ task, projectId, agentActive, agentName, totals, onDeselect }: PinnedTaskHeaderProps) {
  const t = useTranslations("dashboard.codeView");
  const labels = useLabels();
  const lane = LANE_MAP[task.currentLane];
  const status = TASK_STATUS_MAP[task.status];

  return (
    <div className="shrink-0 border-b border-white/[0.06] bg-primary-500/[0.05] px-3 py-2.5">
      <div className="flex items-start gap-2.5">
        <OrcaStatus lane={task.currentLane} size="sm" className="mt-0.5 shrink-0" />
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2">
            <span className="font-mono text-xs text-gray-400">{task.code}</span>
            <Link
              href={`/p/${projectId}/tasks/${task.number}`}
              className="text-xs text-primary-400 underline-offset-2 hover:underline"
            >
              {t("header.openTask")}
            </Link>
          </div>
          <h2 className="mt-0.5 line-clamp-2 font-display text-sm font-semibold leading-snug text-white">
            {task.title}
          </h2>
          <div className="mt-1.5 flex flex-wrap items-center gap-1.5">
            <Badge size="sm" dot dotColor={lane?.color}>
              {labels.lane(task.currentLane)}
            </Badge>
            {agentActive ? (
              <span className="flex items-center gap-1.5 text-xs text-cyan-400">
                <span className="relative flex h-1.5 w-1.5" aria-hidden="true">
                  <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-cyan-400 opacity-75" />
                  <span className="relative inline-flex h-1.5 w-1.5 rounded-full bg-cyan-400" />
                </span>
                {agentName ? t("header.agentWorking", { agent: agentName }) : t("header.working")}
              </span>
            ) : (
              status && <span className="text-xs text-gray-400">{labels.taskStatus(task.status)}</span>
            )}
            {(totals.additions > 0 || totals.deletions > 0) && (
              <span className="font-mono text-xs tabular-nums">
                <span className="text-emerald-400">+{totals.additions}</span>{" "}
                <span className="text-red-400">-{totals.deletions}</span>
              </span>
            )}
          </div>
        </div>
        <button
          type="button"
          onClick={onDeselect}
          className="shrink-0 rounded-md p-1 text-gray-400 transition-colors hover:bg-white/[0.06] hover:text-gray-200"
          aria-label={t("header.deselect")}
          title={t("header.deselect")}
        >
          <svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" aria-hidden="true">
            <path d="M4 4l8 8M12 4l-8 8" strokeLinecap="round" />
          </svg>
        </button>
      </div>
    </div>
  );
}

export { PinnedTaskHeader };
