# FRONTEND.md — Orkka Web App

## Overview

A static single-page app in `ui/` (npm workspaces): Vite + React Router + use-intl, built to `ui/apps/local/dist` and served by Orkka.Local on :6750 — no Node at runtime. Responsive. Every page loads its data in the browser (see Core Rules). Local session auth (no accounts). SignalR for real-time updates. Tailwind CSS v4. Five locales (en, de, es, fr, pt).

---

## Project Structure

```
ui/
├── eslint.config.mjs                   # one lint config for the app and the packages
├── apps/local/                         # the app (@orkka/local-ui)
│   ├── index.html, src/                # main.tsx (router, fonts), routes.tsx (THE route table), root.tsx
│   ├── vite.config.ts                  # + vite.aliases.ts (@/*), vite.excalidraw-fonts.ts; vitest.config.mts
│   ├── app/                            # page and layout modules, in their URL's folder layout
│   │   └── [locale]/                   # every URL carries a locale prefix (/en/…)
│   │       ├── layout.tsx, error.tsx, not-found.tsx
│   │       ├── page.tsx                # → /board
│   │       └── (dashboard)/            # route group: the signed-in shell (sidebar, providers;
│   │           │                       #   loads workspaces + api.org.me in the browser)
│   │           ├── board/              # the workspace board: every project's tasks (?project= narrows)
│   │           ├── dashboard/          # empty state → /projects/new, else → /p/{last or first project}
│   │           ├── inbox/              # what waits on you
│   │           ├── notifications/
│   │           ├── projects/           # /projects → /dashboard; /projects/new = create (from a folder)
│   │           │   └── [projectId]/…   # old URLs: redirect to /p/…
│   │           ├── p/[projectId]/      # a project (layout.tsx)
│   │           │   ├── page.tsx        # overview
│   │           │   ├── board/  tasks/ (new, [taskId], [taskId]/review, [taskId]/merge)
│   │           │   ├── roadmap/ (+ [milestoneId])  repos/  team/  architect/
│   │           │   ├── activity/  automation/  memory/  skills/  design/  documents/
│   │           │   └── settings/       # pipeline + project settings, ticket source
│   │           └── settings/           # /settings → /settings/workspace
│   │               ├── workspace/  preferences/  notifications/
│   │               ├── machine/        # the runner, AI CLIs, forge CLIs, pending command approvals
│   │               ├── trackers/       # Jira / Azure DevOps connections
│   │               ├── usage/          # token usage and estimated list-price spend (/api/usage/*)
│   │               └── api-keys/       # orkka_key_… for MCP and scripts
│   ├── components/                     # app-only: layout, page (skeletons, errors, redirects), projects,
│   │                                   #   providers, repos, run, runner, settings, workspace
│   ├── hooks/                          # use-api-query (page data), use-document-title, use-client-api,
│   │                                   #   use-runner-status, use-selectable-models
│   ├── i18n/                           # routing (locales, preferredLocale), messages, navigation (Link, useRouter…)
│   ├── lib/                            # actions (browser mutations), navigation (params, query), local-auth-client, task-route, nav
│   └── __tests__/                      # vitest (components, hooks, pages, routing, i18n parity)
└── packages/
    ├── core/src/                       # @orkka/core — lib/api.ts (typed API client), lib/signalr.ts,
    │                                   #   lib/types.ts, constants, hooks shared by every page
    ├── ui/src/                         # @orkka/ui — components by area (board, task, chat, pr, merge,
    │                                   #   project, repos, roadmap, team, settings, …) + styles/theme.css
    └── i18n/messages/{en,de,es,fr,pt}/ # common.json, dashboard.json — the five locales move in lockstep
```

There is no audit-log page (the API's `GET /api/audit` has no UI) and no members, billing or
account pages — the cloud product is gone.

---

## Routing, i18n and navigation

The app is a static single-page app: **Vite** builds it, **React Router** (`createBrowserRouter`)
routes it, **use-intl** translates it. `src/main.tsx` mounts the router; `src/routes.tsx` is the
route table — every URL the app answers, in one file:

```
/                                  → /{preferred locale}            (src/root.tsx RootRedirect)
/:locale                           app/[locale]/layout.tsx — catalogs, IntlProvider, NEXT_LOCALE cookie,
│                                  <html lang>, default title; a non-locale first segment = an
│                                  unprefixed URL → redirected to /{preferred locale}/…
├── (index)                        app/[locale]/page.tsx → /board
├── (errorElement)                 app/[locale]/error.tsx (stale chunk after a rebuild → full reload)
├── (dashboard layout)             app/[locale]/(dashboard)/layout.tsx, Suspense → DashboardSkeleton
│   ├── board (BoardSkeleton), dashboard, inbox, notifications, projects, projects/new
│   ├── p/:projectId/…             overview, board (BoardSkeleton), tasks, tasks/new, tasks/:taskId
│   │                              (+ merge, review), roadmap (+ :milestoneId), repos, team
│   │                              (TeamSkeleton), architect, activity, automation, memory, skills,
│   │                              design, documents, settings
│   ├── projects/:projectId/…      old URLs → ClientRedirect to /p/…
│   └── settings/…                 settings layout: workspace (name, AI provider —
│                                  WorkspaceAiProviderCard, git identity), machine, trackers,
│                                  usage, api-keys, preferences, notifications
└── *                              app/[locale]/not-found.tsx (translated 404)
```

- **Page modules stay in `app/`**, in the folder layout their URLs mirror (`[param]` = a path
  param, `(dashboard)` = a layout that adds no URL segment). They are plain modules now: a page
  is a default-exported component, a layout takes `{ children }`; the route table wires them.
  A new page = a module + one line in `src/routes.tsx` (and a case in
  `__tests__/routing/routes.test.tsx`).
- **Code splitting:** every page is its own lazy chunk; layouts ship with the shell. A page's
  chunk suspends to the nearest boundary (the dashboard skeleton, or the board's/team's own).
- **Locales** (`i18n/routing.ts`): `en` (default), `es`, `pt`, `fr`, `de`, always in the URL.
  `preferredLocale()` = the `NEXT_LOCALE` cookie, else `navigator.languages`, else `en` — the same
  order the host applies with `Accept-Language`. The locale layout rewrites the cookie on every
  visit (the name is next-intl's, kept so browsers keep their choice; `getClientLocale` in
  packages/core reads it too). Catalogs load per locale (`i18n/messages.ts`,
  `import.meta.glob`, one chunk per JSON file).
- **Imports are explicit modules, no package aliasing:**
  - translations: `useTranslations`, `useLocale`, `useFormatter` from `use-intl` (the library
    next-intl's client half was built on);
  - `@/i18n/navigation` — locale-aware: `Link` (`href` without the locale; `locale` prop to
    switch), `useRouter()` (`push`/`replace(href, { locale?, scroll? })`, `back`, `forward`),
    `usePathname()` (without the locale), `localizeHref`, `stripLocale`;
  - `@/lib/navigation` — `useParams<T>()`, `useSearchParams()` (read-only) and
    `useUpdateSearchParams()` for `?task=`/`?file=`/`?d=` style changes that keep the page
    mounted. Never call `history.pushState` directly: the router would not see it.
  - `React.lazy` + `Suspense` for client-heavy widgets (Excalidraw, xterm).
- **Titles:** `useDocumentTitle(t("meta.…"))` sets `"<title> | Orkka"` while the page is
  mounted; the locale layout sets the localized default.
- **Fonts** are self-hosted: `@fontsource-variable/*` imported in `src/main.tsx`, exposed under the
  `--font-*` variables `theme.css` reads (`app/globals.css`). Excalidraw's fonts are served from
  `/excalidraw/fonts/` (`vite.excalidraw-fonts.ts`), never its CDN.
- **Tailwind v4** runs through `@tailwindcss/vite`; `app/globals.css` imports the theme and
  registers the shared packages with `@source`.
- **The `@/*` alias** resolves app → `packages/ui/src` → `packages/core/src`, first file wins
  (`vite.aliases.ts`, shared by Vite and Vitest; `tsconfig.json` `paths` say the same).

## Build, dev server and the host contract

- `npm run build` → `apps/local/dist/`: `index.html` (no inline script or style) + hashed
  `assets/`, `excalidraw/fonts/`, and `public/`. Nothing runs at runtime but the host.
- The host serves it: static files (`/assets/*` immutable), a **302** from `/` and unprefixed
  paths to `/{NEXT_LOCALE cookie | Accept-Language | en}/…`, `index.html` for every other GET
  (the SPA renders the page or its 404), a missing `/assets/*` file as a plain 404, the session
  gate on `index.html` only, and a strict CSP (`script-src 'self'`; `style-src` needs
  `'unsafe-inline'` for shiki/xterm/Excalidraw). The exact rules and headers are in
  `apps/local/README.md` → "How it is served".
- `npm run dev` (Vite, :5173) proxies `/api`, `/hubs`, `/blobs`, `/mcp`, `/launch`, `/health` to a
  running host on :6750 with `Host`/`Origin` rewritten for its request guard. A convenience for
  UI work; the shipped path is `dist/` behind the host.
- The bundle reads no environment variables: API, hubs and SSE are always same-origin.

---

## Core Rules

### Data lives in the browser
There is no UI server: the host serves static files, and everything — data, mutations,
redirects, titles — happens in the browser. Nothing may come to depend on a server render.

A page reads its route with `useParams()` / `useSearchParams()` and loads with `useApiQuery`
(`apps/local/hooks/use-api-query.ts`), calling the API with an empty token — the browser's
`orkka_s` session cookie authenticates:

```tsx
export default function ReposPage() {
  const { projectId } = useParams<{ projectId: string }>();
  const t = useTranslations("dashboard");
  useDocumentTitle(t("meta.repos"));

  const { data, error, reload } = useApiQuery(async () => {
    const [repos, project] = await Promise.all([
      api.repos.list(projectId, ""),
      api.projects.get(projectId, "").catch(() => null),     // optional parts degrade inside load
    ]);
    return { repos, project };
  }, [projectId]);                                         // the key: a new key loads again

  if (error) return <PageError error={error} onRetry={reload} />;
  if (!data) return <DashboardSkeleton />;                 // or BoardSkeleton / TeamSkeleton
  return <RepoFileBrowser projectId={projectId} repos={data.repos} />;
}
```

- **Loading / error / not found** come from `apps/local/components/page`: the skeletons
  (`DashboardSkeleton`, `BoardSkeleton`, `TeamSkeleton`, `ShellLoading`; all carry
  `data-testid="page-loading"`), `PageError` (the error-boundary visuals with a retry),
  `NotFoundView` (the 404 page) and `TaskNotFound`.
- **Redirects** are client navigations: `ClientRedirect` for redirect-only routes, and
  `router.replace(…)` from `@/i18n/navigation` (it adds the locale prefix) once data says where
  to go — the dashboard's last-project jump, legacy task ULIDs → the task number.
- **Titles:** `useDocumentTitle(t("meta.…"))` sets `"<title> | Orkka"`; the locale layout sets
  the localized default.

### Mutations and refresh: `refreshData()`
Writes go straight to the API from the browser — `apps/local/lib/actions.ts` or `useClientApi()` / `api.*` in a component. Showing the
result is **one** mechanism, `refreshData()` from `@/lib/data-refresh` (packages/core):

- it drops the API client's GET cache and bumps a data version;
- every mounted `useApiQuery` subscribes to that version and refetches, keeping its current data
  on screen until the new response lands (no skeleton flash; a failed refetch keeps the old data);
- so the page's props change in place — components that sync state from props keep working.

Every write in `lib/actions.ts` calls it; anything else that changes server state the page
shows (a chat turn that created documents, a linked repo, a renamed workspace) calls it too.
Actions never navigate: one that leads elsewhere returns the target path (`deleteTask`, `deleteProject`,
`startInteractiveSession`) and the caller pushes it with the i18n router. Expected failures are
returned as `{ error }` so the API's reason reaches the user.

### Realtime
SignalR (`useBoardSignalR`, `useTaskSignalR`, …) still hydrates live data on top of what a page
loaded; the board and task detail keep their own subscriptions. Hub handlers are per connection,
not per group: one `useBoardSignalR` call joins one project (`projectId`) or several
(`projectIds`) with ONE set of handlers — never call it once per project — and with no project it
joins and listens to nothing.

### Workspace board
`/{locale}/board` (`app/[locale]/(dashboard)/board/page.tsx`) is the landing page: `/`, a workspace
switch and a new workspace all go there; `/dashboard` still opens the last project. It shows every
task of the workspace across its projects through the project board's own `TaskBoard`, given a
`workspace` scope (`{ project, projectUlids }`):
- **Data:** `api.board.workspace(project)` (`GET /api/board`) and `api.board.searchWorkspace`;
  every card carries `project` (`BoardProjectRef`). A created task refetches the board (the hub
  event names no project); every other event patches the lanes as on a project board.
- **Filter:** `?project=<slug>` (a select: "All projects" + each project); an unknown value shows
  every project with a notice. Narrowed, cards lose their project badge, the page links to the
  project's own board, and only that project's group is joined.
- **Cards:** badged with the project name when every project shows; `?task=` takes the task
  CODE (numbers repeat across projects); the detail modal loads the card's own project. A card
  never drops into Testing when its project has none (`project.hasTesting`, `LaneColumn`
  `activeDragAllowsTesting`). No metrics strip or lane summaries — they are per project
  (`useBoardMetrics({ enabled: false })`).
- **New task:** narrowed (or a single project) → straight to that project's `tasks/new`;
  otherwise a menu asks which project.
- Nav: the global nav leads with "Workspace board" (sidebar, mobile tab bar, product tour step
  `workspaceBoard`); the project board links back to it.

---

## Design System

### Color Palette

```css
:root {
  /* Primary — Dark Blue */
  --primary-950: #0a1628;        /* Deepest background */
  --primary-900: #0f2140;        /* Sidebar, cards on dark */
  --primary-800: #15306b;        /* Hover states, active elements */
  --primary-700: #1e40af;        /* Primary buttons, links */
  --primary-600: #2563eb;        /* Primary accent */
  --primary-500: #3b82f6;        /* Highlights, focus rings */

  /* Neutral — Gray */
  --gray-950: #0b0f18;           /* Page background (dark mode) */
  --gray-900: #111827;           /* Card backgrounds */
  --gray-800: #1f2937;           /* Borders, dividers */
  --gray-700: #374151;           /* Secondary text */
  --gray-600: #4b5563;           /* Muted text */
  --gray-400: #9ca3af;           /* Placeholder text */
  --gray-200: #e5e7eb;           /* Light borders */
  --gray-100: #f3f4f6;           /* Light backgrounds */
  --gray-50: #f9fafb;            /* Page background (light mode) */

  /* White */
  --white: #ffffff;

  /* Semantic — Lane Colors */
  --lane-drafting: #6b7280;      /* Gray */
  --lane-ready: #3b82f6;         /* Blue */
  --lane-planning: #8b5cf6;      /* Purple */
  --lane-plan-ready: #a78bfa;    /* Light purple */
  --lane-questions: #f59e0b;     /* Amber */
  --lane-developing: #10b981;    /* Emerald */
  --lane-review: #06b6d4;        /* Cyan */
  --lane-done: #22c55e;          /* Green */

  /* Status */
  --success: #22c55e;
  --warning: #f59e0b;
  --error: #ef4444;
  --info: #3b82f6;
}
```

### Typography

```css
/* Display/Headings: Bold, modern, distinctive */
font-family: 'Plus Jakarta Sans', sans-serif;

/* Body/UI: Clean, readable */
font-family: 'DM Sans', sans-serif;

/* Code/Monospace: For agent output, logs, code snippets */
font-family: 'JetBrains Mono', monospace;
```

Import via Google Fonts in `app/layout.tsx` using `next/font/google`.

### Tailwind v4 theme

There is no `tailwind.config.ts`: the tokens live in `packages/ui/src/styles/theme.css` as an
`@theme` block (`--color-primary-*`, the lane and status colours, `--font-display`, `--font-body`,
`--font-mono`; the `font-brand` face is for the word "Orkka" only). Fonts load through
`next/font/google` in the root layout. The design-system ESLint rules
(`packages/ui/src/eslint-design-system.mjs`) keep components on the tokens.

### Dark Mode Default
Orkka is a developer tool. Default to dark mode. Support light mode toggle but design dark-first.

---

## Mobile-First Design

Every component designed for 320px+ first, then enhanced for tablet/desktop.

### Key Mobile UX Patterns

**Task creation (the most critical mobile flow):**
- Full-screen modal on mobile, side panel on desktop
- Large touch targets (min 44px)
- Camera button prominent for screenshot attachment
- Repo selector as bottom sheet, not dropdown
- Submit must be reachable with one thumb

**Kanban board on mobile:**
- Horizontal swipe between lanes (not all lanes visible)
- Current lane name as sticky header
- Pull-to-refresh
- Tap card to expand, long-press to drag between lanes

**Conversation thread:**
- Chat-bubble layout (agent messages left, human messages right)
- Streaming agent text appears token-by-token (SignalR)
- Quick-reply bar pinned to bottom
- Image attachments expandable

**Notifications:**
- Badge count on mobile nav
- Tap notification → deep link to exact task + scroll to relevant message

### Responsive Breakpoints
```
sm:  640px   (large phones landscape)
md:  768px   (tablets)
lg:  1024px  (small laptops)
xl:  1280px  (desktops)
2xl: 1536px  (large screens)
```

---

## SignalR Client Integration

`packages/core/src/lib/signalr.ts` owns the connection to the host's `/hubs/tasks` (through the
same origin, :6750; the workspace rides as `?workspace=`), with automatic reconnect. The
providers in `apps/local/components/providers` wrap the signed-in shell, and components
subscribe to project or task groups through the shared hooks — the one sanctioned client-side
effect pattern. The run dock uses `/hubs/apprun` the same way.

---

## Auth — local session

There is no sign-in page. `./orkka open` asks the host for a one-time launch code (`POST /local/launch-codes`) and opens `/launch?code=…`, which sets the `orkka_s` session cookie (httpOnly, SameSite=Strict) and redirects to `/`. Browser writes send `X-Orkka-Csrf: 1` (the host checks `Origin` too). Every API call comes from the browser with that cookie and the active workspace in `X-Orkka-Workspace` (from the `orkka_ws` cookie); there is no UI server to make any. See CLAUDE.md → Hosts → Auth.

---

## Testing

- **vitest** in `apps/local/__tests__` (components with React Testing Library, hooks, and the
  i18n parity tests that keep the five locales in lockstep): `npm test` at the root.
- `npm run typecheck` and `npm run lint` (one flat config, `ui/eslint.config.mjs`:
  typescript-eslint, react-hooks incl. the React Compiler rules, react-refresh on route modules,
  the design-system rules) — app and packages together, clean.
- **Playwright MCP** is how Orkka's own QA agent tests a customer's app on the developer's
  machine; this repo has no Playwright suite of its own.

---

## API Client (Typed)

`packages/core/src/lib/api.ts` is the typed client for the host's `/api/*`, with the shapes in
`lib/types.ts`. It runs in the browser only: pages (`useApiQuery`), actions and components call
it with an empty token, so the session cookie authenticates, and it sends `X-Orkka-Csrf: 1`, the
workspace header (`orkka_ws` cookie) and the URL's locale as `Accept-Language`. GETs are
de-duplicated in flight and optionally TTL-cached; any write, or `refreshData()`, clears the
cache. Paths are the host's own (e.g. `/api/projects/{id}/tasks`,
`/api/tasks/{id}/messages`, `/api/usage/summary`, `/api/me/runner`).

---

## Key Dependencies

`vite` 6, `react` 19 (pinned for every dependency with root `overrides`), `react-router` 7,
`use-intl` 4, `@microsoft/signalr` 10, `tailwindcss` 4 (`@tailwindcss/vite`), `vitest` 4,
`@fontsource-variable/*` (see `ui/apps/local/package.json`).
