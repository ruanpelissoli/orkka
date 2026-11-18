import { useCallback, useEffect, useSyncExternalStore } from "react";
import { useSignalRConnection } from "@/components/providers/SignalRProvider";
import { useClientApi } from "@/hooks/use-client-api";
import { api } from "@/lib/api";
import type { Notification } from "@/lib/types";

/** NotificationCreated SignalR payload — a Notification minus isRead (always fresh). */
type NotificationCreatedEvent = Omit<Notification, "isRead"> & { userId: string };

interface UseNotificationsOptions {
  /** Fired for each realtime notification (e.g. to raise an OS toast). */
  onIncoming?: (notification: Notification) => void;
}

interface NotificationState {
  notifications: Notification[];
  unreadCount: number;
  isLoading: boolean;
}

// Module-level store shared by every hook instance. The bell lives in the
// persistent shell while the /notifications page mounts its own instance —
// without shared state, marking notifications read on the page never clears
// the bell's badge.
let state: NotificationState = { notifications: [], unreadCount: 0, isLoading: true };
const subscribers = new Set<() => void>();
const incomingListeners = new Set<(notification: Notification) => void>();
let inflightFetch: Promise<void> | null = null;

function getSnapshot(): NotificationState {
  return state;
}

function subscribe(listener: () => void) {
  subscribers.add(listener);
  return () => {
    subscribers.delete(listener);
  };
}

function setState(next: NotificationState) {
  state = next;
  for (const listener of subscribers) listener();
}

/**
 * Idempotent by id — every mounted instance registers a SignalR handler on the
 * shared connection, so the same event arrives once per instance. Incoming
 * listeners (OS toasts) only fire for the insert, never for the duplicates.
 */
function addNotification(notification: Notification) {
  if (state.notifications.some((n) => n.id === notification.id)) return;
  setState({
    ...state,
    notifications: [notification, ...state.notifications],
    unreadCount: state.unreadCount + 1,
  });
  for (const listener of incomingListeners) listener(notification);
}

function applyMarkRead(id: string) {
  let wasUnread = false;
  const notifications = state.notifications.map((n) => {
    if (n.id === id && !n.isRead) {
      wasUnread = true;
      return { ...n, isRead: true };
    }
    return n;
  });
  setState({
    ...state,
    notifications,
    unreadCount: wasUnread ? Math.max(0, state.unreadCount - 1) : state.unreadCount,
  });
}

function applyMarkAllRead() {
  setState({
    ...state,
    notifications: state.notifications.map((n) => (n.isRead ? n : { ...n, isRead: true })),
    unreadCount: 0,
  });
}

/**
 * The caller's in-app notifications: initial fetch + realtime prepends via the
 * NotificationCreated event (the server auto-joins user:{id} on connect — no group
 * join call needed) + optimistic read state. State is shared across all mounted
 * instances (bell + notifications page) so read actions stay in sync.
 */
function useNotifications({ onIncoming }: UseNotificationsOptions = {}) {
  const withToken = useClientApi();
  const { connection, isConnected } = useSignalRConnection();

  const { notifications, unreadCount, isLoading } = useSyncExternalStore(
    subscribe,
    getSnapshot,
    getSnapshot
  );

  const refetch = useCallback(() => {
    // Bell and page can mount together — share one in-flight request.
    inflightFetch ??= (async () => {
      try {
        const result = await withToken((token) => api.notifications.list(token));
        setState({
          notifications: result.items,
          unreadCount: result.unreadCount,
          isLoading: false,
        });
      } catch {
        // Keep whatever we had — the bell degrades to stale rather than erroring.
        if (state.isLoading) setState({ ...state, isLoading: false });
      } finally {
        inflightFetch = null;
      }
    })();
    return inflightFetch;
  }, [withToken]);

  useEffect(() => {
    void refetch();
  }, [refetch]);

  useEffect(() => {
    if (!onIncoming) return;
    incomingListeners.add(onIncoming);
    return () => {
      incomingListeners.delete(onIncoming);
    };
  }, [onIncoming]);

  useEffect(() => {
    if (!connection || !isConnected) return;

    const handleCreated = (event: NotificationCreatedEvent) => {
      addNotification({ ...event, isRead: false });
    };

    connection.on("NotificationCreated", handleCreated);
    return () => {
      connection.off("NotificationCreated", handleCreated);
    };
  }, [connection, isConnected]);

  const markRead = useCallback(
    (id: string) => {
      applyMarkRead(id);
      withToken((token) => api.notifications.markRead(id, token)).catch(() => {
        void refetch();
      });
    },
    [withToken, refetch]
  );

  const markAllRead = useCallback(() => {
    applyMarkAllRead();
    withToken((token) => api.notifications.markAllRead(token)).catch(() => {
      void refetch();
    });
  }, [withToken, refetch]);

  return { notifications, unreadCount, isLoading, markRead, markAllRead, refetch };
}

export { useNotifications };
export type { UseNotificationsOptions };
