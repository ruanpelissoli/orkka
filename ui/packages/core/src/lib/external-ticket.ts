import type { TrackerKind } from "./types";

/**
 * Tasks imported from a Jira / Azure DevOps / GitHub ticket carry its key, URL and epic. These helpers
 * are the client-side reading of those fields: which tracker a URL belongs to, and the title
 * without the "[KEY] " prefix ingestion writes into it (the card shows the key as a badge).
 */

/** The tracker a ticket URL lives on, from its host; null when it can't be told. */
export function trackerKindFromUrl(url: string | null | undefined): TrackerKind | null {
  if (!url) return null;
  let host: string;
  try {
    host = new URL(url).hostname.toLowerCase();
  } catch {
    return null;
  }
  if (host === "atlassian.net" || host.endsWith(".atlassian.net")) return "jira";
  if (host === "dev.azure.com" || host.endsWith(".visualstudio.com")) return "ado";
  if (host === "github.com" || host.endsWith(".ghe.com")) return "github";
  return null;
}

/**
 * The title to display: a leading "[KEY] " is dropped only when KEY is this task's own
 * external key, so a title that merely starts with brackets is left alone.
 */
export function stripTicketKey(title: string, externalKey: string | null | undefined): string {
  if (!externalKey) return title;
  const prefix = `[${externalKey}]`;
  if (!title.startsWith(prefix)) return title;
  const rest = title.slice(prefix.length).trimStart();
  return rest.length > 0 ? rest : title;
}

/**
 * Which tracker an ExternalComment message came from. Ingestion writes
 * "**Author** commented on KEY in Jira (…)" / "… in Azure DevOps (…)" / "… in GitHub (…)".
 */
export function trackerKindFromComment(content: string): TrackerKind | null {
  const head = content.slice(0, 400);
  if (/ in Azure DevOps \(/.test(head)) return "ado";
  if (/ in GitHub \(/.test(head)) return "github";
  if (/ in Jira \(/.test(head)) return "jira";
  return null;
}

/** Board epic filter values: "" = all, this sentinel = tasks not from a tracker. */
export const NO_EPIC_FILTER = "__no-tracker__";

export function matchesEpicFilter(
  task: { externalKey?: string | null; externalEpic?: string | null },
  filter: string
): boolean {
  if (filter === "") return true;
  if (filter === NO_EPIC_FILTER) return !task.externalKey;
  return (task.externalEpic ?? "") === filter;
}

/** Distinct epics across the given tasks, sorted for the filter's option list. */
export function distinctEpics(tasks: { externalEpic?: string | null }[]): string[] {
  return [...new Set(tasks.map((t) => t.externalEpic).filter((e): e is string => !!e))].sort(
    (a, b) => a.localeCompare(b)
  );
}
