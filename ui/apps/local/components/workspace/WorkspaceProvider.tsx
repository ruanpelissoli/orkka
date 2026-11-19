import { createContext, useCallback, useContext, useEffect, useMemo, useRef } from "react";
import { useAuth } from "@/lib/local-auth-client";
import { useLocale } from "use-intl";
import { localizeHref } from "@/i18n/navigation";
import { api, invalidateApiCache } from "@/lib/api";
import { disconnect } from "@/lib/signalr";
import { getClientWorkspace, setClientWorkspace } from "@/lib/workspace";
import { hardNavigate } from "@/lib/hard-navigate";
import type { Workspace } from "@/lib/types";

interface WorkspaceContextValue {
  workspaces: Workspace[];
  current: Workspace | null;
  /** Makes `orgId` current (cookie + the API's active workspace) and reloads into it. */
  switchTo: (orgId: string, path?: string) => Promise<void>;
  /** Creates a workspace, switches to it, and opens its onboarding. */
  create: (name: string) => Promise<void>;
}

const WorkspaceContext = createContext<WorkspaceContextValue | null>(null);

export function useWorkspaces(): WorkspaceContextValue {
  const ctx = useContext(WorkspaceContext);
  if (!ctx) throw new Error("useWorkspaces must be used within a WorkspaceProvider");
  return ctx;
}

/** Like useWorkspaces, but null outside the provider (shared components, tests). */
export function useOptionalWorkspaces(): WorkspaceContextValue | null {
  return useContext(WorkspaceContext);
}

interface WorkspaceProviderProps {
  workspaces: Workspace[];
  /** The workspace the layout loaded this page for. */
  currentId: string | null;
  /**
   * The browser's `orkka_ws` cookie names a workspace the caller no longer belongs to. The
   * layout renders nothing else in that case; this re-points the cookie and reloads.
   */
  stale?: boolean;
  children?: React.ReactNode;
}

/**
 * Which job's workspace this browser is in. The dashboard layout loaded the page for
 * `currentId` (from the `orkka_ws` cookie, or the active workspace when there is none);
 * this keeps the cookie in step so every later request — API fetches and SignalR — carries
 * the same `X-Orkka-Workspace`.
 */
export function WorkspaceProvider({ workspaces, currentId, stale, children }: WorkspaceProviderProps) {
  const { getToken } = useAuth();
  const locale = useLocale();
  const current = workspaces.find((w) => w.orgId === currentId) ?? null;
  const fixed = useRef(false);

  const localized = useCallback(
    (path: string) => localizeHref(path, locale),
    [locale]
  );

  // First visit after sign-in (no cookie): adopt the workspace the layout picked, which is
  // the caller's active one. A stale cookie is replaced and the page reloaded.
  useEffect(() => {
    if (!currentId || fixed.current) return;
    if (getClientWorkspace() === currentId) return;
    fixed.current = true;
    setClientWorkspace(currentId);
    if (stale) hardNavigate(localized("/board"));
  }, [currentId, stale, localized]);

  const switchTo = useCallback(
    // A switched-to workspace opens on its board: every project of that job at once.
    async (orgId: string, path = "/board") => {
      setClientWorkspace(orgId);
      invalidateApiCache();
      await disconnect().catch(() => undefined);
      try {
        const token = await getToken();
        // Best effort: it only decides where a request WITHOUT a workspace lands (a new tab,
        // the mobile app, an email link). The cookie already decides this browser. The token is
        // empty in the browser — the session cookie authenticates.
        await api.workspaces.setActive(orgId, token);
      } catch {
        // ignore
      }
      hardNavigate(localized(path));
    },
    [getToken, localized]
  );

  const create = useCallback(
    async (name: string) => {
      const token = await getToken();
      const created = await api.workspaces.create(name, token);
      await switchTo(created.orgId, "/board");
    },
    [getToken, switchTo]
  );

  const value = useMemo(
    () => ({ workspaces, current, switchTo, create }),
    [workspaces, current, switchTo, create]
  );

  return <WorkspaceContext.Provider value={value}>{children}</WorkspaceContext.Provider>;
}
