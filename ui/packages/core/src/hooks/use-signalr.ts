import { useCallback, useRef, useSyncExternalStore } from "react";
import { useSignalRConnection } from "@/components/providers/SignalRProvider";

interface SignalROptions<T> {
  filter?: (data: T) => boolean;
}

function useSignalR<T>(
  eventName: string,
  options?: SignalROptions<T>
): { data: T | null; isConnected: boolean } {
  const { connection, isConnected } = useSignalRConnection();
  const dataRef = useRef<T | null>(null);
  const listenersRef = useRef(new Set<() => void>());

  const subscribe = useCallback(
    (callback: () => void) => {
      listenersRef.current.add(callback);

      if (connection) {
        const handler = (data: T) => {
          if (options?.filter && !options.filter(data)) return;
          dataRef.current = data;
          listenersRef.current.forEach((l) => l());
        };

        connection.on(eventName, handler);
        return () => {
          connection.off(eventName, handler);
          listenersRef.current.delete(callback);
        };
      }

      return () => {
        listenersRef.current.delete(callback);
      };
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [connection, eventName]
  );

  const getSnapshot = useCallback(() => dataRef.current, []);

  const data = useSyncExternalStore(subscribe, getSnapshot, () => null);

  return { data, isConnected };
}

export { useSignalR };
