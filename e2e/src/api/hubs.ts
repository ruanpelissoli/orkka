import { HubConnection, HubConnectionBuilder, HttpTransportType, LogLevel } from "@microsoft/signalr";

export interface HubEventRecord {
  at: string;
  hub: string;
  event: string;
  payload: unknown;
}

export const TASK_HUB_EVENTS = [
  "StreamEvent",
  "LaneChanged",
  "StatusChanged",
  "NewMessage",
  "TaskLastMessageChanged",
  "TaskCreated",
  "TaskUpdated",
  "TaskDeleted",
  "TaskMergeFailed",
  "TaskDependenciesChanged",
  "MilestoneChanged",
  "MilestonePlanChanged",
  "AutopilotChanged",
  "PlanReviewed",
  "CodeReviewed",
  "WorkspaceChanged",
  "NotificationCreated",
  "AgentChatMessage",
  "AppRunOutput",
  "AppRunState",
] as const;

/**
 * SignalR client that records every server→client event and lets a test wait for one. Hubs only
 * accept Clerk JWTs (`HubAccess` policy), never org API keys — pass a token factory from the
 * browser session.
 */
export class HubClient {
  readonly recorded: HubEventRecord[] = [];
  private waiters: { event: string; pred: (p: unknown) => boolean; resolve: (p: unknown) => void }[] = [];

  private constructor(
    private readonly conn: HubConnection,
    readonly hubName: string,
  ) {}

  static async connect(apiUrl: string, hubPath: string, hubName: string, events: readonly string[], getToken: () => Promise<string>): Promise<HubClient> {
    const conn = new HubConnectionBuilder()
      .withUrl(new URL(hubPath, apiUrl).toString(), {
        accessTokenFactory: getToken,
        transport: HttpTransportType.WebSockets | HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling,
      })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();
    const client = new HubClient(conn, hubName);
    for (const ev of events) {
      conn.on(ev, (...args: unknown[]) => client.onEvent(ev, args.length === 1 ? args[0] : args));
    }
    await conn.start();
    return client;
  }

  private onEvent(event: string, payload: unknown) {
    this.recorded.push({ at: new Date().toISOString(), hub: this.hubName, event, payload });
    if (this.recorded.length > 1000) this.recorded.shift();
    this.waiters = this.waiters.filter((w) => {
      if (w.event !== event || !w.pred(payload)) return true;
      w.resolve(payload);
      return false;
    });
  }

  invoke(method: string, ...args: unknown[]) {
    return this.conn.invoke(method, ...args);
  }

  /** Resolves with the payload of the first matching event (past or future) within the deadline. */
  waitFor<T = unknown>(event: string, pred: (p: T) => boolean = () => true, deadlineMs = 60_000): Promise<T> {
    const past = this.recorded.find((r) => r.event === event && pred(r.payload as T));
    if (past) return Promise.resolve(past.payload as T);
    return new Promise<T>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.waiters = this.waiters.filter((w) => w.resolve !== wrapped);
        reject(new Error(`No ${this.hubName}.${event} event within ${deadlineMs}ms`));
      }, deadlineMs);
      const wrapped = (p: unknown) => {
        clearTimeout(timer);
        resolve(p as T);
      };
      this.waiters.push({ event, pred: pred as (p: unknown) => boolean, resolve: wrapped });
    });
  }

  async stop() {
    await this.conn.stop().catch(() => {});
  }
}

export const connectTaskHub = (apiUrl: string, getToken: () => Promise<string>) => HubClient.connect(apiUrl, "/hubs/tasks", "TaskHub", TASK_HUB_EVENTS, getToken);
