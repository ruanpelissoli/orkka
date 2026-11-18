import { type ReactNode } from "react";
import { SignalRProvider } from "@/components/providers/SignalRProvider";

/** SignalR for the local UI: the hub is same-origin and authenticates with the host's session
 * cookie, so no access-token factory is passed. */
export function LocalSignalRProvider({ children }: { children: ReactNode }) {
  return <SignalRProvider>{children}</SignalRProvider>;
}
