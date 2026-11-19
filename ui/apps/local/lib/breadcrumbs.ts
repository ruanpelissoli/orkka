/**
 * The top bar's breadcrumb trail: a locale-free pathname (`/p/01J…/tasks/new`) split into
 * crumbs, each labelled with the string the rest of the UI already uses for that section (the
 * sidebar, the settings nav, the page titles) so a translated page never shows a raw URL
 * segment. Ids and anything unknown keep the segment as typed.
 */

/** A translation key, in the namespace the label lives in. */
export interface CrumbLabelKey {
  namespace: "dashboard" | "common";
  key: string;
}

export interface Crumb {
  href: string;
  isCurrent: boolean;
  /** The key to translate, or null for an id/unknown segment shown as `text`. */
  labelKey: CrumbLabelKey | null;
  /** The project id segment: the caller shows the project's name. */
  isProject: boolean;
  text: string;
}

const dash = (key: string): CrumbLabelKey => ({ namespace: "dashboard", key });

/** The project's own sections — the sidebar's labels (`getProjectNav`). */
const PROJECT_SECTIONS = new Set([
  "team",
  "activity",
  "skills",
  "automation",
  "memory",
  "roadmap",
  "board",
  "tasks",
  "repos",
  "documents",
  "design",
  "architect",
  "settings",
]);

/** Settings → … — the settings nav's labels. */
const SETTINGS_SECTIONS: Record<string, string> = {
  preferences: "settingsNav.preferences",
  notifications: "settingsNav.notifications",
  workspace: "settingsNav.workspace",
  machine: "settingsNav.machine",
  trackers: "settingsNav.trackers",
  usage: "settingsNav.usage",
  "api-keys": "settingsNav.mcpKeys",
};

/** Top-level pages. */
const TOP_LEVEL: Record<string, CrumbLabelKey> = {
  dashboard: { namespace: "common", key: "nav.dashboard" },
  board: dash("sidebar.workspaceBoard"),
  inbox: dash("sidebar.inbox"),
  notifications: dash("sidebar.notifications"),
  projects: dash("sidebar.projects"),
  settings: dash("settingsNav.title"),
};

function labelFor(segment: string, parent: string | undefined, parentIsProject: boolean, isFirst: boolean): CrumbLabelKey | null {
  if (parentIsProject && PROJECT_SECTIONS.has(segment)) return dash(`sidebar.${segment}`);
  if (isFirst && TOP_LEVEL[segment]) return TOP_LEVEL[segment];
  if (parent === "settings" && SETTINGS_SECTIONS[segment]) return dash(SETTINGS_SECTIONS[segment]);
  if (segment === "new" && (parent === "tasks" || parent === "projects")) return dash("mobileNav.new");
  if (segment === "review") return dash("reviewPage.breadcrumb");
  if (segment === "merge") return dash("meta.merge");
  return null;
}

function safeDecode(segment: string): string {
  try {
    return decodeURIComponent(segment);
  } catch {
    return segment;
  }
}

/** The crumbs for a locale-free pathname; `projectId` is the route's project param, if any. */
export function buildCrumbs(pathname: string, projectId?: string): Crumb[] {
  const parts = pathname.split("/").filter(Boolean);
  const crumbs: Crumb[] = [];
  parts.forEach((segment, index) => {
    // "p" is only a route prefix.
    if (segment === "p" && index === 0) return;
    const href = "/" + parts.slice(0, index + 1).join("/");
    const isCurrent = index === parts.length - 1;
    if (projectId && segment === projectId) {
      crumbs.push({ href, isCurrent, labelKey: null, isProject: true, text: segment });
      return;
    }
    const parent = index > 0 ? parts[index - 1] : undefined;
    const parentIsProject = Boolean(projectId) && parent === projectId;
    const labelKey = labelFor(segment, parent, parentIsProject, index === 0);
    // Ids keep their own spelling: a task key is E2E-1, not "E2E 1".
    crumbs.push({ href, isCurrent, labelKey, isProject: false, text: safeDecode(segment) });
  });
  return crumbs;
}
