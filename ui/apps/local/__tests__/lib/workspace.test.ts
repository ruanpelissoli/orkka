import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";

// A constructible stand-in for the SignalR builder that records the hub URL it was given.
const withUrlCalls: string[] = [];
vi.mock("@microsoft/signalr", () => {
  class HubConnectionBuilder {
    withUrl(url: string) {
      withUrlCalls.push(url);
      return this;
    }
    withAutomaticReconnect() {
      return this;
    }
    configureLogging() {
      return this;
    }
    build() {
      const conn = {
        state: "Connected",
        start: vi.fn(async () => undefined),
        stop: vi.fn(async () => {
          conn.state = "Disconnected";
        }),
      };
      return conn;
    }
  }
  return {
    HubConnectionBuilder,
    HubConnectionState: {
      Disconnected: "Disconnected",
      Connecting: "Connecting",
      Connected: "Connected",
      Disconnecting: "Disconnecting",
      Reconnecting: "Reconnecting",
    },
    LogLevel: { Warning: 3 },
  };
});
import {
  WORKSPACE_COOKIE,
  WORKSPACE_HEADER,
  getClientWorkspace,
  isWorkspaceId,
  setClientWorkspace,
  withWorkspaceQuery,
} from "@/lib/workspace";
import { api, invalidateApiCache } from "@/lib/api";
import { disconnect, getConnection, tasksHubUrl } from "@/lib/signalr";

const WS_A = "01JAAAAAAAAAAAAAAAAAAAAAAA";
const WS_B = "01JBBBBBBBBBBBBBBBBBBBBBBB";

function headersOf(call: unknown[]): Record<string, string> {
  return ((call[1] as RequestInit).headers ?? {}) as Record<string, string>;
}

describe("workspace cookie", () => {
  afterEach(() => setClientWorkspace(null));

  it("accepts only well-formed org ids", () => {
    expect(isWorkspaceId(WS_A)).toBe(true);
    expect(isWorkspaceId("acme")).toBe(false);
    expect(isWorkspaceId("")).toBe(false);
    expect(isWorkspaceId(null)).toBe(false);
  });

  it("stores the current workspace in the orkka_ws cookie", () => {
    setClientWorkspace(WS_A);
    expect(document.cookie).toContain(`${WORKSPACE_COOKIE}=${WS_A}`);
    expect(getClientWorkspace()).toBe(WS_A);

    setClientWorkspace(null);
    expect(getClientWorkspace()).toBeNull();
  });

  it("builds the hub query parameter", () => {
    expect(withWorkspaceQuery("https://api/hubs/tasks", WS_A)).toBe(`https://api/hubs/tasks?workspace=${WS_A}`);
    expect(withWorkspaceQuery("https://api/hubs/tasks?x=1", WS_A)).toBe(`https://api/hubs/tasks?x=1&workspace=${WS_A}`);
    expect(withWorkspaceQuery("https://api/hubs/tasks", null)).toBe("https://api/hubs/tasks");
  });
});

describe("api.ts workspace header", () => {
  const fetchMock = vi.fn();

  beforeEach(() => {
    invalidateApiCache();
    fetchMock.mockReset();
    fetchMock.mockImplementation(() =>
      Promise.resolve(new Response(JSON.stringify([]), { status: 200 }))
    );
    vi.stubGlobal("fetch", fetchMock);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    setClientWorkspace(null);
  });

  it("sends X-Orkka-Workspace on every request when a workspace is set", async () => {
    setClientWorkspace(WS_A);
    await api.tasks.list("proj-1", "tok");

    expect(headersOf(fetchMock.mock.calls[0])[WORKSPACE_HEADER]).toBe(WS_A);
  });

  it("sends no workspace header when none is set (the API uses the active one)", async () => {
    await api.tasks.list("proj-1", "tok");

    expect(headersOf(fetchMock.mock.calls[0])).not.toHaveProperty(WORKSPACE_HEADER);
  });

  it("keys the response cache per workspace", async () => {
    setClientWorkspace(WS_A);
    await api.projects.list("tok");
    await api.projects.list("tok");
    expect(fetchMock).toHaveBeenCalledTimes(1);

    // Another job's workspace: the same path is a different answer.
    setClientWorkspace(WS_B);
    await api.projects.list("tok");
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(headersOf(fetchMock.mock.calls[1])[WORKSPACE_HEADER]).toBe(WS_B);
  });

  it("asks the workspace list without a workspace header, so a stale cookie can't break it", async () => {
    setClientWorkspace(WS_A);
    await api.workspaces.mine("tok");

    expect(headersOf(fetchMock.mock.calls[0])).not.toHaveProperty(WORKSPACE_HEADER);
  });

  it("can ask another workspace's runner explicitly", async () => {
    fetchMock.mockImplementation(() =>
      Promise.resolve(new Response(JSON.stringify({ online: true }), { status: 200 }))
    );
    setClientWorkspace(WS_A);
    await api.runner.status("tok", WS_B);

    expect(headersOf(fetchMock.mock.calls[0])[WORKSPACE_HEADER]).toBe(WS_B);
  });
});

describe("signalr.ts workspace query", () => {
  afterEach(async () => {
    await disconnect();
    setClientWorkspace(null);
  });

  it("opens the tasks hub with ?workspace= for the current workspace", async () => {
    setClientWorkspace(WS_A);
    expect(tasksHubUrl()).toMatch(new RegExp(`/hubs/tasks\\?workspace=${WS_A}$`));

    withUrlCalls.length = 0;
    await getConnection(async () => "tok");

    expect(withUrlCalls[0]).toMatch(new RegExp(`\\?workspace=${WS_A}$`));
  });

  it("drops a connection opened for another workspace", async () => {
    setClientWorkspace(WS_A);
    const first = await getConnection(async () => "tok");

    setClientWorkspace(WS_B);
    const second = await getConnection(async () => "tok");

    expect(second).not.toBe(first);
    expect(first.stop).toHaveBeenCalled();
  });
});
