import { useEffect } from "react";
import { useSignalRConnection } from "@/components/providers/SignalRProvider";
import type { AgentMessage } from "@/lib/types";

interface UseAgentChatSignalROptions {
  conversationId: string | null;
  onNewMessage?: (message: AgentMessage) => void;
}

function useAgentChatSignalR({
  conversationId,
  onNewMessage,
}: UseAgentChatSignalROptions) {
  const { connection, isConnected } = useSignalRConnection();

  useEffect(() => {
    if (!connection || !isConnected || !conversationId) return;

    connection.invoke("JoinAgentChat", conversationId).catch(() => {});

    const handleNewMessage = (message: AgentMessage) => {
      onNewMessage?.(message);
    };

    connection.on("AgentChatMessage", handleNewMessage);

    return () => {
      connection.off("AgentChatMessage", handleNewMessage);
      connection.invoke("LeaveAgentChat", conversationId).catch(() => {});
    };
  }, [connection, isConnected, conversationId, onNewMessage]);

  return { isConnected };
}

export { useAgentChatSignalR };
