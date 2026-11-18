import { useCallback, useEffect, useRef, useState } from "react";
import { useSignalRConnection } from "@/components/providers/SignalRProvider";
import { trackGroupJoin, trackGroupLeave } from "@/lib/signalr";
import { api } from "@/lib/api";
import type { AppRunSnapshot } from "@/lib/types";
import { useClientApi } from "@/hooks/use-client-api";

export type { AppRunSnapshot };

/**
 * Transport for the Run panel: commands go over REST (relayed by the API to the
 * worker pod holding this user's runner socket), output and state come back over the
 * existing tasks hub — the worker forwards them into the caller's per-(project, user)
 * apprun group, so nothing here opens a second connection.
 *
 * Output does not flow through React state — a dev server emits far too
 * many frames for that; xterm subscribers receive raw base64 chunks directly.
 */
export function useAppRun(projectId: string | null) {
  const { connection, isConnected } = useSignalRConnection();
  const withToken = useClientApi();

  const [snapshots, setSnapshots] = useState<AppRunSnapshot[]>([]);
  const [supported, setSupported] = useState(false);
  // True once the initial attach has answered — the moment "nothing is running" is a fact
  // rather than "we have not asked yet". Auto-start keys off this.
  const [attached, setAttached] = useState(false);
  const [boundProjectId, setBoundProjectId] = useState<string | null>(projectId);
  const outputHandlers = useRef(new Map<string, (base64: string) => void>());

  // Clearing the previous project's runs is derived state, not an effect: doing it in an
  // effect would paint one project's runs under another project's label for a frame.
  if (projectId !== boundProjectId) {
    setBoundProjectId(projectId);
    setSnapshots([]);
    setSupported(false);
    setAttached(false);
  }

  useEffect(() => {
    if (!projectId || !connection || !isConnected) return;
    let disposed = false;

    // Join is idempotent server-side and always sent (a reconnect needs the re-join);
    // Leave is ref-counted like every other shared-connection group.
    trackGroupJoin(`apprun:${projectId}`);
    connection.invoke("JoinAppRun", projectId).catch(() => {});

    const handleOutput = (runId: string, base64: string) => {
      outputHandlers.current.get(runId)?.(base64);
    };
    const handleState = (next: AppRunSnapshot[]) => {
      if (disposed) return;
      // Broadcast state travels without scrollback (hub message-size limits on the
      // worker→API hop) — keep the copy the attach response gave us, so switching log
      // tabs still replays history.
      setSnapshots((prev) => {
        const scrollbacks = new Map(prev.map((s) => [s.runId, s.scrollback]));
        return next.map((s) =>
          s.scrollback === null && scrollbacks.get(s.runId)
            ? { ...s, scrollback: scrollbacks.get(s.runId) ?? null }
            : s
        );
      });
    };
    connection.on("AppRunOutput", handleOutput);
    connection.on("AppRunState", handleState);

    // Attach AFTER the group join: the worker may reconcile during attach and broadcast a
    // fresher state than the response carries.
    withToken((token) => api.appRun.attach(projectId, token))
      .then((state) => {
        if (disposed) return;
        setSupported(state.supported);
        setSnapshots(state.snapshots);
        setAttached(true);
      })
      .catch(() => {
        if (!disposed) setSupported(false);
      });

    return () => {
      disposed = true;
      setAttached(false);
      connection.off("AppRunOutput", handleOutput);
      connection.off("AppRunState", handleState);
      if (trackGroupLeave(`apprun:${projectId}`)) {
        connection.invoke("LeaveAppRun", projectId).catch(() => {});
      }
    };
  }, [projectId, connection, isConnected, withToken]);

  const subscribeOutput = useCallback(
    (runId: string, handler: (base64: string) => void) => {
      outputHandlers.current.set(runId, handler);
      return () => {
        outputHandlers.current.delete(runId);
      };
    },
    []
  );

  const start = useCallback(
    async (targetKeys: string[], forceFreePorts: boolean) => {
      if (!projectId) throw new Error("No project selected.");
      const result = await withToken((token) =>
        api.appRun.start(projectId, { targetKeys, forceFreePorts }, token)
      );
      if (!result.supported)
        throw new Error(result.error ?? "Your runner is not connected.");
      // The broadcast covers the running ones; failures are returned here only (they have
      // no process to track), so merge rather than replace.
      setSupported(true);
      setSnapshots((prev) => {
        const byKey = new Map(prev.map((s) => [s.key, s]));
        for (const snapshot of result.snapshots) byKey.set(snapshot.key, snapshot);
        return [...byKey.values()];
      });
      return result.snapshots;
    },
    [projectId, withToken]
  );

  const stop = useCallback(
    async (runId: string | null) => {
      if (!projectId) return;
      await withToken((token) => api.appRun.stop(projectId, runId, token));
      if (runId) setSnapshots((prev) => prev.filter((s) => s.runId !== runId));
      else setSnapshots([]);
    },
    [projectId, withToken]
  );

  const sendInput = useCallback(
    (runId: string, dataBase64: string) => {
      if (!projectId) return;
      void withToken((token) =>
        api.appRun.input(projectId, runId, dataBase64, token)
      ).catch(() => {});
    },
    [projectId, withToken]
  );

  const sendResize = useCallback(
    (runId: string, cols: number, rows: number) => {
      if (!projectId) return;
      void withToken((token) =>
        api.appRun.resize(projectId, runId, cols, rows, token)
      ).catch(() => {});
    },
    [projectId, withToken]
  );

  return {
    snapshots,
    supported,
    attached,
    connected: isConnected,
    start,
    stop,
    subscribeOutput,
    sendInput,
    sendResize,
  };
}
