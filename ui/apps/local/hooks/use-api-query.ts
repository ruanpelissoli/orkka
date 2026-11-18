import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from "react";
import { getDataVersion, subscribeDataRefresh } from "@/lib/data-refresh";

export interface ApiQuery<T> {
  /** The latest successful result; kept while a refetch is in flight. */
  data: T | undefined;
  /** Set when the first load failed (a failed refetch keeps the previous data instead). */
  error: unknown;
  /** True until the first result (or error) for the current key arrives. */
  loading: boolean;
  /** Refetch this query only. App-wide refreshes go through `refreshData()`. */
  reload: () => void;
}

/** The data version from lib/data-refresh, as React state. */
export function useDataVersion(): number {
  return useSyncExternalStore(subscribeDataRefresh, getDataVersion, getDataVersion);
}

/**
 * Loads page data in the browser. `key` identifies the request (route params, search params):
 * a new key loads again. The query also reloads whenever `refreshData()` fires — that is how a
 * mutation refreshes what is on screen. Data from the previous key or version stays visible
 * until the new response arrives, the way a server-rendered page kept its content during a
 * soft navigation. Calls go to the host same-origin with the session cookie (empty token).
 *
 * `load` must not throw for parts of a page that degrade (catch those inside it); a throw is
 * the page's error state.
 */
export function useApiQuery<T>(load: () => Promise<T>, key: readonly unknown[]): ApiQuery<T> {
  const version = useDataVersion();
  const [nonce, setNonce] = useState(0);
  const loadRef = useRef(load);
  useEffect(() => {
    loadRef.current = load;
  });

  const keyString = JSON.stringify(key);
  const [state, setState] = useState<{ key: string | null; data?: T; error?: unknown }>({
    key: null,
  });

  useEffect(() => {
    let live = true;
    loadRef.current().then(
      (data) => {
        if (live) setState({ key: keyString, data });
      },
      (error: unknown) => {
        if (!live) return;
        // A refetch that fails keeps what is on screen; only a page with nothing to show
        // goes to its error state.
        setState((prev) =>
          prev.data !== undefined ? { ...prev, key: keyString } : { key: keyString, error }
        );
      }
    );
    return () => {
      live = false;
    };
  }, [keyString, version, nonce]);

  const reload = useCallback(() => setNonce((n) => n + 1), []);

  return {
    data: state.data,
    error: state.data === undefined && state.key === keyString ? state.error : undefined,
    loading: state.key !== keyString && state.data === undefined,
    reload,
  };
}
