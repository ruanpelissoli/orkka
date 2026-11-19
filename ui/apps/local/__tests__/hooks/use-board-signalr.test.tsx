import { describe, it, expect, beforeEach, vi } from "vitest";
import { renderHook, waitFor } from "@testing-library/react";

const connection = {
  invoke: vi.fn().mockResolvedValue(undefined),
  on: vi.fn(),
  off: vi.fn(),
};

vi.mock("@/components/providers/SignalRProvider", () => ({
  useSignalRConnection: () => ({ connection, isConnected: true }),
}));

import { useBoardSignalR } from "@/hooks/use-board-signalr";

describe("useBoardSignalR", () => {
  beforeEach(() => {
    connection.invoke.mockClear();
    connection.on.mockClear();
    connection.off.mockClear();
  });

  it("joins one project's board group", async () => {
    const onConnected = vi.fn();
    renderHook(() => useBoardSignalR({ projectId: "01SHOP", onConnected }));
    expect(connection.invoke).toHaveBeenCalledWith("JoinBoard", "01SHOP");
    await waitFor(() => expect(onConnected).toHaveBeenCalledTimes(1));
  });

  it("joins every project of the workspace board with ONE set of handlers, and leaves them all", async () => {
    const onConnected = vi.fn();
    const { unmount } = renderHook(() =>
      useBoardSignalR({ projectIds: ["01SHOP", "01BILL"], onConnected })
    );
    expect(connection.invoke.mock.calls.filter(([m]) => m === "JoinBoard")).toEqual([
      ["JoinBoard", "01SHOP"],
      ["JoinBoard", "01BILL"],
    ]);
    // Handlers are per connection: registered once, an event is handled once.
    expect(connection.on.mock.calls.filter(([e]) => e === "LaneChanged")).toHaveLength(1);
    // Reconciled once, after every group is joined.
    await waitFor(() => expect(onConnected).toHaveBeenCalledTimes(1));

    unmount();
    expect(connection.invoke.mock.calls.filter(([m]) => m === "LeaveBoard").map(([, id]) => id)).toEqual([
      "01SHOP",
      "01BILL",
    ]);
  });

  it("does not resubscribe for a new array with the same projects", () => {
    const { rerender } = renderHook(({ ids }) => useBoardSignalR({ projectIds: ids }), {
      initialProps: { ids: ["01SHOP", "01BILL"] },
    });
    rerender({ ids: ["01SHOP", "01BILL"] });
    expect(connection.invoke.mock.calls.filter(([m]) => m === "JoinBoard")).toHaveLength(2);
  });

  it("joins nothing and listens to nothing without a project", () => {
    renderHook(() => useBoardSignalR({ projectId: undefined, onLaneChanged: vi.fn() }));
    expect(connection.invoke).not.toHaveBeenCalled();
    expect(connection.on).not.toHaveBeenCalled();
  });
});
