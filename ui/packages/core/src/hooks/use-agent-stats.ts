import { useCallback, useEffect, useRef, useState } from "react";
import { useClientApi } from "@/hooks/use-client-api";
import { api } from "@/lib/api";
import type { AgentStats, AgentStatsEnvelope, AgentStatsWindow } from "@/lib/types";

interface UseAgentStatsOptions {
  projectId: string;
  window: AgentStatsWindow;
  /** Server-rendered envelope for the initial window, so the first paint has numbers. */
  initial?: AgentStatsEnvelope | null;
  /** Poll interval as a backstop for runs that finish while the page is open. 0 disables. */
  pollMs?: number;
}

interface UseAgentStatsResult {
  byAgent: ReadonlyMap<string, AgentStats>;
  isLoading: boolean;
  error: string | null;
  refetch: () => Promise<void>;
}

const EMPTY: ReadonlyMap<string, AgentStats> = new Map();

/**
 * Per-agent operational counters for the Team page. Keyed by agent id so cards can look
 * themselves up in O(1); refetches on window change and on a slow poll (runs finish while
 * the user is looking at the page, and the board hub does not carry execution events).
 */
function useAgentStats({ projectId, window, initial, pollMs = 30_000 }: UseAgentStatsOptions): UseAgentStatsResult {
  const withToken = useClientApi();
  const [byAgent, setByAgent] = useState<ReadonlyMap<string, AgentStats>>(() =>
    initial && initial.window === window ? toMap(initial) : EMPTY
  );
  const [isLoading, setIsLoading] = useState(!(initial && initial.window === window));
  const [error, setError] = useState<string | null>(null);
  const requestRef = useRef(0);

  const refetch = useCallback(async () => {
    const requestId = ++requestRef.current;
    try {
      const envelope = await withToken((token) => api.agents.stats(projectId, window, token));
      if (requestId !== requestRef.current) return;
      setByAgent(toMap(envelope));
      setError(null);
    } catch {
      if (requestId !== requestRef.current) return;
      setError("load-failed");
    } finally {
      if (requestId === requestRef.current) setIsLoading(false);
    }
  }, [withToken, projectId, window]);

  // Skip the very first fetch when the server already handed us this window.
  const skipFirstRef = useRef(Boolean(initial && initial.window === window));
  useEffect(() => {
    if (skipFirstRef.current) {
      skipFirstRef.current = false;
      return;
    }
    setIsLoading(true);
    void refetch();
  }, [refetch]);

  useEffect(() => {
    if (!pollMs) return;
    const id = setInterval(() => void refetch(), pollMs);
    return () => clearInterval(id);
  }, [pollMs, refetch]);

  return { byAgent, isLoading, error, refetch };
}

function toMap(envelope: AgentStatsEnvelope): ReadonlyMap<string, AgentStats> {
  return new Map(envelope.agents.map((a) => [a.agentId, a]));
}

export { useAgentStats };
export type { UseAgentStatsOptions, UseAgentStatsResult };
