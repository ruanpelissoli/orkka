import type { RepoForge } from "./types";

/**
 * Reads a repository URL the way a developer types it — https, ssh://, scp-style
 * (`git@host:org/repo.git`, including ~/.ssh/config host aliases such as
 * `git@github-work:org/repo.git`), or a folder path. Pure string work.
 *
 * Mirrors orkka-api `Orkka.SharedKernel/Utilities/RepoUrl.cs` (`Classify`, `DetectForge`,
 * `HasEmbeddedCredential`) so the add-repo form says what the server will decide before the
 * request is sent. Keep the two in step.
 */

/** How the URL is written. `local` = a folder path (or file://), never a remote. */
export type RepoUrlKind = "https" | "ssh" | "scp" | "local";

export const REPO_FORGES: readonly RepoForge[] = [
  "github",
  "azuredevops",
  "gitlab",
  "bitbucket",
  "other",
];

const TOKEN_PREFIXES = [
  "ghp_",
  "gho_",
  "ghs_",
  "ghu_",
  "github_pat_",
  "glpat-",
  "x-access-token",
  "oauth2",
];

/** `[user@]host:path` — git's scp-like syntax. A Windows drive letter (`C:\x`) is one
 * character before the colon, and git treats a slash before the first colon as local. */
function scpParts(value: string): { host: string; path: string } | null {
  const colon = value.indexOf(":");
  if (colon <= 0 || colon === value.length - 1) return null;

  const before = value.slice(0, colon);
  if (before.includes("/") || before.includes("\\")) return null;

  const at = before.lastIndexOf("@");
  const host = at >= 0 ? before.slice(at + 1) : before;
  if (host.length <= 1) return null;

  return { host, path: value.slice(colon + 1) };
}

/** True for a git remote; false for a folder path (or nothing at all). */
export function isRemoteRepoUrl(url: string | null | undefined): boolean {
  if (!url || !url.trim()) return false;
  const value = url.trim();
  if (value.toLowerCase().startsWith("file://")) return false;
  if (value.indexOf("://") > 0) return true;
  return scpParts(value) !== null;
}

function parseAbsolute(value: string): URL | null {
  try {
    return new URL(value);
  } catch {
    return null;
  }
}

/** The lower-cased host of a remote, or null for a folder or an unparseable URL. */
export function repoUrlHost(url: string | null | undefined): string | null {
  if (!isRemoteRepoUrl(url)) return null;
  const value = url!.trim();
  if (value.includes("://")) {
    const parsed = parseAbsolute(value);
    return parsed && parsed.hostname.length > 0 ? parsed.hostname.toLowerCase() : null;
  }
  return scpParts(value)?.host.toLowerCase() ?? null;
}

/** The forge serving the URL, from its host. An ssh host alias reads as "other" — the user
 * overrides it. */
export function detectRepoForge(url: string | null | undefined): RepoForge {
  const host = repoUrlHost(url);
  if (!host) return "other";
  if (host === "github.com" || host.endsWith(".github.com")) return "github";
  if (
    host === "dev.azure.com" ||
    host === "ssh.dev.azure.com" ||
    host === "vs-ssh.visualstudio.com" ||
    host.endsWith(".visualstudio.com")
  )
    return "azuredevops";
  if (host === "gitlab.com" || host.startsWith("gitlab.")) return "gitlab";
  if (host === "bitbucket.org") return "bitbucket";
  return "other";
}

/**
 * True when an http(s) URL carries a secret: a password (`user:token@`) or a user name that is
 * itself an access token (`ghp_…@github.com`). A plain user name is fine — Azure DevOps hands
 * out `https://{org}@dev.azure.com/…` clone URLs. The server rejects these.
 */
export function repoUrlHasEmbeddedCredential(url: string | null | undefined): boolean {
  const value = url?.trim() ?? "";
  const schemeEnd = value.indexOf("://");
  if (schemeEnd <= 0) return false;
  const scheme = value.slice(0, schemeEnd).toLowerCase();
  if (scheme !== "https" && scheme !== "http") return false;
  if (!parseAbsolute(value)) return false;

  const rest = value.slice(schemeEnd + 3);
  const authorityEnd = rest.search(/[/?#]/);
  const authority = authorityEnd >= 0 ? rest.slice(0, authorityEnd) : rest;
  const at = authority.lastIndexOf("@");
  if (at <= 0) return false;

  let user = authority.slice(0, at);
  try {
    user = decodeURIComponent(user);
  } catch {
    // Keep it raw — a malformed escape is still not a credential we can see.
  }
  const lower = user.toLowerCase();
  return user.includes(":") || TOKEN_PREFIXES.some((prefix) => lower.startsWith(prefix));
}

/** How the URL is written, or null for an empty field. */
export function repoUrlKind(url: string | null | undefined): RepoUrlKind | null {
  if (!url || !url.trim()) return null;
  if (!isRemoteRepoUrl(url)) return "local";
  const value = url.trim().toLowerCase();
  if (value.startsWith("https://") || value.startsWith("http://")) return "https";
  if (value.includes("://")) return "ssh";
  return "scp";
}

/** The repo's name as the URL spells it — the last path segment without `.git`. */
export function repoNameFromUrl(url: string | null | undefined): string {
  if (!isRemoteRepoUrl(url)) return "";
  const value = url!.trim();
  let path: string;
  if (value.includes("://")) {
    path = parseAbsolute(value)?.pathname ?? "";
  } else {
    path = scpParts(value)?.path ?? "";
  }
  const segments = path.split("/").filter(Boolean);
  if (segments.length === 0) return "";
  let name = segments[segments.length - 1];
  try {
    name = decodeURIComponent(name);
  } catch {
    // Keep it raw.
  }
  return name.toLowerCase().endsWith(".git") ? name.slice(0, -4) : name;
}

export interface RepoUrlClassification {
  kind: RepoUrlKind;
  host: string | null;
  forge: RepoForge;
  hasEmbeddedCredential: boolean;
  /** The name to prefill. Empty when the URL has no path to take it from. */
  suggestedName: string;
  /** A remote the server would accept: has a host and no secret in it. */
  acceptable: boolean;
}

/** Everything the add-repo form shows as the user types. Null for an empty field. */
export function classifyRepoUrl(url: string | null | undefined): RepoUrlClassification | null {
  const kind = repoUrlKind(url);
  if (kind === null) return null;
  const host = repoUrlHost(url);
  const hasEmbeddedCredential = repoUrlHasEmbeddedCredential(url);
  return {
    kind,
    host,
    forge: detectRepoForge(url),
    hasEmbeddedCredential,
    suggestedName: repoNameFromUrl(url),
    acceptable: kind !== "local" && host !== null && !hasEmbeddedCredential,
  };
}
