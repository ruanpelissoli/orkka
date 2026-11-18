import { useTranslations } from "use-intl";
import { cn, formatRelativeTime } from "@/lib/utils";
import type { Notification, NotificationTypeKey } from "@/lib/types";

const TYPE_COLORS: Record<NotificationTypeKey, string> = {
  PlanApprovalRequired: "#a78bfa",
  QuestionAsked: "#f59e0b",
  CodeReviewRequired: "#06b6d4",
  QaReviewRequired: "#fbbf24",
  TaskFailed: "#ef4444",
  TaskMergeFailed: "#ef4444",
  TaskCompleted: "#10b981",
  AgentReplied: "#22d3ee",
  VerificationRequired: "#22d3ee",
  RunnerOffline: "#f59e0b",
  MilestoneCompleted: "#10b981",
  // Red, not the milestone green: the work finished but the merge did not, and the
  // roadmap is held until someone acts.
  MilestoneShipFailed: "#ef4444",
  AutopilotStopped: "#f59e0b",
  ApprovalRequested: "#f59e0b",
  ScheduleFailed: "#ef4444",
};

interface NotificationListProps {
  notifications: Notification[];
  onSelect: (notification: Notification) => void;
  className?: string;
}

/**
 * Rows shared by the bell dropdown and the full /notifications page. Display text is
 * localized from `type`; the row body is the task title (plus reason when present).
 */
function NotificationList({ notifications, onSelect, className }: NotificationListProps) {
  const t = useTranslations("dashboard");

  if (notifications.length === 0) {
    return (
      <div className={cn("px-4 py-8 text-center", className)}>
        <p className="text-sm font-medium text-gray-300">{t("notifications.empty")}</p>
        <p className="mt-1 text-xs text-gray-400">{t("notifications.emptyDescription")}</p>
      </div>
    );
  }

  return (
    <ul className={className}>
      {notifications.map((notification) => {
        const clickable =
          notification.projectSlug !== null
          && (notification.taskNumber !== null || notification.agentId !== null);
        return (
          <li key={notification.id}>
            <button
              type="button"
              disabled={!clickable}
              onClick={() => onSelect(notification)}
              className={cn(
                "flex w-full items-start gap-3 px-4 py-3 text-left transition-colors",
                clickable ? "hover:bg-white/[0.04]" : "cursor-default"
              )}
            >
              <span
                className="mt-1.5 h-2 w-2 shrink-0 rounded-full"
                style={{
                  backgroundColor: notification.isRead
                    ? "transparent"
                    : TYPE_COLORS[notification.type] ?? "#6b7280",
                  boxShadow: notification.isRead
                    ? `inset 0 0 0 1px ${TYPE_COLORS[notification.type] ?? "#6b7280"}`
                    : undefined,
                }}
              />
              <span className="min-w-0 flex-1">
                <span
                  className={cn(
                    "block text-sm",
                    notification.isRead ? "text-gray-400" : "font-medium text-gray-100"
                  )}
                >
                  {t(`notifications.types.${notification.type}`)}
                </span>
                <span className="mt-0.5 block truncate text-xs text-gray-400">
                  {notification.title}
                  {notification.body ? ` — ${notification.body}` : ""}
                </span>
              </span>
              <span className="mt-0.5 shrink-0 text-xs text-gray-400">
                {formatRelativeTime(notification.createdAt)}
              </span>
            </button>
          </li>
        );
      })}
    </ul>
  );
}

export { NotificationList };
export type { NotificationListProps };
