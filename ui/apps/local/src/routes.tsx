import { lazy, Suspense, type ComponentType, type ReactNode } from "react";
import { Outlet, type RouteObject } from "react-router";
import { Root, RootRedirect } from "./root";
import LocaleLayout from "@/app/[locale]/layout";
import DashboardLayout from "@/app/[locale]/(dashboard)/layout";
import SettingsLayout from "@/app/[locale]/(dashboard)/settings/layout";
import RootError from "@/app/[locale]/error";
import NotFound from "@/app/[locale]/not-found";
import { BoardSkeleton, DashboardSkeleton, TeamSkeleton } from "@/components/page";

/**
 * The route table. Every URL is `/{locale}/…`; the page modules still live under `app/` in the
 * folder layout their URLs mirror (`[param]` = a path param, `(dashboard)` = a layout that adds
 * no URL segment). Layouts are bundled with the app shell; each page is its own chunk.
 *
 * Loading states: a page's chunk suspends to the nearest boundary — the dashboard skeleton for
 * every dashboard page, the board's and team's own skeletons for those two. Once loaded, a page
 * shows its own skeleton while its data loads (useApiQuery).
 */

type PageModule = { default: ComponentType };

/** A lazily loaded page; `fallback` gives it its own loading boundary. */
function page(load: () => Promise<PageModule>, fallback?: ReactNode): ReactNode {
  const Page = lazy(load);
  return fallback ? (
    <Suspense fallback={fallback}>
      <Page />
    </Suspense>
  ) : (
    <Page />
  );
}

/** A layout module (`{children}`-style) rendering its child routes. */
function layout(Layout: ComponentType<{ children: ReactNode }>, suspense?: ReactNode): ReactNode {
  return (
    <Layout>
      {suspense === undefined ? (
        <Outlet />
      ) : (
        <Suspense fallback={suspense}>
          <Outlet />
        </Suspense>
      )}
    </Layout>
  );
}


/** `/p/:projectId/…` — a project's pages. */
const projectRoutes: RouteObject[] = [
  { index: true, element: page(() => import("@/app/[locale]/(dashboard)/p/[projectId]/page")) },
  { path: "activity", element: page(() => import("@/app/[locale]/(dashboard)/p/[projectId]/activity/page")) },
  { path: "architect", element: page(() => import("@/app/[locale]/(dashboard)/p/[projectId]/architect/page")) },
  { path: "automation", element: page(() => import("@/app/[locale]/(dashboard)/p/[projectId]/automation/page")) },
  {
    path: "board",
    element: page(() => import("@/app/[locale]/(dashboard)/p/[projectId]/board/page"), <BoardSkeleton />),
  },
  { path: "design", element: page(() => import("@/app/[locale]/(dashboard)/p/[projectId]/design/page")) },
  { path: "documents", element: page(() => import("@/app/[locale]/(dashboard)/p/[projectId]/documents/page")) },
  { path: "memory", element: page(() => import("@/app/[locale]/(dashboard)/p/[projectId]/memory/page")) },
  { path: "repos", element: page(() => import("@/app/[locale]/(dashboard)/p/[projectId]/repos/page")) },
  { path: "roadmap", element: page(() => import("@/app/[locale]/(dashboard)/p/[projectId]/roadmap/page")) },
  {
    path: "roadmap/:milestoneId",
    element: page(() => import("@/app/[locale]/(dashboard)/p/[projectId]/roadmap/[milestoneId]/page")),
  },
  { path: "settings", element: page(() => import("@/app/[locale]/(dashboard)/p/[projectId]/settings/page")) },
  { path: "skills", element: page(() => import("@/app/[locale]/(dashboard)/p/[projectId]/skills/page")) },
  { path: "tasks", element: page(() => import("@/app/[locale]/(dashboard)/p/[projectId]/tasks/page")) },
  { path: "tasks/new", element: page(() => import("@/app/[locale]/(dashboard)/p/[projectId]/tasks/new/page")) },
  { path: "tasks/:taskId", element: page(() => import("@/app/[locale]/(dashboard)/p/[projectId]/tasks/[taskId]/page")) },
  {
    path: "tasks/:taskId/merge",
    element: page(() => import("@/app/[locale]/(dashboard)/p/[projectId]/tasks/[taskId]/merge/page")),
  },
  {
    path: "tasks/:taskId/review",
    element: page(() => import("@/app/[locale]/(dashboard)/p/[projectId]/tasks/[taskId]/review/page")),
  },
  {
    path: "team",
    element: page(() => import("@/app/[locale]/(dashboard)/p/[projectId]/team/page"), <TeamSkeleton />),
  },
];

/** `/projects/:projectId/…` — old URLs, each forwarding to its `/p/…` page. */
const oldProjectRoutes: RouteObject[] = [
  { index: true, element: page(() => import("@/app/[locale]/(dashboard)/projects/[projectId]/page")) },
  { path: "board", element: page(() => import("@/app/[locale]/(dashboard)/projects/[projectId]/board/page")) },
  { path: "repos", element: page(() => import("@/app/[locale]/(dashboard)/projects/[projectId]/repos/page")) },
  { path: "settings", element: page(() => import("@/app/[locale]/(dashboard)/projects/[projectId]/settings/page")) },
  {
    path: "tasks/new",
    element: page(() => import("@/app/[locale]/(dashboard)/projects/[projectId]/tasks/new/page")),
  },
  {
    path: "tasks/:taskId",
    element: page(() => import("@/app/[locale]/(dashboard)/projects/[projectId]/tasks/[taskId]/page")),
  },
];

const settingsRoutes: RouteObject[] = [
  { index: true, element: page(() => import("@/app/[locale]/(dashboard)/settings/page")) },
  { path: "api-keys", element: page(() => import("@/app/[locale]/(dashboard)/settings/api-keys/page")) },
  { path: "machine", element: page(() => import("@/app/[locale]/(dashboard)/settings/machine/page")) },
  { path: "notifications", element: page(() => import("@/app/[locale]/(dashboard)/settings/notifications/page")) },
  { path: "preferences", element: page(() => import("@/app/[locale]/(dashboard)/settings/preferences/page")) },
  { path: "trackers", element: page(() => import("@/app/[locale]/(dashboard)/settings/trackers/page")) },
  { path: "usage", element: page(() => import("@/app/[locale]/(dashboard)/settings/usage/page")) },
  { path: "workspace", element: page(() => import("@/app/[locale]/(dashboard)/settings/workspace/page")) },
];

const dashboardRoutes: RouteObject[] = [
  {
    path: "dashboard",
    element: page(() => import("@/app/[locale]/(dashboard)/dashboard/page")),
  },
  { path: "inbox", element: page(() => import("@/app/[locale]/(dashboard)/inbox/page")) },
  {
    path: "notifications",
    element: page(() => import("@/app/[locale]/(dashboard)/notifications/page")),
  },
  { path: "projects", element: page(() => import("@/app/[locale]/(dashboard)/projects/page")) },
  {
    path: "projects/new",
    element: page(() => import("@/app/[locale]/(dashboard)/projects/new/page")),
  },
  { path: "projects/:projectId", children: oldProjectRoutes },
  { path: "p/:projectId", children: projectRoutes },
  { path: "settings", element: layout(SettingsLayout), children: settingsRoutes },
];

export const routes: RouteObject[] = [
  {
    element: <Root />,
    children: [
      { path: "/", element: <RootRedirect /> },
      {
        // A first segment that is not a locale is an unprefixed URL; LocaleLayout redirects it.
        path: "/:locale",
        element: layout(LocaleLayout),
        children: [
          {
            // Inside the locale layout so the error page is translated.
            errorElement: <RootError />,
            children: [
              { index: true, element: page(() => import("@/app/[locale]/page")) },
              {
                element: layout(DashboardLayout, <DashboardSkeleton />),
                children: dashboardRoutes,
              },
              { path: "*", element: <NotFound /> },
            ],
          },
        ],
      },
    ],
  },
];
