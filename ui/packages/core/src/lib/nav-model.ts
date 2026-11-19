import type { Permission } from "./constants";

/**
 * Navigation model shared by every Orkka client — the hosted web app's Sidebar,
 * MobileDrawer and MobileTabBar, and the React Native app's tab bar. It lives in core
 * (not `packages/ui`) so it can be consumed without pulling in DOM components; the icon
 * names are a closed set that each renderer maps to its own glyphs.
 *
 * Labels are translated at the call site (dashboard namespace) via the `t` argument.
 *
 * Named `nav-model` (not `nav`) because every app has a `lib/nav.ts` of its own, and the
 * app directory wins the `@/*` fallback chain — `@/lib/nav` would never reach this file.
 */

type NavIconName =
  | "home"
  | "board"
  | "roadmap"
  | "tasks"
  | "repos"
  | "documents"
  | "design"
  | "team"
  | "architect"
  | "members"
  | "settings"
  | "bell"
  | "user"
  | "terminal"
  | "inbox"
  | "activity"
  | "skills"
  | "automation"
  | "memory"
  | "plus";

interface NavItem {
  key: string;
  href: string;
  icon: NavIconName;
  label: string;
  exact?: boolean;
}

interface MobileTab extends NavItem {
  isCreate?: boolean;
  permission?: Permission;
}

type NavTranslator = (key: string) => string;

function getProjectNav(projectId: string, t: NavTranslator): NavItem[] {
  // Ordered as the user should walk the project: what happened (overview), who is
  // doing it (team), where it is going (roadmap), what is moving now (board), then
  // the individual work items (tasks). Inbox is not repeated here — it is
  // cross-project and already sits in the global nav below the divider.
  return [
    { key: "overview", label: t("sidebar.overview"), href: `/p/${projectId}`, icon: "home", exact: true },
    { key: "team", label: t("sidebar.team"), href: `/p/${projectId}/team`, icon: "team" },
    { key: "activity", label: t("sidebar.activity"), href: `/p/${projectId}/activity`, icon: "activity" },
    { key: "skills", label: t("sidebar.skills"), href: `/p/${projectId}/skills`, icon: "skills" },
    { key: "automation", label: t("sidebar.automation"), href: `/p/${projectId}/automation`, icon: "automation" },
    { key: "memory", label: t("sidebar.memory"), href: `/p/${projectId}/memory`, icon: "memory" },
    { key: "roadmap", label: t("sidebar.roadmap"), href: `/p/${projectId}/roadmap`, icon: "roadmap" },
    { key: "board", label: t("sidebar.board"), href: `/p/${projectId}/board`, icon: "board" },
    { key: "tasks", label: t("sidebar.tasks"), href: `/p/${projectId}/tasks`, icon: "tasks" },
    { key: "repos", label: t("sidebar.repos"), href: `/p/${projectId}/repos`, icon: "repos" },
    { key: "documents", label: t("sidebar.documents"), href: `/p/${projectId}/documents`, icon: "documents" },
    { key: "design", label: t("sidebar.design"), href: `/p/${projectId}/design`, icon: "design" },
    { key: "architect", label: t("sidebar.architect"), href: `/p/${projectId}/architect`, icon: "architect" },
    { key: "settings", label: t("sidebar.settings"), href: `/p/${projectId}/settings`, icon: "settings" },
  ];
}

function getGlobalNav(t: NavTranslator): NavItem[] {
  // The workspace board leads: every task of this workspace across its projects, the landing
  // page. Shown inside a project too, one click from that project's own board.
  return [
    { key: "workspaceBoard", label: t("sidebar.workspaceBoard"), href: "/board", icon: "board", exact: true },
    { key: "inbox", label: t("sidebar.inbox"), href: "/inbox", icon: "inbox" },
    { key: "notifications", label: t("sidebar.notifications"), href: "/notifications", icon: "bell" },
    { key: "account", label: t("settingsNav.title"), href: "/settings", icon: "settings" },
  ];
}

function getMobileTabs(projectId: string | undefined, t: NavTranslator): MobileTab[] {
  if (!projectId) {
    return [
      { key: "board", label: t("mobileNav.board"), href: "/board", icon: "board", exact: true },
      { key: "alerts", label: t("mobileNav.alerts"), href: "/notifications", icon: "bell" },
      { key: "settings", label: t("mobileNav.settings"), href: "/settings", icon: "settings" },
    ];
  }

  return [
    { key: "home", label: t("mobileNav.home"), href: `/p/${projectId}`, icon: "home", exact: true },
    { key: "board", label: t("mobileNav.board"), href: `/p/${projectId}/board`, icon: "board" },
    {
      key: "new",
      label: t("mobileNav.new"),
      href: `/p/${projectId}/tasks/new`,
      icon: "plus",
      isCreate: true,
      permission: "TasksCreate",
    },
    { key: "alerts", label: t("mobileNav.alerts"), href: "/notifications", icon: "bell" },
    { key: "settings", label: t("mobileNav.settings"), href: `/p/${projectId}/settings`, icon: "settings" },
  ];
}

export { getProjectNav, getGlobalNav, getMobileTabs };
export type { NavIconName, NavItem, MobileTab, NavTranslator };
