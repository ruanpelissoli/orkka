import {
  createContext,
  useContext,
  useEffect,
  useRef,
  useState,
  type ReactNode,
} from "react";
import type { HubConnection } from "@microsoft/signalr";
import { getConnection, disconnect } from "@/lib/signalr";

interface SignalRContextValue {
  connection: HubConnection | null;
  isConnected: boolean;
}

const SignalRContext = createContext<SignalRContextValue>({
  connection: null,
  isConnected: false,
});

export function useSignalRConnection() {
  return useContext(SignalRContext);
}

interface SignalRProviderProps {
  children: ReactNode;
  /**
   * Returns a bearer token for the hub connection. Omitted by the local UI: the hub is
   * same-origin and the host's session cookie authenticates it.
   */
  tokenFactory?: () => Promise<string | null>;
}

function SignalRProvider({ children, tokenFactory }: SignalRProviderProps) {
  const getTokenRef = useRef(tokenFactory);
  // Synced in an effect, not during render. Declared before the connection effect so the
  // first connect already reads the current factory.
  useEffect(() => {
    getTokenRef.current = tokenFactory;
  }, [tokenFactory]);

  const [connection, setConnection] = useState<HubConnection | null>(null);
  const [isConnected, setIsConnected] = useState(false);

  useEffect(() => {
    let cancelled = false;
    let retryTimer: ReturnType<typeof setTimeout> | null = null;
    let retryDelayMs = 5_000;

    // The automatic-reconnect policy retries forever, so onclose only fires for hard
    // failures (auth rejection, explicit stop). This loop is the safety net that rebuilds
    // the connection from scratch in those cases — without it, one hard close meant no
    // realtime until a page reload.
    function scheduleReconnect() {
      if (cancelled || retryTimer) return;
      retryTimer = setTimeout(() => {
        retryTimer = null;
        void connect();
      }, retryDelayMs);
      retryDelayMs = Math.min(retryDelayMs * 2, 30_000);
    }

    async function connect() {
      if (cancelled) return;
      try {
        const tokenFactory = getTokenRef.current;
        if (tokenFactory) {
          const token = await tokenFactory();
          if (!token || cancelled) return;
        }

        const conn = await getConnection(tokenFactory ? () => tokenFactory() : undefined);
        if (cancelled) return;

        conn.onreconnecting(() => setIsConnected(false));
        conn.onreconnected(() => setIsConnected(true));
        conn.onclose(() => {
          setIsConnected(false);
          scheduleReconnect();
        });

        retryDelayMs = 5_000;
        // Set last: subscribers key their join effects on (connection, isConnected), so a
        // rebuilt connection object re-runs every group join.
        setConnection(conn);
        setIsConnected(true);
      } catch {
        // SignalR not available (e.g., no backend running) — keep trying quietly
        setIsConnected(false);
        scheduleReconnect();
      }
    }

    connect();

    return () => {
      cancelled = true;
      if (retryTimer) clearTimeout(retryTimer);
      disconnect();
    };
  }, []);

  return (
    <SignalRContext.Provider value={{ connection, isConnected }}>
      {children}
    </SignalRContext.Provider>
  );
}

export { SignalRProvider };
