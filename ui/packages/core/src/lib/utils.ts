import { type ClassValue, clsx } from "clsx";
import { twMerge } from "tailwind-merge";
import type { TreeNode } from "./types";

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs));
}

export function formatRelativeTime(date: string | Date): string {
  const now = new Date();
  const then = new Date(date);
  const seconds = Math.floor((now.getTime() - then.getTime()) / 1000);

  if (seconds < 60) return "just now";
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`;
  if (seconds < 604800) return `${Math.floor(seconds / 86400)}d ago`;
  return then.toLocaleDateString();
}

/** Absolute time for chat messages: "14:32" today, "Jul 9, 14:32" this year, "Jul 9, 2025, 14:32" otherwise. */
export function formatMessageTime(date: string | Date): string {
  const then = new Date(date);
  const now = new Date();
  const time = then.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });
  if (then.toDateString() === now.toDateString()) return time;
  const day = then.toLocaleDateString(undefined, {
    month: "short",
    day: "numeric",
    ...(then.getFullYear() !== now.getFullYear() ? { year: "numeric" } : {}),
  });
  return `${day}, ${time}`;
}

export function truncate(str: string, length: number): string {
  if (str.length <= length) return str;
  return str.slice(0, length) + "...";
}

export function getInitials(name: string): string {
  return name
    .split(" ")
    .map((n) => n[0])
    .join("")
    .toUpperCase()
    .slice(0, 2);
}

export function formatTokenCount(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(1)}K`;
  return String(n);
}

/**
 * The API serializes message types in PascalCase ("CodeReview") while the UI's
 * MESSAGE_TYPES constants are snake_case ("code_review"). Normalize any casing.
 */
export function normalizeMessageType(raw: string | undefined): string {
  if (!raw) return "text";
  const compact = raw.toLowerCase().replace(/_/g, "");
  const map: Record<string, string> = {
    text: "text",
    question: "question",
    progress: "progress",
    error: "error",
    plan: "plan",
    codereview: "code_review",
    qareport: "qa_report",
    answer: "answer",
    toolcall: "tool_call",
    toolresult: "tool_result",
    systemevent: "system_event",
    externalcomment: "external_comment",
  };
  return map[compact] ?? "text";
}

/** Message metadata arrives as a JSON string from the API — parse defensively. */
export function parseMessageMetadata(
  metadata: unknown
): Record<string, unknown> | null {
  if (!metadata) return null;
  if (typeof metadata === "object") return metadata as Record<string, unknown>;
  if (typeof metadata === "string") {
    try {
      const parsed = JSON.parse(metadata);
      return typeof parsed === "object" && parsed !== null ? parsed : null;
    } catch {
      return null;
    }
  }
  return null;
}

/**
 * Agents run on the developer's own AI subscription, so Orkka charges nothing: a USD figure in
 * the UI is only an estimate of the run at the model's list price. Below this it rounds to
 * nothing worth showing — callers skip the figure rather than print "$0.00".
 */
export const MIN_SHOWN_USD = 0.005;

/** True when a list-price estimate is large enough to be worth showing at all. */
export function hasCostEstimate(usd: number | null | undefined): usd is number {
  return typeof usd === "number" && Number.isFinite(usd) && usd >= MIN_SHOWN_USD;
}

/**
 * A list-price estimate in US dollars: "≈ $0.42". Always approximate, never a charge — put it
 * beside a "list price" label where there is room. Amounts under a cent read "< $0.01".
 */
export function formatUsdEstimate(usd: number): string {
  if (usd > 0 && usd < 0.01) return "< $0.01";
  return `≈ ${formatUsd(usd)}`;
}

/** Plain "$0.42", for the ends of a range whose label already says it is approximate. */
export function formatUsd(usd: number): string {
  return new Intl.NumberFormat("en-US", {
    style: "currency",
    currency: "USD",
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  }).format(usd);
}

export function getMonthDateRange(offset = 0): { from: string; to: string } {
  const now = new Date();
  const from = new Date(now.getFullYear(), now.getMonth() + offset, 1);
  const to = new Date(now.getFullYear(), now.getMonth() + offset + 1, 0, 23, 59, 59);
  return { from: from.toISOString(), to: to.toISOString() };
}

export function formatShortDate(date: string): string {
  return new Date(date).toLocaleDateString("en-US", {
    month: "short",
    day: "numeric",
  });
}

/**
 * Agents sometimes save a fresh document instead of revising the existing one, leaving
 * several rows with the same folder + title. Pickers should only offer the newest copy
 * of each name — the older rows are stale versions, not distinct documents.
 */
export function latestDocumentVersions<
  T extends { id: string; title: string; path: string; updatedAt: string },
>(documents: T[]): T[] {
  const newest = new Map<string, T>();
  for (const doc of documents) {
    const key = `${doc.path}\u0000${doc.title.trim().toLowerCase()}`;
    const current = newest.get(key);
    // ULIDs sort chronologically, so the id breaks updatedAt ties.
    if (
      !current ||
      doc.updatedAt > current.updatedAt ||
      (doc.updatedAt === current.updatedAt && doc.id > current.id)
    ) {
      newest.set(key, doc);
    }
  }
  return documents.filter(
    (doc) => newest.get(`${doc.path}\u0000${doc.title.trim().toLowerCase()}`) === doc
  );
}

/**
 * Uploads the documents page accepts. Text formats become markdown documents; a PDF is
 * stored as the file it is and rendered by a viewer, so it goes through a different path
 * (`uploadDocumentFile`) and is deliberately NOT accepted by `documentUploadTitle`.
 */
export const DOCUMENT_UPLOAD_ACCEPT = ".md,.markdown,.txt,.pdf";

const DOCUMENT_UPLOAD_EXTENSION = /\.(md|markdown|txt)$/i;

const PDF_EXTENSION = /\.pdf$/i;

/**
 * True for files that must be stored as-is rather than decoded into a document body.
 * The file input's `accept` list is only a dialog hint, so this — not the dialog — is
 * what routes an upload to the file path.
 */
export function isPdfUpload(fileName: string): boolean {
  return PDF_EXTENSION.test(fileName);
}

/**
 * True when a document is a stored file rather than a markdown body. `fileName` is set
 * by the API for exactly those, so it is the one flag every viewer branches on.
 */
export function isFileDocument(doc: { fileName?: string | null }): boolean {
  return Boolean(doc.fileName);
}

/**
 * Title for an uploaded *text* document, or null if the file is not a supported text
 * format. A bare name with no extension is rejected too — an extension is what makes the
 * format knowable, and the old code silently accepted anything. PDFs return null here on
 * purpose: UTF-8 decoding one produced the binary garbage this guard was added to stop.
 */
export function documentUploadTitle(fileName: string): string | null {
  if (!DOCUMENT_UPLOAD_EXTENSION.test(fileName)) return null;
  const title = fileName.replace(DOCUMENT_UPLOAD_EXTENSION, "").trim();
  return title || null;
}

/**
 * Secondary guard for a binary renamed to a text extension: NUL bytes survive UTF-8
 * decoding and never occur in real text, and U+FFFD means bytes that were not valid
 * UTF-8 at all. A stray replacement char can appear in legitimately mis-encoded text,
 * so only a meaningful concentration counts as binary.
 */
export function looksBinary(content: string): boolean {
  const nul = String.fromCharCode(0);
  const replacementChar = String.fromCharCode(0xfffd);
  if (content.includes(nul)) return true;
  let replacements = 0;
  for (const ch of content) {
    if (ch === replacementChar) replacements++;
  }
  return replacements > 8 && replacements / content.length > 0.01;
}

export function buildFileTree(paths: string[]): TreeNode[] {
  const root: TreeNode[] = [];

  for (const filePath of paths) {
    const parts = filePath.split("/");
    let current = root;

    for (let i = 0; i < parts.length; i++) {
      const name = parts[i];
      const isLast = i === parts.length - 1;
      const partialPath = parts.slice(0, i + 1).join("/");

      let existing = current.find((n) => n.name === name);
      if (!existing) {
        existing = {
          name,
          path: partialPath,
          isDirectory: !isLast,
          children: [],
        };
        current.push(existing);
      }
      if (!isLast) {
        existing.isDirectory = true;
      }
      current = existing.children;
    }
  }

  function sortTree(nodes: TreeNode[]): TreeNode[] {
    return nodes
      .sort((a, b) => {
        if (a.isDirectory !== b.isDirectory) return a.isDirectory ? -1 : 1;
        return a.name.localeCompare(b.name);
      })
      .map((n) => ({ ...n, children: sortTree(n.children) }));
  }

  return sortTree(root);
}

// ── Model catalog helpers ────────────────────────────────────────────
// Shared by the Team page (agent model + image provider) and the task create form in both
// editions, so grouping and image-option derivation can't drift between them.

/**
 * Groups models for <optgroup> rendering: sorted by the catalog's own sortOrder, then grouped
 * by provider in first-appearance order. Sorting by sortOrder alone would interleave providers
 * and shatter the groups.
 */
export function groupModelsByProvider<T extends { provider: string; sortOrder?: number }>(
  models: T[]
): [string, T[]][] {
  const groups = new Map<string, T[]>();
  const ordered =
    models.some((m) => typeof m.sortOrder === "number")
      ? [...models].sort((a, b) => (a.sortOrder ?? 0) - (b.sortOrder ?? 0))
      : models;

  for (const m of ordered) {
    const list = groups.get(m.provider) ?? [];
    list.push(m);
    groups.set(m.provider, list);
  }
  return [...groups.entries()];
}

/**
 * Fallback provider derivation for a model id the catalog doesn't list — mirrors the backend's
 * ModelProviders.FromModelId. Prefer looking the id up in the catalog; this only covers ids
 * that legitimately aren't there, like locally-pulled Ollama tags and the models an
 * OpenCode install's own provider logins expose.
 */
export function providerFromModelId(modelId: string): string {
  const id = modelId.trim().toLowerCase();
  if (!id) return "Anthropic";
  // OpenCode first of all: its ids EMBED another vendor's id ("opencode/anthropic/…"), so any
  // later test would match the inner vendor and name the wrong provider. The harness owns the
  // id, not the vendor inside it.
  if (id.startsWith("opencode")) return "OpenCode";
  // Copilot and Ollama next: they serve models whose bare ids ("gpt-5", "gpt-oss") would
  // match the vendor prefixes below, so their catalog ids are namespaced.
  if (id.startsWith("copilot")) return "GitHubCopilot";
  if (id.startsWith("ollama")) return "Ollama";
  if (id.startsWith("pixellab")) return "PixelLab";
  if (id.startsWith("gemini")) return "Google";
  if (id.startsWith("gpt") || id.startsWith("codex") || /^o\d/.test(id)) return "OpenAI";
  // Bare ids — nothing else in the catalog starts with "grok", so no namespace is needed.
  if (id.startsWith("grok")) return "XAI";
  return "Anthropic";
}

/**
 * The git command that puts an agent's finished branch in front of a human so they can
 * run and test it — the missing last step of the "agents built it, now verify it" loop.
 * Agent branches live on the forge, so they must be fetched first.
 */
export function checkoutCommand(branchName: string): string {
  return `git fetch origin && git switch ${branchName}`;
}
