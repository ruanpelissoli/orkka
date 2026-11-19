import type { TrackerRoutingRuleKind, TrackerStatusStep } from "./types";

/**
 * Client-side rules for a project's ticket source — the same ones orkka-api's
 * `SaveProjectTrackerValidator` / `TrackerSubpath` enforce, so the settings card can say what
 * is wrong before a save round-trips. Keep the two in step.
 */

export const TRACKER_MIN_POLL_SECONDS = 60;
export const TRACKER_MAX_POLL_SECONDS = 300;
export const TRACKER_DEFAULT_POLL_SECONDS = 90;
export const TRACKER_DEFAULT_BRANCH_TEMPLATE = "{type}/{key}-{slug}";

/** What a new ticket source picks up until the tracker's own statuses say otherwise. */
export const TRACKER_DEFAULT_INTAKE_STATUSES = ["To Do"] as const;

/** The Orkka steps a ticket's tracker status can follow, in pipeline order. */
export const TRACKER_STATUS_STEPS: TrackerStatusStep[] = [
  "pickedUp",
  "planning",
  "developing",
  "inReview",
  "prOpened",
  "merged",
];

/** Routing-rule kinds each tracker has: Jira components and labels; Azure DevOps area paths and
 * tags; GitHub labels (an issue's repository is matched to the project's repos first). */
export function routingRuleKindsFor(kind: "jira" | "ado" | "github"): TrackerRoutingRuleKind[] {
  if (kind === "github") return ["label"];
  return kind === "ado" ? ["areaPath", "label"] : ["component", "label"];
}

/** What a developer runs on their own machine to add a connection; the token stays there.
 * GitHub uses the developer's `gh` login by default (`--token` asks for a token instead). */
export const TRACKER_ADD_COMMANDS = {
  jira: "./orkka tracker add jira --site acme.atlassian.net --email you@acme.com",
  ado: "./orkka tracker add ado --org https://dev.azure.com/acme",
  github: "./orkka tracker add github",
} as const;

/** The GitHub host a new connection talks to unless the developer names a GHE.com one. */
export const GITHUB_DEFAULT_HOST = "github.com";

/** The single-select field a GitHub project keeps its status in, unless the source names another. */
export const GITHUB_DEFAULT_STATUS_FIELD = "Status";

/**
 * A GitHub host the connection can use: github.com, or a GHE.com data-residency host
 * (`acme.ghe.com`). A URL is reduced to its host. GitHub Enterprise Server (any other host) is
 * not supported. Null when the input is not one of those.
 */
export function normalizeGitHubHost(value: string | null | undefined): string | null {
  let text = (value ?? "").trim().toLowerCase();
  if (!text) return GITHUB_DEFAULT_HOST;
  text = text.replace(/^https?:\/\//, "").split("/")[0];
  if (text.startsWith("api.")) text = text.slice(4);
  if (text === "github.com" || text === "www.github.com") return GITHUB_DEFAULT_HOST;
  if (/^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.ghe\.com$/.test(text)) return text;
  return null;
}

/**
 * A folder inside a repository, as the epic mapping stores it: forward slashes, no leading or
 * trailing slash, no `.`/`..` segments, no drive, root or home. `ok: false` = refused (the
 * server refuses it rather than cleaning it up); `normalized: null` = the repo root.
 */
export function normalizeTrackerSubpath(
  value: string | null | undefined
): { ok: boolean; normalized: string | null } {
  if (!value || !value.trim()) return { ok: true, normalized: null };

  const path = value.trim().replace(/\\/g, "/");
  if (path.startsWith("/") || path.includes(":") || path.startsWith("~")) {
    return { ok: false, normalized: null };
  }

  const segments = path.split("/").filter((s) => s.length > 0);
  if (segments.length === 0) return { ok: true, normalized: null };
  if (segments.some((s) => s === "." || s === ".." || s.trim().length !== s.length)) {
    return { ok: false, normalized: null };
  }

  const normalized = segments.join("/");
  return normalized.length <= 500 ? { ok: true, normalized } : { ok: false, normalized: null };
}

export function isValidBranchTemplate(template: string | null | undefined): boolean {
  if (!template || !template.trim()) return true; // blank = the server's default
  return template.includes("{key}") && template.length <= 200;
}

export function isValidPollInterval(seconds: number): boolean {
  return (
    Number.isInteger(seconds) &&
    seconds >= TRACKER_MIN_POLL_SECONDS &&
    seconds <= TRACKER_MAX_POLL_SECONDS
  );
}

/** Keys of epics mapped more than once (case-insensitive, like the server). */
export function duplicateEpicKeys(keys: string[]): string[] {
  const seen = new Map<string, number>();
  for (const key of keys) {
    const k = key.trim().toLowerCase();
    seen.set(k, (seen.get(k) ?? 0) + 1);
  }
  return [...seen.entries()].filter(([, n]) => n > 1).map(([k]) => k);
}
