import { useCallback, useEffect, useRef, useState } from "react";
import { useClientApi } from "@/hooks/use-client-api";
import { useBoardSignalR } from "@/hooks/use-board-signalr";
import { api } from "@/lib/api";
import { normalizeBoardLaneKey } from "@/lib/constants";
import type { AgentStatsWindow, BoardMetricsResponse } from "@/lib/types";

interface UseBoardMetricsOptions {
  /** Route ref (slug or ULID) — what the API calls take. */
  projectId: string;
  /** Project ULID — the SignalR board group key. */
  projectUlid: string;
  window: AgentStatsWindow;
  /** Server-rendered envelope for the default window, so the first paint has numbers. */
  initial?: BoardMetricsResponse | null;
  /** False = no fetches and no hub subscription (the workspace board: metrics are per project). */
  enabled?: boolean;
}

interface UseBoardMetricsResult {
  metrics: BoardMetricsResponse | null;
  isLoading: boolean;
  refetch: () => Promise<void>;
}

/** A lane move fires several hub events in quick succession; one refetch is enough. */
const REFETCH_DEBOUNCE_MS = 1500;

/**
 * The board's strip and per-lane averages.
 *
 * Event-driven rather than polled. The endpoint folds several queries per call, and a board is
 * something people leave open all day — a timer on every open board would spend far more than the
 * numbers are worth, while the hub already tells us the only things that move them: a run
 * finishing, a task changing lane, a merge failing.
 *
 * A failed fetch keeps the last good numbers rather than blanking the strip. Metrics are context,
 * not the page; flickering to an error state because one poll lost the network is worse than
 * showing figures that are a minute stale.
 */
function useBoardMetrics({
  projectId,
  projectUlid,
  window,
  initial,
  enabled = true,
}: UseBoardMetricsOptions): UseBoardMetricsResult {
  const withToken = useClientApi();
  // A disabled strip counts as seeded: nothing to load, so never a skeleton.
  const seeded = !enabled || Boolean(initial && initial.window === window);
  const [metrics, setMetrics] = useState<BoardMetricsResponse | null>(
    initial ? normalizeLaneKeys(initial) : null
  );
  const [isLoading, setIsLoading] = useState(!seeded);
  const requestRef = useRef(0);

  const refetch = useCallback(async () => {
    if (!enabled) return;
    const requestId = ++requestRef.current;
    try {
      const envelope = await withToken((token) => api.board.metrics(projectId, window, token));
      if (requestId !== requestRef.current) return;
      setMetrics(normalizeLaneKeys(envelope));
    } catch {
      // Deliberately silent — see the note above about keeping the last good numbers.
    } finally {
      if (requestId === requestRef.current) setIsLoading(false);
    }
  }, [withToken, projectId, window, enabled]);

  // Skip the very first fetch when the server already handed us this window; any window change
  // after that is a real request.
  const skipFirstRef = useRef(seeded);
  useEffect(() => {
    if (skipFirstRef.current) {
      skipFirstRef.current = false;
      return;
    }
    setIsLoading(true);
    void refetch();
  }, [refetch]);

  const debounceRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const scheduleRefetch = useCallback(() => {
    if (debounceRef.current) clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(() => void refetch(), REFETCH_DEBOUNCE_MS);
  }, [refetch]);

  useEffect(() => () => {
    if (debounceRef.current) clearTimeout(debounceRef.current);
  }, []);

  useBoardSignalR({
    projectId: enabled ? projectUlid : undefined,
    onStatusChanged: scheduleRefetch,
    onLaneChanged: scheduleRefetch,
    onTaskMergeFailed: scheduleRefetch,
  });

  return { metrics, isLoading, refetch };
}

/**
 * The metrics endpoint keys lanes the way the board endpoint does — snake_case, one wire dialect.
 * Everything on the client compares against the enum names, so normalize here for the same reason
 * `use-live-lanes` normalizes the board: a lane key in the wrong dialect does not error, it just
 * silently never matches, and the column quietly shows no metric.
 */
function normalizeLaneKeys(envelope: BoardMetricsResponse): BoardMetricsResponse {
  return {
    ...envelope,
    lanes: Object.fromEntries(
      Object.entries(envelope.lanes).map(([key, value]) => [normalizeBoardLaneKey(key), value])
    ),
  };
}

export { useBoardMetrics };
export type { UseBoardMetricsOptions, UseBoardMetricsResult };
