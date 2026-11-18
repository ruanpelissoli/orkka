import { useCallback, useEffect, useRef, useState } from "react";
import { AnimatePresence, motion } from "framer-motion";
import { useTranslations } from "use-intl";
import { Link, useRouter } from "@/i18n/navigation";
import { cn } from "@/lib/utils";
import { SPRING_LAYOUT } from "@/lib/motion";
import { useNotifications } from "@/hooks/use-notifications";
import { showDesktopNotification } from "@/lib/desktop-notifications";
import type { Notification } from "@/lib/types";
import { NotificationList } from "./NotificationList";
import { DesktopNotificationsToggle } from "./DesktopNotificationsToggle";

const PANEL_LIMIT = 10;

interface NotificationBellProps {
  /** Horizontal panel anchoring relative to the bell. */
  align?: "left" | "right";
  /** Open the panel above the bell (e.g. mounted in a sidebar footer). */
  openUp?: boolean;
  className?: string;
}

/**
 * Bell icon + unread badge + recent-notifications panel, rendered in the TopBar — auth,
 * SignalR, and navigation come from the app's contracts.
 */
function NotificationBell({ align = "right", openUp = false, className }: NotificationBellProps) {
  const t = useTranslations("dashboard");
  const router = useRouter();
  const [open, setOpen] = useState(false);
  const containerRef = useRef<HTMLDivElement>(null);

  const tRef = useRef(t);
  const routerRef = useRef(router);
  useEffect(() => {
    tRef.current = t;
    routerRef.current = router;
  }, [t, router]);

  const onIncoming = useCallback((notification: Notification) => {
    showDesktopNotification(
      notification,
      tRef.current(`notifications.types.${notification.type}`),
      (href) => routerRef.current.push(href)
    );
  }, []);

  const { notifications, unreadCount, markRead, markAllRead } = useNotifications({ onIncoming });

  useEffect(() => {
    if (!open) return;
    const handleClickOutside = (e: MouseEvent) => {
      if (containerRef.current && !containerRef.current.contains(e.target as Node)) {
        setOpen(false);
      }
    };
    const handleEscape = (e: KeyboardEvent) => {
      if (e.key === "Escape") setOpen(false);
    };
    document.addEventListener("mousedown", handleClickOutside);
    document.addEventListener("keydown", handleEscape);
    return () => {
      document.removeEventListener("mousedown", handleClickOutside);
      document.removeEventListener("keydown", handleEscape);
    };
  }, [open]);

  const handleSelect = (notification: Notification) => {
    setOpen(false);
    if (!notification.isRead) markRead(notification.id);
    if (notification.type === "AgentReplied" && notification.projectSlug && notification.agentId) {
      router.push(`/p/${notification.projectSlug}/team?agent=${notification.agentId}`);
    } else if (notification.type === "ScheduleFailed" && notification.projectSlug) {
      router.push(`/p/${notification.projectSlug}/automation`);
    } else if (notification.projectSlug && notification.taskNumber != null) {
      router.push(`/p/${notification.projectSlug}/tasks/${notification.taskNumber}`);
    }
  };

  return (
    <div ref={containerRef} className={cn("relative", className)}>
      <button
        type="button"
        aria-label={t("notifications.bellLabel")}
        onClick={() => setOpen((prev) => !prev)}
        className={cn(
          "relative rounded-lg p-2 text-gray-400 transition-colors",
          "hover:bg-white/[0.04] hover:text-gray-200",
          open && "bg-white/[0.04] text-gray-200"
        )}
      >
        <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5">
          <path d="M18 8A6 6 0 006 8c0 7-3 9-3 9h18s-3-2-3-9M13.73 21a2 2 0 01-3.46 0" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
        {/* The badge is the only thing that moves when a notification arrives
            while you are looking elsewhere on the page, so it gets a pop rather
            than appearing fully formed. Keyed on the count so an incoming
            notification re-runs the animation on an already-visible badge. */}
        <AnimatePresence initial={false}>
          {unreadCount > 0 && (
            <motion.span
              key={unreadCount}
              initial={{ scale: 0.4, opacity: 0 }}
              animate={{ scale: 1, opacity: 1 }}
              exit={{ scale: 0.4, opacity: 0 }}
              transition={SPRING_LAYOUT}
              className="absolute -right-0.5 -top-0.5 flex h-4 min-w-4 items-center justify-center rounded-full bg-primary-500 px-1 text-xs font-semibold text-white"
            >
              {unreadCount > 99 ? "99+" : unreadCount}
            </motion.span>
          )}
        </AnimatePresence>
      </button>

      <AnimatePresence>
        {open && (
          <motion.div
            initial={{ opacity: 0, y: openUp ? 4 : -4, scale: 0.98 }}
            animate={{ opacity: 1, y: 0, scale: 1 }}
            exit={{ opacity: 0, y: openUp ? 4 : -4, scale: 0.98 }}
            transition={{ duration: 0.12 }}
            className={cn(
              "absolute z-50 w-80 overflow-hidden rounded-xl border border-white/[0.06] bg-gray-900/95 shadow-xl backdrop-blur-xl",
              align === "right" ? "right-0" : "left-0",
              openUp ? "bottom-full mb-2" : "top-full mt-2"
            )}
          >
            <div className="flex items-center justify-between border-b border-white/[0.04] px-4 py-2.5">
              <span className="text-sm font-medium text-gray-200">
                {t("notifications.title")}
              </span>
              {unreadCount > 0 && (
                <button
                  type="button"
                  onClick={markAllRead}
                  className="text-xs text-primary-400 transition-colors hover:text-primary-300"
                >
                  {t("notifications.markAllRead")}
                </button>
              )}
            </div>

            <div className="max-h-96 overflow-y-auto">
              <NotificationList
                notifications={notifications.slice(0, PANEL_LIMIT)}
                onSelect={handleSelect}
              />
            </div>

            <div className="space-y-2 border-t border-white/[0.04] px-4 py-2.5">
              <DesktopNotificationsToggle compact />
              <Link
                href="/notifications"
                onClick={() => setOpen(false)}
                className="block text-xs text-gray-400 transition-colors hover:text-gray-300"
              >
                {t("notifications.viewAll")}
              </Link>
            </div>
          </motion.div>
        )}
      </AnimatePresence>
    </div>
  );
}

export { NotificationBell };
