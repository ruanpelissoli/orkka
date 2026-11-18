import { vi } from "vitest";
import React from "react";

vi.mock("@microsoft/signalr", () => ({
  HubConnectionBuilder: vi.fn(() => ({
    withUrl: vi.fn().mockReturnThis(),
    withAutomaticReconnect: vi.fn().mockReturnThis(),
    build: vi.fn(() => ({
      start: vi.fn().mockResolvedValue(undefined),
      stop: vi.fn().mockResolvedValue(undefined),
      on: vi.fn(),
      off: vi.fn(),
      invoke: vi.fn(),
      state: "Connected",
      onclose: vi.fn(),
      onreconnecting: vi.fn(),
      onreconnected: vi.fn(),
    })),
  })),
  HubConnectionState: {
    Disconnected: "Disconnected",
    Connecting: "Connecting",
    Connected: "Connected",
    Disconnecting: "Disconnecting",
    Reconnecting: "Reconnecting",
  },
}));

vi.mock("@/components/providers/SignalRProvider", () => ({
  useSignalRConnection: vi.fn(() => ({
    connection: null,
    isConnected: false,
  })),
  SignalRProvider: ({ children }: { children: React.ReactNode }) => children,
}));

vi.mock("@/hooks/use-signalr", () => ({
  useSignalR: vi.fn(() => []),
}));

vi.mock("@/hooks/use-task-signalr", () => ({
  useTaskSignalR: vi.fn(() => ({
    task: null,
    messages: [],
    plans: [],
    executions: [],
  })),
}));

vi.mock("@/hooks/use-board-signalr", () => ({
  useBoardSignalR: vi.fn(() => ({
    lanes: [],
  })),
}));

vi.mock("@/hooks/use-agent-chat-signalr", () => ({
  useAgentChatSignalR: vi.fn(() => ({
    messages: [],
    isStreaming: false,
    sendMessage: vi.fn(),
  })),
}));

