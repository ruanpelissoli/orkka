import { useCallback, useEffect, useRef, useState } from "react";
import { useSignalRConnection } from "@/components/providers/SignalRProvider";
import { useClientApi } from "@/hooks/use-client-api";
import { api } from "@/lib/api";
import type { InboxFilterParams, InboxItem, InboxResponse } from "@/lib/types";

interface UseInboxOptions extends InboxFilterParams {
  /** Poll interval as a backstop for events the hub never delivers. 0 disables. */
  pollMs?: number;
}

interface UseInboxResult {
  items: InboxItem[];
  counts: InboxResponse["counts"];
  total: number;
  isLoading: boolean;
  error: string | null;
  refetch: () => Promise<void>;
}

/**
 * The Attention Inbox feed. Refetches rather than patching in place: ranking is a
 * server-side decision that depends on the whole set (staleness, blocked dependents), so a
 * locally-mutated list would drift out of order the moment anything changed.
 */
function useInbox({ scope, projectId, kind, limit, pollMs = 60_000 }: UseInboxOptions = {}): UseInboxResult {
  const withToken = useClientApi();
  const { connection, isConnected } = useSignalRConnection();

  const [state, setState] = useState<Omit<UseInboxResult, "refetch">>({
    items: [],
    counts: [],
    total: 0,
    isLoading: true,
    error: null,
  });

  // Guards against a slow response from a previous filter overwriting a newer one.
  const requestRef = useRef(0);

  // State is only updated in the promise callbacks, so the mount/filter effect below can
  // call this directly.
  const refetch = useCallback((): Promise<void> => {
    const requestId = ++requestRef.current;
    return withToken((token) => api.inbox.list(token, { scope, projectId, kind, limit })).then(
      (result) => {
        if (requestId !== requestRef.current) return;
        setState({
          items: result.items,
          counts: result.counts,
          total: result.total,
          isLoading: false,
          error: null,
        });
      },
      () => {
        if (requestId !== requestRef.current) return;
        setState((prev) => ({ ...prev, isLoading: false, error: "load-failed" }));
      }
    );
  }, [withToken, scope, projectId, kind, limit]);

  // A new filter shows the loading state until its first response lands (adjusted during
  // render; the initial state is already loading).
  const filterKey = JSON.stringify([scope, projectId, kind, limit]);
  const [prevFilterKey, setPrevFilterKey] = useState(filterKey);
  if (filterKey !== prevFilterKey) {
    setPrevFilterKey(filterKey);
    setState((prev) => ({ ...prev, isLoading: true }));
  }

  useEffect(() => {
    void refetch();
  }, [refetch]);

  // Any lane/status movement can add or clear an inbox item, so both events refetch.
  // `LaneChanged`/`StatusChanged` (the hub's actual event names — see TaskHub.cs) only
  // arrive on `board:{id}` / `task:{id}` groups some other subscriber has joined; the
  // inbox is cross-project, so it also listens to `NotificationCreated`, which the hub
  // pushes to the caller's own `user:{id}` group unconditionally and which fires for
  // exactly the "needs a human" moments the inbox surfaces.
  useEffect(() => {
    if (!connection || !isConnected) return;
    const handler = () => void refetch();
    connection.on("LaneChanged", handler);
    connection.on("StatusChanged", handler);
    connection.on("NotificationCreated", handler);
    return () => {
      connection.off("LaneChanged", handler);
      connection.off("StatusChanged", handler);
      connection.off("NotificationCreated", handler);
    };
  }, [connection, isConnected, refetch]);

  useEffect(() => {
    if (!pollMs) return;
    const id = setInterval(() => void refetch(), pollMs);
    return () => clearInterval(id);
  }, [pollMs, refetch]);

  return { ...state, refetch };
}

export { useInbox };
export type { UseInboxOptions, UseInboxResult };
