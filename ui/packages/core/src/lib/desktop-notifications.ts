import type { Notification as AppNotification } from "@/lib/types";

/**
 * OS desktop notifications (browser Notification API — the Teams-style toast).
 * The preference is per-device by nature (permission is granted per browser/origin),
 * so it lives in localStorage rather than the backend.
 */
const STORAGE_KEY = "orkka:desktop-notifications";

export function isDesktopNotificationsSupported(): boolean {
  return typeof window !== "undefined" && "Notification" in window;
}

export function getDesktopNotificationsEnabled(): boolean {
  if (!isDesktopNotificationsSupported()) return false;
  try {
    return localStorage.getItem(STORAGE_KEY) === "true";
  } catch {
    return false;
  }
}

export function setDesktopNotificationsEnabled(enabled: boolean): void {
  try {
    localStorage.setItem(STORAGE_KEY, String(enabled));
  } catch {
    // Storage unavailable (private mode) — the toggle simply won't persist.
  }
}

export function getDesktopNotificationPermission(): NotificationPermission | null {
  return isDesktopNotificationsSupported() ? Notification.permission : null;
}

export async function requestDesktopNotificationPermission(): Promise<NotificationPermission | null> {
  if (!isDesktopNotificationsSupported()) return null;
  return Notification.requestPermission();
}

/**
 * Fire an OS toast for an in-app notification. No-ops unless the device toggle is on
 * and the browser permission is granted. Clicking the toast focuses the app and
 * navigates to the task.
 */
export function showDesktopNotification(
  notification: AppNotification,
  localizedTitle: string,
  navigate: (href: string) => void
): void {
  if (!isDesktopNotificationsSupported()) return;
  if (!getDesktopNotificationsEnabled()) return;
  if (Notification.permission !== "granted") return;

  const toast = new Notification(localizedTitle, {
    body: notification.body ?? notification.title,
    tag: notification.id,
  });

  toast.onclick = () => {
    window.focus();
    if (notification.type === "AgentReplied" && notification.projectSlug && notification.agentId) {
      navigate(`/p/${notification.projectSlug}/team?agent=${notification.agentId}`);
    } else if (notification.projectSlug && notification.taskNumber != null) {
      navigate(`/p/${notification.projectSlug}/tasks/${notification.taskNumber}`);
    }
    toast.close();
  };
}
