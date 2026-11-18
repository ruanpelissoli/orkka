import * as signalR from "@microsoft/signalr";
import { CSRF_HEADERS } from "./api";
import { getClientWorkspace, withWorkspaceQuery } from "./workspace";

// Same origin, like the API (see api.ts): hub URLs are relative.
const HUB_BASE_URL = "";

// ── Host-level options ───────────────────────────────────────────────
//
// Browsers negotiate the best transport themselves. React Native has WebSocket but no
// EventSource, and its long-polling path is slow to fail over, so the mobile app pins
// WebSockets up front. Web never calls this and keeps the defaults.
interface SignalROptions {
  transport?: signalR.HttpTransportType;
}
let hostOptions: SignalROptions = {};

export function configureSignalR(options: SignalROptions): void {
  hostOptions = { ...hostOptions, ...options };
}

function hubUrlOptions(
  getToken?: () => Promise<string | null>
): signalR.IHttpConnectionOptions {
  return {
    // No token factory = cookie auth on a same-origin hub (the local UI): the browser sends
    // the host's session cookie with the negotiate request and the WebSocket upgrade.
    ...(getToken
      ? {
          accessTokenFactory: async () => {
            const token = await getToken();
            return token ?? "";
          },
        }
      : { withCredentials: true, headers: CSRF_HEADERS }),
    ...(hostOptions.transport !== undefined ? { transport: hostOptions.transport } : {}),
  };
}

// Retry forever. The default array policy gives up after its last entry (~47s of
// disconnection), fires onclose, and nothing ever reconnects — a laptop sleep or an API
// deploy longer than that silently killed realtime until a hard refresh. Capped
// exponential backoff with jitter; never returns null.
const foreverRetry: signalR.IRetryPolicy = {
  nextRetryDelayInMilliseconds: (ctx) => {
    const exp = Math.min(ctx.previousRetryCount, 5); // 1s, 2s, 4s, 8s, 16s, 30s cap
    const base = Math.min(30_000, 1_000 * 2 ** exp);
    return base + Math.floor(Math.random() * 1_000);
  },
};

// ── Group membership ref-counting ────────────────────────────────────
//
// The hub connection is a module-level singleton shared by every subscriber on the page,
// but the server's Leave{Board,Task} removes the CONNECTION from the group. With two
// components subscribed to the same group (TaskBoard + AutopilotToggle on the board;
// TaskDetailLive + InteractiveApprovalPanel on a task), whichever unmounted or re-ran its
// effect first silenced the other. Join is idempotent server-side and always invoked;
// Leave is only invoked when the last subscriber goes away.

const groupRefs = new Map<string, number>();

export function trackGroupJoin(key: string): void {
  groupRefs.set(key, (groupRefs.get(key) ?? 0) + 1);
}

/** Returns true when this was the last subscriber and the server Leave should be sent. */
export function trackGroupLeave(key: string): boolean {
  const next = (groupRefs.get(key) ?? 1) - 1;
  if (next <= 0) {
    groupRefs.delete(key);
    return true;
  }
  groupRefs.set(key, next);
  return false;
}

// ── Tasks Hub ────────────────────────────────────────────────────────

let connection: signalR.HubConnection | null = null;
let connectionStart: Promise<void> | null = null;
/** The workspace the live connection was opened for — the hub scopes its groups to it. */
let connectionWorkspace: string | null = null;

/** The tasks hub URL for the current workspace (`?workspace=<orgId>` when one is set). */
export function tasksHubUrl(): string {
  return withWorkspaceQuery(`${HUB_BASE_URL}/hubs/tasks`, getClientWorkspace());
}

export async function getConnection(
  getToken?: () => Promise<string | null>
): Promise<signalR.HubConnection> {
  // A connection opened for another workspace must not carry this one's events.
  if (connection && connectionWorkspace !== getClientWorkspace()) {
    const stale = connection;
    connection = null;
    connectionStart = null;
    void stale.stop().catch(() => undefined);
  }

  if (connection) {
    if (connection.state === signalR.HubConnectionState.Connected) {
      return connection;
    }
    if (connectionStart) {
      // A start is already in flight — share it instead of racing a second one.
      await connectionStart;
      return connection;
    }
    if (connection.state !== signalR.HubConnectionState.Disconnected) {
      // Reconnecting: the retry policy owns it; hand back the live object.
      return connection;
    }
    // Disconnected with no start in flight = a dead object (failed start or hard close).
    // Drop it and build fresh — reusing it caches the failure forever.
    connection = null;
  }

  connectionWorkspace = getClientWorkspace();
  connection = new signalR.HubConnectionBuilder()
    .withUrl(tasksHubUrl(), hubUrlOptions(getToken))
    .withAutomaticReconnect(foreverRetry)
    .configureLogging(signalR.LogLevel.Warning)
    .build();

  connectionStart = connection.start();
  try {
    await connectionStart;
  } finally {
    connectionStart = null;
  }
  return connection;
}

export function getExistingConnection(): signalR.HubConnection | null {
  return connection;
}

export async function disconnect(): Promise<void> {
  if (connection) {
    await connection.stop();
    connection = null;
  }
}
