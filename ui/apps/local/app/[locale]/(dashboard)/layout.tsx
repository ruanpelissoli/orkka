import { DashboardShell } from "@/components/layout";
import { WorkspaceProvider } from "@/components/workspace/WorkspaceProvider";
import { getClientWorkspace } from "@/lib/workspace";
import {
  PermissionsProvider,
  ProjectProvider,
} from "@/components/providers";
import { LocalSignalRProvider } from "@/components/providers/LocalSignalRProvider";
import { LocalUserPreferencesProvider } from "@/components/providers/LocalUserPreferencesProvider";
import { ToastProvider } from "@/components/ui";
import { ShellLoading } from "@/components/page";
import { useApiQuery } from "@/hooks/use-api-query";
import { api } from "@/lib/api";
import type { OrgMe, Workspace } from "@/lib/types";

/**
 * The app shell. Loads the caller's workspaces and their permissions in the browser (the
 * session cookie authenticates), then renders the providers every page relies on.
 */
export default function DashboardLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  const { data } = useApiQuery(async () => {
    // Which job's workspace this browser is in: the `orkka_ws` cookie when it names one of the
    // caller's workspaces, else the active one. A cookie naming a workspace the caller has since
    // left is stale — every workspace-scoped request would 403 — so nothing else renders until
    // the provider re-points it.
    const cookieWorkspace = getClientWorkspace();
    let workspaces: Workspace[] = [];
    try {
      workspaces = await api.workspaces.mine("");
    } catch {
      // The switcher hides itself without a list; requests fall back to the active workspace.
    }
    const current =
      workspaces.find((w) => w.orgId === cookieWorkspace) ??
      workspaces.find((w) => w.isActive) ??
      workspaces[0] ??
      null;
    const stale =
      !!cookieWorkspace &&
      workspaces.length > 0 &&
      !workspaces.some((w) => w.orgId === cookieWorkspace);

    let me: OrgMe | null = null;
    if (!stale) {
      try {
        me = await api.org.me("");
      } catch {
        // Fail open on UI gating only — every action is enforced by the API. A null `me`
        // means controls are hidden until the next refresh.
      }
    }
    return { workspaces, currentId: current?.orgId ?? null, stale, me };
  }, []);

  if (!data) return <ShellLoading />;

  if (data.stale) {
    return <WorkspaceProvider workspaces={data.workspaces} currentId={data.currentId} stale />;
  }

  return (
    <WorkspaceProvider workspaces={data.workspaces} currentId={data.currentId}>
    <PermissionsProvider me={data.me}>
      <LocalUserPreferencesProvider initial={data.me?.preferences ?? null}>
        <ProjectProvider>
          <LocalSignalRProvider>
            <ToastProvider>
              <DashboardShell>{children}</DashboardShell>
            </ToastProvider>
          </LocalSignalRProvider>
        </ProjectProvider>
      </LocalUserPreferencesProvider>
    </PermissionsProvider>
    </WorkspaceProvider>
  );
}
