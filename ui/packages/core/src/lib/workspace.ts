/**
 * Which workspace (Orkka org) a request is for.
 *
 * A developer who works several jobs has one workspace per job. The API resolves the caller's
 * membership in the org named by the `X-Orkka-Workspace` header, and the tasks hub takes the
 * same id as `?workspace=`; without either it falls back to the caller's active workspace.
 *
 * The current id lives in the `orkka_ws` cookie, which the browser reads directly (the API client
 * runs in the browser only). The mobile app can register its own client getter, the same pattern
 * as `lib/locale.ts`.
 */

export const WORKSPACE_COOKIE = "orkka_ws";
export const WORKSPACE_HEADER = "X-Orkka-Workspace";

const ULID = /^[0-9A-HJKMNP-TV-Z]{26}$/i;

/** A well-formed workspace (org) id — anything else is never sent, the API would 400. */
export function isWorkspaceId(value: string | null | undefined): value is string {
  return !!value && ULID.test(value);
}

let clientGetter: (() => string | null) | null = null;

/** For hosts without `document.cookie` (React Native). */
export function registerClientWorkspaceGetter(fn: () => string | null): void {
  clientGetter = fn;
}

export function getClientWorkspace(): string | null {
  if (clientGetter) {
    const id = clientGetter();
    return isWorkspaceId(id) ? id : null;
  }
  if (typeof document === "undefined") return null;
  const raw = document.cookie
    .split("; ")
    .find((c) => c.startsWith(`${WORKSPACE_COOKIE}=`))
    ?.split("=")[1];
  return isWorkspaceId(raw) ? raw : null;
}

/** Sets (or, with null, clears) the browser's current workspace. A year, site-wide. */
export function setClientWorkspace(orgId: string | null): void {
  if (typeof document === "undefined") return;
  if (orgId && isWorkspaceId(orgId)) {
    document.cookie = `${WORKSPACE_COOKIE}=${orgId}; path=/; max-age=31536000; samesite=lax`;
  } else {
    document.cookie = `${WORKSPACE_COOKIE}=; path=/; max-age=0; samesite=lax`;
  }
}

/** The workspace this request is for, or null for "the caller's active one". */
export async function resolveWorkspace(): Promise<string | null> {
  return getClientWorkspace();
}

/** `{ "X-Orkka-Workspace": id }` when a workspace is set, else `{}`. */
export async function workspaceHeaders(): Promise<Record<string, string>> {
  const id = await resolveWorkspace();
  return id ? { [WORKSPACE_HEADER]: id } : {};
}

/** The tasks hub URL for a workspace: `?workspace=<id>` when one is set. */
export function withWorkspaceQuery(url: string, orgId: string | null): string {
  if (!orgId) return url;
  return `${url}${url.includes("?") ? "&" : "?"}workspace=${encodeURIComponent(orgId)}`;
}
