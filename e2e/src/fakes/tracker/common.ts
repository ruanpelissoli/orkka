/** Shared plumbing for the fake Jira Cloud / Azure DevOps servers. */
import type { IncomingMessage, ServerResponse } from "node:http";

export interface FakeComment {
  id: string;
  author: string;
  created: Date;
  bodyHtml: string;
  deleted?: boolean;
}

export interface FakeAttachment {
  id: string;
  filename: string;
  mimeType: string;
  content: Buffer;
}

export interface RecordedRequest {
  at: string;
  tracker: "jira" | "ado" | "github" | "admin" | "unknown";
  method: string;
  path: string;
  query: Record<string, string>;
  body: unknown;
  /** Whether the Authorization header carried the configured credential. */
  authOk: boolean;
  /** The raw Authorization header (Basic … / Bearer … for GitHub) — kept so specs can assert who sent what. */
  authorization: string | null;
  status: number;
}

export interface InjectedFailure {
  tracker?: "jira" | "ado" | "github";
  method?: string;
  path: RegExp;
  status: number;
  body?: unknown;
  contentType?: string;
  /** How many requests it applies to (default: unlimited until cleared). */
  times?: number;
}

export interface Ctx {
  req: IncomingMessage;
  res: ServerResponse;
  method: string;
  path: string;
  segments: string[];
  query: Record<string, string>;
  body: unknown;
  rawBody: Buffer;
  /** Origin the client used (scheme://host:port), for URLs the fake hands back. */
  origin: string;
  record: RecordedRequest;
}

export function send(ctx: Ctx, status: number, body?: unknown, contentType = "application/json; charset=utf-8") {
  ctx.record.status = status;
  if (body === undefined || status === 204) {
    ctx.res.writeHead(status);
    ctx.res.end();
    return;
  }
  const payload = Buffer.isBuffer(body) ? body : Buffer.from(typeof body === "string" ? body : JSON.stringify(body));
  ctx.res.writeHead(status, { "Content-Type": contentType, "Content-Length": payload.length });
  ctx.res.end(payload);
}

export function basicOf(user: string, secret: string) {
  return "Basic " + Buffer.from(`${user}:${secret}`, "utf8").toString("base64");
}

export function htmlToText(html: string): string {
  return html
    .replace(/<br\s*\/?>/gi, "\n")
    .replace(/<\/p>/gi, "\n")
    .replace(/<[^>]+>/g, "")
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/&quot;/g, '"')
    .replace(/&#39;/g, "'")
    .replace(/&amp;/g, "&")
    .trim();
}

/** A minimal Atlassian Document Format value carrying the text of some HTML. */
export function adfOf(html: string | undefined | null) {
  if (!html) return null;
  return {
    type: "doc",
    version: 1,
    content: htmlToText(html)
      .split(/\n+/)
      .filter(Boolean)
      .map((line) => ({ type: "paragraph", content: [{ type: "text", text: line }] })),
  };
}

/** Jira writes offsets without a colon: 2026-10-01T09:30:00.000+0000. */
export function jiraDate(d: Date) {
  return d.toISOString().replace("Z", "+0000");
}

/**
 * Splits `text` on a keyword (AND / OR) that sits at the top level — outside parentheses and
 * quoted strings. Case-insensitive, whole word.
 */
export function splitTopLevel(text: string, keyword: string): string[] {
  const parts: string[] = [];
  let depth = 0;
  let quote: string | null = null;
  let start = 0;
  const kw = ` ${keyword.toUpperCase()} `;
  for (let i = 0; i < text.length; i++) {
    const ch = text[i];
    if (quote) {
      if (ch === "\\") i++;
      else if (ch === quote) {
        // WIQL doubles a quote to escape it.
        if (quote === "'" && text[i + 1] === "'") i++;
        else quote = null;
      }
      continue;
    }
    if (ch === '"' || ch === "'") quote = ch;
    else if (ch === "(") depth++;
    else if (ch === ")") depth--;
    else if (depth === 0 && text.slice(i, i + kw.length).toUpperCase() === kw) {
      parts.push(text.slice(start, i));
      start = i + kw.length;
      i += kw.length - 1;
    }
  }
  parts.push(text.slice(start));
  return parts.map((p) => p.trim()).filter((p) => p.length > 0);
}

/** `(a AND b)` → `a AND b` when the outer parentheses wrap the whole term. */
export function unwrap(term: string): string {
  let t = term.trim();
  while (t.startsWith("(") && t.endsWith(")")) {
    let depth = 0;
    let wrapsAll = true;
    for (let i = 0; i < t.length; i++) {
      if (t[i] === "(") depth++;
      else if (t[i] === ")") depth--;
      if (depth === 0 && i < t.length - 1) {
        wrapsAll = false;
        break;
      }
    }
    if (!wrapsAll) break;
    t = t.slice(1, -1).trim();
  }
  return t;
}

/** Parses a JQL ("…" with \-escapes) or WIQL ('…' with doubled quotes) literal list. */
export function parseLiterals(list: string): string[] {
  const out: string[] = [];
  const re = /"((?:[^"\\]|\\.)*)"|'((?:[^']|'')*)'|([^\s,()]+)/g;
  for (const m of list.matchAll(re)) {
    if (m[1] !== undefined) out.push(m[1].replace(/\\(.)/g, "$1"));
    else if (m[2] !== undefined) out.push(m[2].replace(/''/g, "'"));
    else if (m[3] !== undefined) out.push(m[3]);
  }
  return out;
}

export class QueryError extends Error {}
