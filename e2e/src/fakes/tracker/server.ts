/**
 * One local HTTP server that is a fake Jira Cloud site (at its root, `/rest/api/3/…`), a
 * fake Azure DevOps organization (under `/{org}/…`) and a fake GitHub GraphQL API (under
 * `/github`, site URL `http://127.0.0.1:<port>/github`). Specs hold the instance and mutate its
 * state directly; `/__fake/*` exposes the same state over HTTP for standalone use
 * (`npm run fake-tracker`).
 *
 * Point the runner at it with site URLs `http://127.0.0.1:<port>` (Jira) and
 * `http://127.0.0.1:<port>/<org>` (ADO) — see src/runner/sandbox.ts for why those cannot be typed
 * into `./orkka tracker add` today and how the harness seeds them instead.
 */
import http from "node:http";
import type { AddressInfo } from "node:net";
import { FakeAdo, type FakeAdoOptions } from "./ado.js";
import { send, type Ctx, type InjectedFailure, type RecordedRequest } from "./common.js";
import { FakeGitHub, type FakeGitHubOptions } from "./github.js";
import { FakeJira, type AppliedTransition, type FakeJiraOptions } from "./jira.js";

export * from "./ado.js";
export * from "./common.js";
export * from "./github.js";
export * from "./jira.js";

export interface FakeTrackerOptions {
  port?: number;
  host?: string;
  jira?: FakeJiraOptions;
  ado?: FakeAdoOptions;
  github?: FakeGitHubOptions;
}

export class FakeTrackerServer {
  readonly jira: FakeJira;
  readonly ado: FakeAdo;
  readonly github: FakeGitHub;
  readonly requests: RecordedRequest[] = [];
  private failures: InjectedFailure[] = [];
  /** When true every tracker request answers 503 (the tracker is down). */
  down = false;
  /** Artificial latency per request, ms. */
  latencyMs = 0;
  private server?: http.Server;
  private _port = 0;
  readonly host: string;

  constructor(private readonly opts: FakeTrackerOptions = {}) {
    this.jira = new FakeJira(opts.jira);
    this.ado = new FakeAdo(opts.ado);
    this.github = new FakeGitHub(opts.github);
    this.host = opts.host ?? "127.0.0.1";
  }

  get port() {
    return this._port;
  }
  get origin() {
    return `http://${this.host}:${this._port}`;
  }
  /** The site URL a runner's Jira connection must hold. */
  get jiraSiteUrl() {
    return this.origin;
  }
  /** The site URL a runner's Azure DevOps connection must hold. */
  get adoSiteUrl() {
    return `${this.origin}/${this.ado.org}`;
  }
  /** The site URL a runner's GitHub connection must hold (its API is at `{site}/graphql`). */
  get githubSiteUrl() {
    return `${this.origin}/github`;
  }
  /** Every status move either tracker accepted, in order (GitHub's: `github.statusChanges`). */
  get transitions(): AppliedTransition[] {
    return [...this.jira.transitions, ...this.ado.transitions].sort((a, b) => a.at.localeCompare(b.at));
  }

  async start(): Promise<this> {
    this.server = http.createServer((req, res) => {
      this.dispatch(req, res).catch((e) => {
        if (!res.headersSent) res.writeHead(500, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ message: `fake tracker crashed: ${(e as Error).message}` }));
      });
    });
    await new Promise<void>((resolve, reject) => {
      this.server!.once("error", reject);
      this.server!.listen(this.opts.port ?? 0, this.host, () => resolve());
    });
    this._port = (this.server.address() as AddressInfo).port;
    return this;
  }

  async stop(): Promise<void> {
    if (!this.server) return;
    await new Promise<void>((resolve) => this.server!.close(() => resolve()));
    this.server.closeAllConnections?.();
    this.server = undefined;
  }

  /** Make matching requests fail (e.g. a 500 on the search endpoint to exercise a failed poll). */
  injectFailure(f: InjectedFailure) {
    this.failures.push({ ...f });
  }
  clearFailures() {
    this.failures = [];
  }

  /** Requests to one tracker, optionally filtered by method + path regex. */
  requestsTo(tracker: "jira" | "ado" | "github", filter?: { method?: string; path?: RegExp }) {
    return this.requests.filter((r) => r.tracker === tracker && (!filter?.method || r.method === filter.method) && (!filter?.path || filter.path.test(r.path)));
  }

  private async dispatch(req: http.IncomingMessage, res: http.ServerResponse) {
    const url = new URL(req.url ?? "/", `http://${req.headers.host ?? `${this.host}:${this._port}`}`);
    const chunks: Buffer[] = [];
    for await (const c of req) chunks.push(c as Buffer);
    const rawBody = Buffer.concat(chunks);
    let body: unknown = undefined;
    if (rawBody.length) {
      try {
        body = JSON.parse(rawBody.toString("utf8"));
      } catch {
        body = rawBody.toString("utf8");
      }
    }
    const path = url.pathname;
    const segments = path.split("/").filter(Boolean).map((s) => decodeURIComponent(s));
    const tracker: RecordedRequest["tracker"] = path.startsWith("/__fake/")
      ? "admin"
      : this.jira.handles(path)
        ? "jira"
        : this.github.handles(path)
          ? "github"
          : this.ado.handles(segments)
            ? "ado"
            : "unknown";
    const authorization = (req.headers.authorization as string | undefined) ?? null;
    const authOk =
      tracker === "jira"
        ? authorization === this.jira.authorization || path.startsWith("/__media/")
        : tracker === "ado"
          ? authorization === this.ado.authorization
          : tracker === "github"
            ? this.github.isAuthorized(authorization)
            : true;
    const record: RecordedRequest = {
      at: new Date().toISOString(),
      tracker,
      method: req.method ?? "GET",
      path,
      query: Object.fromEntries(url.searchParams.entries()),
      body,
      authOk,
      authorization,
      status: 0,
    };
    if (tracker !== "admin") this.requests.push(record);
    const ctx: Ctx = { req, res, method: record.method, path, segments, query: record.query, body, rawBody, origin: `http://${req.headers.host ?? `${this.host}:${this._port}`}`, record };

    if (this.latencyMs > 0) await new Promise((r) => setTimeout(r, this.latencyMs));
    if (tracker === "admin") return this.admin(ctx);
    if (this.down && (tracker === "jira" || tracker === "ado" || tracker === "github")) return send(ctx, 503, { message: "fake tracker is down" });
    const failure = this.failures.find((f) => (!f.tracker || f.tracker === tracker) && (!f.method || f.method === record.method) && f.path.test(path));
    if (failure) {
      if (failure.times !== undefined && --failure.times <= 0) this.failures = this.failures.filter((f) => f !== failure);
      return send(ctx, failure.status, failure.body ?? { errorMessages: [`injected ${failure.status}`], message: `injected ${failure.status}` }, failure.contentType);
    }
    if (tracker === "jira") return this.jira.handle(ctx);
    if (tracker === "ado") return this.ado.handle(ctx);
    if (tracker === "github") return this.github.handle(ctx);
    return send(ctx, 404, { message: `fake tracker: nothing at ${path}` });
  }

  private admin(ctx: Ctx) {
    if (ctx.method === "GET" && ctx.path === "/__fake/state") {
      return send(ctx, 200, {
        jira: { siteUrl: this.jiraSiteUrl, email: this.jira.email, issues: [...this.jira.issues.values()], transitions: this.jira.transitions },
        ado: { siteUrl: this.adoSiteUrl, org: this.ado.org, items: [...this.ado.items.values()], transitions: this.ado.transitions },
        github: {
          siteUrl: this.githubSiteUrl,
          login: this.github.login,
          projects: [...this.github.projects.values()],
          issues: [...this.github.issues.values()],
          items: [...this.github.items.values()],
          statusChanges: this.github.statusChanges,
        },
      });
    }
    if (ctx.method === "GET" && ctx.path === "/__fake/requests") return send(ctx, 200, this.requests.map((r) => ({ ...r, authorization: r.authorization ? "(redacted)" : null })));
    if (ctx.method === "POST" && ctx.path === "/__fake/jira/issues") return send(ctx, 201, this.jira.addIssue(ctx.body as never));
    if (ctx.method === "POST" && ctx.path === "/__fake/ado/items") return send(ctx, 201, this.ado.add(ctx.body as never));
    let m: RegExpExecArray | null;
    // Mutations a spec (or an operator) makes on the tracker side: edit a ticket, comment on it.
    if (ctx.method === "PATCH" && (m = /^\/__fake\/jira\/issues\/([^/]+)$/.exec(ctx.path))) return send(ctx, 200, this.jira.update(decodeURIComponent(m[1]), ctx.body as never));
    if (ctx.method === "POST" && (m = /^\/__fake\/jira\/issues\/([^/]+)\/comments$/.exec(ctx.path))) {
      const b = ctx.body as { bodyHtml: string; author?: string };
      return send(ctx, 201, this.jira.addComment(decodeURIComponent(m[1]), b.bodyHtml, b.author));
    }
    if (ctx.method === "PATCH" && (m = /^\/__fake\/ado\/items\/(\d+)$/.exec(ctx.path))) return send(ctx, 200, this.ado.update(Number(m[1]), ctx.body as never));
    if (ctx.method === "POST" && (m = /^\/__fake\/ado\/items\/(\d+)\/comments$/.exec(ctx.path))) {
      const b = ctx.body as { bodyHtml: string; author?: string };
      return send(ctx, 201, this.ado.addComment(Number(m[1]), b.bodyHtml, b.author));
    }
    if (ctx.method === "POST" && ctx.path === "/__fake/github/projects") return send(ctx, 201, this.github.addProject(ctx.body as never));
    if (ctx.method === "POST" && ctx.path === "/__fake/github/issues") return send(ctx, 201, this.github.addIssue(ctx.body as never));
    if (ctx.method === "POST" && ctx.path === "/__fake/github/items") {
      const b = ctx.body as { projectId: string; issueKey?: string; type?: never; status?: string | null };
      return send(ctx, 201, this.github.addItem(b.projectId, b));
    }
    if (ctx.method === "POST" && ctx.path === "/__fake/github/comments") {
      const b = ctx.body as { key: string; body: string; author?: string };
      return send(ctx, 201, this.github.addComment(b.key, b.body, b.author));
    }
    if (ctx.method === "POST" && ctx.path === "/__fake/github/status") {
      const b = ctx.body as { projectId: string; key: string; status: string | null };
      this.github.setStatus(b.projectId, b.key, b.status);
      return send(ctx, 200, this.github.itemFor(b.projectId, b.key));
    }
    if (ctx.method === "GET" && ctx.path === "/__fake/transitions") return send(ctx, 200, this.transitions);
    if (ctx.method === "POST" && ctx.path === "/__fake/down") {
      this.down = Boolean((ctx.body as { down?: boolean })?.down);
      return send(ctx, 200, { down: this.down });
    }
    return send(ctx, 404, { message: "unknown admin route" });
  }
}

/** Start a fake tracker seeded with one Jira project + epic and one ADO project + epic. */
export async function startFakeTracker(opts: FakeTrackerOptions = {}): Promise<FakeTrackerServer> {
  return new FakeTrackerServer(opts).start();
}
