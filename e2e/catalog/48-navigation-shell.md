---
area: NAV
title: Navigation shell — sidebar, top bar, mobile nav, tour, redirects, error pages
modes-default: [all]
---

The hosted dashboard shell (`DashboardShell`): `Sidebar` driven by `getProjectNav`/`getGlobalNav` (`packages/core/src/lib/nav-model.ts`), `TopBar`, `MobileDrawer`, `MobileTabBar`, `FloatingDockHost`, `RunDock`, `ProductTour`. Labels come from `dashboard.sidebar`, `dashboard.mobileNav`, `dashboard.topBar`, `dashboard.tour`.

## NAV-001 Show the full sidebar in walk order for a Ready project
```yaml
priority: P0
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]
  - orkka-ui:packages/core/src/lib/nav-model.ts
  - orkka-ui:apps/local/components/layout/Sidebar.tsx
  - i18n:sidebar
```
**Preconditions**: Enterprise org; Ready Unmanaged project; technical details on; desktop viewport (≥1024 px).
**Steps**:
1. Open `/p/{slug}`; read the sidebar items top to bottom.
**Expected**:
- "Project" label + project name (no status pill when Ready).
- Items: Overview, Team, Activity, Skills, Automation, Memory, Roadmap, Board, Tasks, Repos, Documents, Design, Diagrams, Members, Settings.
- Divider, then Inbox, Notifications, Account.
- Each link carries `data-tour="nav-{key}"` (`nav-overview` … `nav-account`).
**Notes**: none.

## NAV-004 Hide the Members item when team features are off
```yaml
priority: P2
surface: [ui]
automation: manual
refs:
  - orkka-ui:/p/[projectId]
  - orkka-ui:packages/core/src/lib/nav-model.ts
```
**Preconditions**: Free-plan org.
**Steps**:
1. Open a Ready project.
**Expected**:
- No "Members" item between "Diagrams" and "Settings"; everything else as NAV-001.
**Notes**: none.

## NAV-006 Collapse and expand the sidebar
```yaml
priority: P2
surface: [ui]
automation: planned
refs:
  - orkka-ui:apps/local/components/layout/Sidebar.tsx
```
**Preconditions**: desktop viewport, any dashboard page.
**Steps**:
1. Click the chevron button at the bottom of the sidebar.
2. Click it again.
**Expected**:
- Collapsed: `<aside>` width 68 px, labels hidden, brand mark only, chevron rotated 180°, project name block hidden; icons remain clickable and navigate.
- Expanded: width 240 px, labels back.
- State is per page load (not persisted).
**Notes**: none.

## NAV-007 Highlight the active nav item correctly
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks
  - orkka-ui:apps/local/components/layout/Sidebar.tsx
```
**Preconditions**: Ready project.
**Steps**:
1. Open `/p/{slug}` → then `/p/{slug}/tasks/new` → then `/p/{slug}/roadmap/{milestoneId}`.
**Expected**:
- `/p/{slug}`: only "Overview" highlighted (exact match).
- `/tasks/new`: "Tasks" highlighted, "Overview" not (prefix match).
- `/roadmap/{id}`: "Roadmap" highlighted.
**Notes**: none.

## NAV-009 Navigate through the global nav
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:/inbox
  - orkka-ui:/notifications
  - orkka-ui:/settings
  - i18n:sidebar
```
**Preconditions**: any project page.
**Steps**:
1. Click "Inbox", then "Notifications", then "Account".
**Expected**:
- URLs `/inbox`, `/notifications`, `/settings`; each item highlighted while active; page headings "Inbox", "Notifications", "Settings".
**Notes**: none.

## NAV-010 Show the project-less sidebar on global pages
```yaml
priority: P2
surface: [ui]
automation: planned
refs:
  - orkka-ui:/inbox
  - i18n:sidebar
```
**Preconditions**: signed in.
**Steps**:
1. Open `/inbox` directly.
2. Click "Back to Project".
**Expected**:
- Sidebar label "Navigation", item "Back to Project" followed by Inbox, Notifications, Account; no project items.
- "Back to Project" → `/dashboard` → redirects to the last project (`orkka-last-project` cookie) or `/onboarding` when none.
**Notes**: none.

## NAV-011 Render the top bar controls
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:apps/local/components/layout/TopBar.tsx
  - orkka-ui:/p/[projectId]/board
  - i18n:topBar
```
**Preconditions**: desktop viewport; Ready project.
**Steps**:
1. Open `/p/{slug}/board`.
**Expected**:
- Left: hamburger hidden at ≥1024 px; `ProjectSwitcher` showing the project name; breadcrumbs "{project name} / Board".
- Right: PlanBadge ("Enterprise"/plan name), CreditsBadge (no execution badge since cloud execution was removed), `select[aria-label="switch language"]`, button `aria-label="Report a bug"`, bell `aria-label="Notifications"`, Clerk `UserButton` avatar.
**Notes**: none.

## NAV-012 Navigate with breadcrumbs
```yaml
priority: P2
surface: [ui]
automation: planned
refs:
  - orkka-ui:apps/local/components/layout/TopBar.tsx
  - orkka-ui:/p/[projectId]/tasks
```
**Preconditions**: task #1 exists.
**Steps**:
1. Open `/p/{slug}/tasks/1`; read the crumbs; click the project-name crumb.
**Expected**:
- Crumbs "{project} / Tasks / 1"; the last is plain text, the others links; project crumb → `/p/{slug}`; "Tasks" → `/p/{slug}/tasks`.
- Below 768 px the breadcrumb nav is hidden (`md:flex`).
**Notes**: none.

## NAV-013 Open the mobile drawer from the hamburger
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:apps/local/components/layout/DashboardShell.tsx
  - i18n:topBar
```
**Preconditions**: viewport 390×844; Ready project.
**Steps**:
1. Click the button `aria-label="Toggle sidebar"`.
2. Click "Board" inside the drawer.
**Expected**:
- Drawer slides in with the full sidebar plus a footer holding CreditsBadge and a full-width LocaleSwitcher (the controls the header drops below md).
- Selecting an item navigates and closes the drawer.
**Notes**: none.

## NAV-014 Render the mobile tab bar with permission-gated New
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:packages/core/src/lib/nav-model.ts
  - orkka-ui:/p/[projectId]/tasks/new
  - i18n:mobileNav
```
**Preconditions**: viewport 390×844; Ready project.
**Steps**:
1. Open `/p/{slug}`; read the bottom bar; tap "New".
2. Open `/inbox` (no project).
**Expected**:
- In project: Home, Board, New (raised FAB), Alerts, Settings; "New" → `/p/{slug}/tasks/new`; active tab tinted primary; "Home" exact-match only.
- Without project: Home (`/dashboard`), Alerts (`/notifications`), Settings (`/settings`).
- A member without TasksCreate does not get the "New" FAB (`RequirePermission`).
**Notes**: none.

## NAV-015 Switch the interface language from the top bar
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PUT /api/org/me/preferences
  - orkka-ui:packages/ui/src/components/ui/LocaleSwitcher.tsx
  - orkka-ui:/p/[projectId]/board
```
**Preconditions**: locale `en`.
**Steps**:
1. Choose `ES` in `select[aria-label="switch language"]`.
2. Reload; choose `EN`.
**Expected**:
- URL prefixed `/es/p/{slug}/board`; sidebar reads "Tablero"/Spanish labels; `PUT /api/org/me/preferences {locale:"es"}` sent; subsequent API calls carry `Accept-Language: es`.
- Reload keeps Spanish; `EN` restores unprefixed URL.
**Notes**: none.

## NAV-017 Stack floating launchers in dock order
```yaml
priority: P3
modes: [hybrid-repo, hybrid-folder]
surface: [ui]
automation: planned
refs:
  - orkka-ui:packages/ui/src/components/ui/FloatingDock.tsx
  - orkka-ui:/p/[projectId]/documents
```
**Preconditions**: runner online with `app-run`; project with a repo containing `orkka.json`.
**Steps**:
1. Open `/p/{slug}/documents`.
**Expected**:
- Bottom-right column shows the Run bubble (order 20) above the agent-chat launcher (order 40) — never overlapping; on `/board` only the Run bubble remains.
**Notes**: none.

## NAV-018 Walk the product tour and skip it
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PUT /api/org/me/preferences
  - orkka-ui:packages/ui/src/components/tour/ProductTour.tsx
  - orkka-ui:packages/ui/src/components/tour/tour-steps.ts
  - i18n:tour
```
**Preconditions**: `onboardingTourSeen:false` (set via `PUT /api/org/me/preferences`); Ready Unmanaged project; Enterprise (members step present); desktop.
**Steps**:
1. Open `/p/{slug}`.
2. Click "Next" three times; reload the page.
3. Click "Back"; then "Skip tour".
4. `GET /api/org/me`.
**Expected**:
- `data-testid="product-tour"` card "Welcome to Orkka" with progress "1 of 19", buttons "Skip tour", "Next".
- Steps 2–4 spotlight `nav-overview`, `nav-team`, `nav-activity` and navigate to each page; progress increments.
- Reload resumes at the same step (`sessionStorage["orkka.tour.step"]`).
- "Back" goes one step back; "Skip tour" closes it and `PUT {onboardingTourSeen:true}` is sent; step 4 → `preferences.onboardingTourSeen:true`; reload shows no tour.
**Notes**: none.

## NAV-019 Finish the tour on the last step
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PUT /api/org/me/preferences
  - orkka-ui:/settings
  - i18n:tour
```
**Preconditions**: NAV-018 reset (`onboardingTourSeen:false`).
**Steps**:
1. Click "Next" until the last step ("Your account", anchored `nav-account`, page `/settings`).
2. Click "Finish".
**Expected**:
- Last step shows "19 of 19" and the button reads "Finish" instead of "Next".
- Finish closes the tour, persists `onboardingTourSeen:true`, clears `orkka.tour.step`.
**Notes**: none.

## NAV-021 Default to the Board view and switch to Code
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/board
```
**Preconditions**: Ready project.
**Steps**:
1. Open `/p/{slug}/board`; read the `role="group"` switch.
2. Click "Code"; then "List".
**Expected**:
- Default: "Board" is `<span aria-current="page">`, kanban lanes rendered.
- "Code" → `?view=code`, code workbench, `aria-current` moves; "List" → `/p/{slug}/tasks`.
- Reloading `/board` without the query returns to Board (Board is the default since 2026-09-04).
**Notes**: none.

## NAV-022 Redirect legacy /projects URLs
```yaml
priority: P2
surface: [ui]
automation: planned
refs:
  - orkka-ui:/projects
  - orkka-ui:/projects/[projectId]
  - orkka-ui:/projects/[projectId]/board
  - orkka-ui:/projects/[projectId]/repos
  - orkka-ui:/projects/[projectId]/tasks/new
  - orkka-ui:/projects/[projectId]/tasks/[taskId]
  - orkka-ui:/projects/[projectId]/settings
```
**Preconditions**: project id/slug `S`; task #1.
**Steps**:
1. Open `/projects`, `/projects/S`, `/projects/S/board`, `/projects/S/repos`, `/projects/S/settings`, `/projects/S/tasks/new`, `/projects/S/tasks/1`.
**Expected**:
- `/projects` → `/dashboard`; each other URL → the matching `/p/S/…` page (task detail canonicalizes to `/p/S/tasks/1`).
- A task ULID under `/p/S/tasks/{ulid}` also server-redirects to `/tasks/{number}`.
**Notes**: none.

## NAV-023 Show the 404 page
```yaml
priority: P2
surface: [ui]
automation: planned
refs:
  - orkka-ui:apps/local/app/[locale]/not-found.tsx
  - i18n:notFound
```
**Preconditions**: signed in.
**Steps**:
1. Open `/this-route-does-not-exist-{stamp}`.
2. Click "Back to Home".
**Expected**:
- HTTP 404; orca mark, big "404", "Page not found", "The page you're looking for doesn't exist or has been moved.".
- Button → `/` (marketing root; signed-in users bounce to `/dashboard`).
**Notes**: none.

## NAV-024 Show the error boundary page
```yaml
priority: P3
surface: [ui]
automation: manual
refs:
  - orkka-ui:apps/local/app/[locale]/error.tsx
  - i18n:errorPage
```
**Preconditions**: a way to make a route render throw (e.g. API returning malformed JSON for the project page via a local proxy).
**Steps**:
1. Trigger the render error; click "Try Again".
**Expected**:
- Orca error animation, "Something went wrong", the error message or "An unexpected error occurred. Please try again.", "Error ID: {digest}" when present, button "Try Again" re-renders the segment.
**Notes**: none.

## NAV-025 Observe stale data on back-navigation within the 30 s router cache
```yaml
priority: P3
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/settings
  - orkka-ui:/p/[projectId]
```
**Preconditions**: project `U`.
**Steps**:
1. Open `/p/{slug}` then `/p/{slug}/settings`; rename the project (SET-013).
2. Press browser Back immediately; then hard-reload.
**Expected**:
- Documented behaviour: the overview may show the OLD name for up to 30 s (`staleTimes.dynamic: 30`); after reload (or 30 s) it shows the new one. Test asserts the reload state and tolerates the stale window.
**Notes**: none.

## NAV-026 Route /dashboard to the right place
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:/
  - orkka-ui:/dashboard
  - i18n:firstRun
since: 2026-09-15
```
**Preconditions**: Owner with ≥ 1 project; cookie `orkka-last-project` set by a previous visit.
**Steps**:
1. Open `/dashboard`.
2. Clear the cookie; open `/dashboard`.
3. (Fresh workspace) open `/dashboard` with zero projects.
4. Open `/` and `/en`.
**Expected**:
- Step 1 → `/p/{lastSlug}`; step 2 → `/p/{firstSlug}` (there is no brief or setup stage any more).
- Step 3: stays on `/dashboard` with "No projects yet" / "Point Orkka at the folder that holds your code — one repository or several. Its AI team works them on this machine." and "Create a project" (→ `/projects/new`).
- Step 4: both land on `/en/dashboard` (the root has no landing page; an unprefixed path is redirected to its locale first).

## NAV-027 Project overview page
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]
  - i18n:projectOverview
since: 2026-10-26
```
**Preconditions**: A project with two tasks (one in review, one Done) and no description.
**Steps**:
1. Open `/p/{slug}`.
**Expected**:
- "Here's what's happening with this project."; stat cards "Active Tasks", "In Review" (1), "Completed" (1), "Active Agents"; "Recent Tasks" with "View Board"; Quick Actions "New Task", "Manage Repos", "Project Settings"; a project without tasks shows "No tasks yet. Create one to get started."
