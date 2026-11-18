import { useEffect, useState } from "react";
import { api } from "@/lib/api";
import type { MyRunnerStatus } from "@/lib/types";
import { useClientApi } from "./use-client-api";

/**
 * The caller's own runner presence, polled. DB-backed on the API side (no hub state), so a
 * 30s interval is the freshness/traffic balance the settings page already uses. Null until
 * the first answer arrives — callers treat that as "unknown", not "offline".
 */
export function useRunnerStatus(): MyRunnerStatus | null {
  const withToken = useClientApi();
  const [status, setStatus] = useState<MyRunnerStatus | null>(null);

  useEffect(() => {
    let disposed = false;

    const load = () =>
      withToken((token) => api.runner.status(token))
        .then((next) => {
          if (!disposed) setStatus(next);
        })
        .catch(() => {
          // Transient fetch failure: keep the last known answer rather than flapping the UI.
        });

    void load();
    const timer = setInterval(() => void load(), 30_000);
    return () => {
      disposed = true;
      clearInterval(timer);
    };
  }, [withToken]);

  return status;
}
