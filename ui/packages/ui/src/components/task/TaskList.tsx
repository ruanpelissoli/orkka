import { Link } from "@/i18n/navigation";
import { cn } from "@/lib/utils";
import { PriorityBadge } from "@/components/ui";
import { LANE_MAP, TASK_STATUS_MAP } from "@/lib/constants";
import { useLabels } from "@/hooks/use-labels";
import { formatRelativeTime } from "@/lib/utils";
import type { Task } from "@/lib/types";
import { useTranslations } from "use-intl";

interface TaskListProps {
  tasks: Task[];
  projectId: string;
}

function TaskTypeIconInline({ type }: { type: string }) {
  const cls = "w-3.5 h-3.5 shrink-0";
  switch (type) {
    case "BugFix":
      return (
        <svg className={cn(cls, "text-red-400")} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5">
          <circle cx="8" cy="8" r="3" />
          <path d="M8 1v4M8 11v4M1 8h4M11 8h4" strokeLinecap="round" />
        </svg>
      );
    case "Refactor":
      return (
        <svg className={cn(cls, "text-cyan-400")} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5">
          <path d="M2 8h5l-2-3M14 8H9l2 3" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      );
    case "Improvement":
      return (
        <svg className={cn(cls, "text-amber-400")} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5">
          <path d="M8 2l1.5 3 3.5.5-2.5 2.5.5 3.5L8 10l-3 1.5.5-3.5L3 5.5 6.5 5z" strokeLinejoin="round" />
        </svg>
      );
    default:
      return (
        <svg className={cn(cls, "text-violet-400")} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5">
          <path d="M8 2a4 4 0 014 4c0 1.5-.8 2.5-1.5 3.5S9 11 9 12H7c0-1-.8-2-1.5-3S4 7.5 4 6a4 4 0 014-4z" />
          <path d="M6.5 14h3" strokeLinecap="round" />
        </svg>
      );
  }
}

function TaskList({ tasks, projectId }: TaskListProps) {
  const t = useTranslations("dashboard");
  const labels = useLabels();

  if (tasks.length === 0) {
    return (
      <div className="rounded-2xl border border-white/[0.06] bg-white/[0.02] py-16 text-center">
        <p className="text-sm text-gray-400">{t("taskList.noTasksFound")}</p>
      </div>
    );
  }

  return (
    <div className="rounded-2xl border border-white/[0.06] bg-white/[0.02] overflow-hidden">
      {/* Header */}
      <div className="grid grid-cols-[auto_1fr_auto_auto_auto_auto] gap-4 px-4 py-2.5 text-label border-b border-white/[0.04]">
        <span className="w-4" />
        <span>{t("taskList.title")}</span>
        <span className="w-20 text-center">{t("taskList.priority")}</span>
        <span className="w-28 text-center">{t("taskList.lane")}</span>
        <span className="w-24 text-center">{t("taskList.status")}</span>
        <span className="w-20 text-right">{t("taskList.created")}</span>
      </div>

      {/* Rows */}
      {tasks.map((task) => {
        const lane = LANE_MAP[task.currentLane];
        const status = TASK_STATUS_MAP[task.status];

        return (
          <Link
            key={task.id}
            href={`/p/${projectId}/tasks/${task.number}`}
            className="grid grid-cols-[auto_1fr_auto_auto_auto_auto] gap-4 items-center px-4 py-3 border-b border-white/[0.03] transition-colors hover:bg-white/[0.03] last:border-b-0"
          >
            <TaskTypeIconInline type={task.type} />
            <div className="min-w-0">
              <p className="text-sm text-gray-200 truncate">
                <span className="mr-2 font-mono text-xs text-gray-400">{task.code}</span>
                {task.title}
              </p>
              {task.repoName && (
                <p className="text-xs text-gray-400 font-mono truncate">
                  {task.repoName}
                </p>
              )}
            </div>
            <div className="w-20 flex justify-center">
              {task.priority && (
                <PriorityBadge priority={task.priority} />
              )}
            </div>
            <div className="w-28 flex justify-center">
              {lane && (
                <span
                  className="rounded-full px-2 py-0.5 text-xs font-medium"
                  style={{
                    backgroundColor: `${lane.color}15`,
                    color: lane.color,
                  }}
                >
                  {labels.lane(task.currentLane)}
                </span>
              )}
            </div>
            <div className="w-24 flex justify-center">
              {status && (
                <span
                  className="rounded-full px-2 py-0.5 text-xs font-medium"
                  style={{
                    backgroundColor: `${status.color}15`,
                    color: status.color,
                  }}
                >
                  {labels.taskStatus(task.status)}
                </span>
              )}
            </div>
            <span className="w-20 text-right text-xs text-gray-400">
              {formatRelativeTime(task.createdAt)}
            </span>
          </Link>
        );
      })}
    </div>
  );
}

export { TaskList };
