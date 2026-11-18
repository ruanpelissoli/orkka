/**
 * Thin fetch wrapper over the Orkka API: auth modes, per-minute token buckets, 429/5xx retry,
 * ProblemDetails-aware errors, and a per-process request ledger the reporter can attach.
 */
export type HttpMethod = "GET" | "POST" | "PUT" | "PATCH" | "DELETE";

export type AuthMode =
  | { kind: "api-key"; key: string }
  /** Any other bearer — on the local stack, the install secret. */
  | { kind: "bearer"; getToken: () => Promise<string> }
  | { kind: "none" };

export interface RequestRecord {
  at: string;
  method: HttpMethod;
  path: string;
  status: number;
  ms: number;
  requestId?: string;
  error?: string;
}

export interface RequestOptions {
  body?: unknown;
  query?: Record<string, string | number | boolean | undefined>;
  headers?: Record<string, string>;
  /** Counts against the AI bucket (30/min in prod) instead of the general one. */
  ai?: boolean;
  /** Accepted statuses; anything else throws ApiError. Default: 2xx. */
  expect?: number[];
  timeoutMs?: number;
  /** Retry 429/5xx/network errors. Default true for GET, false otherwise. */
  retry?: boolean;
  /** Return the raw Response (for SSE / binary) instead of parsed JSON. */
  raw?: boolean;
}

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly method: HttpMethod,
    public readonly path: string,
    public readonly body: unknown,
    public readonly requestId?: string,
  ) {
    super(`${method} ${path} → ${status}: ${ApiError.describe(body)}`);
    this.name = "ApiError";
  }

  static describe(body: unknown): string {
    if (body == null) return "(empty body)";
    if (typeof body === "string") return body.slice(0, 300);
    const b = body as Record<string, unknown>;
    const errors = b.errors as Record<string, unknown> | undefined;
    if (errors) {
      const general = errors.generalErrors as string[] | undefined;
      if (general?.length) return general.join("; ");
      const first = Object.entries(errors)[0];
      if (first) return `${first[0]}: ${Array.isArray(first[1]) ? first[1].join("; ") : String(first[1])}`;
    }
    for (const k of ["detail", "error", "message", "title", "code"]) {
      if (typeof b[k] === "string") return b[k] as string;
    }
    return JSON.stringify(body).slice(0, 300);
  }

  /** The `code` field on 402 plan-limit / trial-limit responses, if any. */
  get code(): string | undefined {
    const b = this.body as Record<string, unknown> | null;
    return b && typeof b.code === "string" ? b.code : undefined;
  }
}

class TokenBucket {
  private stamps: number[] = [];
  constructor(private readonly perMinute: number) {}
  async take(): Promise<void> {
    for (;;) {
      const now = Date.now();
      this.stamps = this.stamps.filter((t) => now - t < 60_000);
      if (this.stamps.length < this.perMinute) {
        this.stamps.push(now);
        return;
      }
      const waitMs = 60_000 - (now - this.stamps[0]) + 50;
      await sleep(waitMs);
    }
  }
}

export const sleep = (ms: number) => new Promise<void>((r) => setTimeout(r, ms));

export class OrkkaHttp {
  private readonly general: TokenBucket;
  private readonly ai: TokenBucket;
  readonly ledger: RequestRecord[] = [];
  onRequest?: (rec: RequestRecord) => void;

  constructor(
    readonly baseUrl: string,
    private auth: AuthMode,
    rate: { generalPerMin: number; aiPerMin: number } = { generalPerMin: 280, aiPerMin: 25 },
  ) {
    this.general = new TokenBucket(rate.generalPerMin);
    this.ai = new TokenBucket(rate.aiPerMin);
  }

  /** Headers sent on every request (e.g. X-Orkka-Workspace). */
  defaultHeaders: Record<string, string> = {};

  withAuth(auth: AuthMode): OrkkaHttp {
    const h = new OrkkaHttp(this.baseUrl, auth);
    h.onRequest = this.onRequest;
    h.defaultHeaders = { ...this.defaultHeaders };
    return h;
  }

  withHeaders(headers: Record<string, string>): OrkkaHttp {
    const h = this.withAuth(this.auth);
    h.defaultHeaders = { ...this.defaultHeaders, ...headers };
    return h;
  }

  private async authHeader(): Promise<Record<string, string>> {
    switch (this.auth.kind) {
      case "api-key":
        return { Authorization: `Bearer ${this.auth.key}` };
      case "bearer":
        return { Authorization: `Bearer ${await this.auth.getToken()}` };
      case "none":
        return {};
    }
  }

  async request<T = unknown>(method: HttpMethod, path: string, opts: RequestOptions = {}): Promise<T> {
    const url = new URL(path, this.baseUrl);
    for (const [k, v] of Object.entries(opts.query ?? {})) {
      if (v !== undefined) url.searchParams.set(k, String(v));
    }
    const retry = opts.retry ?? method === "GET";
    const maxAttempts = retry ? 4 : 1;
    let lastErr: unknown;

    for (let attempt = 1; attempt <= maxAttempts; attempt++) {
      await (opts.ai ? this.ai : this.general).take();
      const started = Date.now();
      const rec: RequestRecord = { at: new Date(started).toISOString(), method, path: url.pathname + url.search, status: 0, ms: 0 };
      try {
        const headers: Record<string, string> = {
          Accept: "application/json, text/event-stream;q=0.9, */*;q=0.8",
          "Accept-Language": "en",
          ...(await this.authHeader()),
          ...this.defaultHeaders,
          ...(opts.headers ?? {}),
        };
        if (opts.body !== undefined && !(opts.body instanceof FormData)) headers["Content-Type"] = "application/json";
        const ctrl = new AbortController();
        const timer = setTimeout(() => ctrl.abort(), opts.timeoutMs ?? 60_000);
        let res: Response;
        try {
          res = await fetch(url, {
            method,
            headers,
            body: opts.body === undefined ? undefined : opts.body instanceof FormData ? opts.body : JSON.stringify(opts.body),
            signal: ctrl.signal,
          });
        } finally {
          if (!opts.raw) clearTimeout(timer);
        }
        rec.status = res.status;
        rec.ms = Date.now() - started;
        rec.requestId = res.headers.get("x-request-id") ?? res.headers.get("request-id") ?? undefined;
        this.record(rec);

        if (opts.raw) return res as unknown as T;

        const ok = opts.expect ? opts.expect.includes(res.status) : res.status >= 200 && res.status < 300;
        if (ok) {
          if (res.status === 204 || res.headers.get("content-length") === "0") return undefined as T;
          const text = await res.text();
          if (!text) return undefined as T;
          try {
            return JSON.parse(text) as T;
          } catch {
            return text as unknown as T;
          }
        }

        const body = await res.text().then((t) => {
          try {
            return t ? JSON.parse(t) : null;
          } catch {
            return t;
          }
        });
        const err = new ApiError(res.status, method, rec.path, body, rec.requestId);
        const retryable = res.status === 429 || res.status === 502 || res.status === 503 || res.status === 504;
        if (retryable && attempt < maxAttempts) {
          lastErr = err;
          const retryAfter = Number(res.headers.get("retry-after"));
          await sleep(res.status === 429 ? (retryAfter > 0 ? retryAfter * 1000 : msToNextMinute()) : 1000 * 2 ** (attempt - 1));
          continue;
        }
        throw err;
      } catch (e) {
        if (e instanceof ApiError) throw e;
        rec.ms = Date.now() - started;
        rec.error = e instanceof Error ? e.message : String(e);
        this.record(rec);
        lastErr = e;
        if (attempt < maxAttempts) {
          await sleep(1000 * 2 ** (attempt - 1));
          continue;
        }
        throw e;
      }
    }
    throw lastErr;
  }

  private record(rec: RequestRecord) {
    this.ledger.push(rec);
    if (this.ledger.length > 2000) this.ledger.splice(0, this.ledger.length - 2000);
    this.onRequest?.(rec);
  }

  get<T>(path: string, opts?: RequestOptions) {
    return this.request<T>("GET", path, opts);
  }
  post<T>(path: string, body?: unknown, opts?: RequestOptions) {
    return this.request<T>("POST", path, { ...opts, body });
  }
  put<T>(path: string, body?: unknown, opts?: RequestOptions) {
    return this.request<T>("PUT", path, { ...opts, body });
  }
  patch<T>(path: string, body?: unknown, opts?: RequestOptions) {
    return this.request<T>("PATCH", path, { ...opts, body });
  }
  delete<T>(path: string, opts?: RequestOptions) {
    return this.request<T>("DELETE", path, opts);
  }
}

function msToNextMinute(): number {
  const now = new Date();
  return (60 - now.getSeconds()) * 1000 + 250;
}
