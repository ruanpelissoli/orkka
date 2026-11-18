import { useTranslations } from "use-intl";
import { useRouter } from "@/i18n/navigation";
import { GlassCard } from "@/components/ui";
import { NotificationList } from "@/components/notifications/NotificationList";
import { useNotifications } from "@/hooks/use-notifications";
import type { Notification } from "@/lib/types";

export function NotificationsPageClient() {
  const t = useTranslations("dashboard");
  const router = useRouter();
  const { notifications, unreadCount, markRead, markAllRead, isLoading } = useNotifications();

  const handleSelect = (notification: Notification) => {
    if (!notification.isRead) markRead(notification.id);
    if (notification.type === "ScheduleFailed" && notification.projectSlug) {
      router.push(`/p/${notification.projectSlug}/automation`);
    } else if (notification.projectSlug && notification.taskNumber != null) {
      router.push(`/p/${notification.projectSlug}/tasks/${notification.taskNumber}`);
    }
  };

  return (
    <div className="mx-auto max-w-3xl p-4 lg:p-8">
      <div className="mb-6 flex items-center justify-between">
        <h1 className="font-display text-2xl font-bold text-white">
          {t("notifications.title")}
        </h1>
        {unreadCount > 0 && (
          <button
            type="button"
            onClick={markAllRead}
            className="text-sm text-primary-400 transition-colors hover:text-primary-300"
          >
            {t("notifications.markAllRead")}
          </button>
        )}
      </div>

      <GlassCard padding="none">
        {isLoading ? (
          <div className="px-4 py-8 text-center text-sm text-gray-400">…</div>
        ) : (
          <NotificationList
            notifications={notifications}
            onSelect={handleSelect}
            className="divide-y divide-white/[0.03]"
          />
        )}
      </GlassCard>
    </div>
  );
}
