# FRONTEND.md — Orkka Local UI

## Overview

A static single-page app (Vite + React Router + use-intl), built to `apps/local/dist` and served by the Orkka host on the developer's own machine — no Node at runtime. Mobile-first. Every page loads its data in the browser (see Core Rules). No accounts and no Clerk: the local host authenticates (see **Auth** below). SignalR for real-time updates. Tailwind CSS v4. There is no landing page, marketing site or SEO surface — `/` goes straight to the workspace board (every task of the workspace, across its projects).

---

## Project Structure

```
apps/local/
├── index.html                          # The one HTML page: meta (noindex, theme colour), #root, /src/main.tsx
├── src/
│   ├── main.tsx                        # Fonts, globals.css, createBrowserRouter(routes)
│   ├── routes.tsx                      # THE route table (see Routing below)
│   └── root.tsx                        # Root (scroll restoration) and the `/` redirect
├── app/                                # Page and layout modules, in their URL's folder layout
│   ├── globals.css                     # Tailwind + theme + font variables
│   └── [locale]/
│       ├── layout.tsx                  # Catalogs + IntlProvider, NEXT_LOCALE cookie, unprefixed-URL redirect
│       ├── error.tsx, not-found.tsx    # Route error boundary, 404
│       ├── page.tsx                    # Client redirect to /board
│       └── (dashboard)/
│           ├── layout.tsx              # Client: loads workspaces + org.me → permissions, preferences, SignalR
│           ├── board/page.tsx          # The workspace board: every project's tasks, ?project= narrows
│           ├── dashboard/page.tsx      # Last project, or the first-run "Create a project" state
│           ├── projects/new/page.tsx   # Create a project from a base folder (discovery preview) → its setup
│           ├── p/[projectId]/          # Overview, board, tasks, repos, team, memory, skills,
│           │                           # automation, roadmap, documents, design, architect, settings,
│           │                           # setup (the guided setup, components/project-setup/, lib/project-setup.ts)
│           ├── inbox/ notifications/
│           └── settings/               # workspace, machine, trackers, usage (tokens + list-price estimate), api-keys ("MCP keys"),
│                                       # preferences, notifications
├── components/                         # App-local components (layout, page states, providers, run dock…)
├── hooks/use-api-query.ts              # Page data: load in the browser, refetch on refreshData()
├── hooks/use-document-title.ts         # "<title> | Orkka" while a page is mounted
├── hooks/use-client-api.ts             # Browser API access (cookie auth, empty token)
├── i18n/                               # routing.ts (locales, preferredLocale), messages.ts, navigation.tsx
├── lib/
│   ├── actions.ts                      # Mutations, run in the browser; refreshData() after each write
│   ├── navigation.ts                   # useParams, useSearchParams, useUpdateSearchParams
│   ├── task-route.ts                   # task route segment (number or legacy ULID) → task
│   └── local-auth-client.ts            # client useAuth() shim → empty token
├── vite.config.ts                      # plugins, dev proxy; vite.aliases.ts (@/*), vite.excalidraw-fonts.ts
└── vitest.config.mts                   # same @/* resolver
```

Shared code lives in `packages/ui` (components) and `packages/core` (API client, types, hooks), reached through the `@/*` fallback chain.

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
│   └── settings/…                 settings layout: workspace, machine, trackers, usage, api-keys,
│                                  preferences, notifications
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
`/{locale}/board` is the landing page (`/`, a workspace switch and a new workspace go there;
`/dashboard` still opens the last project): every task of the workspace across its projects, in
the project board's own `TaskBoard` given a `workspace` scope (`{ project, projectUlids }`).
`api.board.workspace` / `searchWorkspace` (`GET /api/board[/search]`) return cards that carry
their `project`; `?project=<slug>` narrows the board (an unknown value shows every project with a
notice); cards are badged with their project when every project shows, `?task=` takes the task
CODE, the detail modal loads the card's own project, a card never drops into a Testing lane its
project lacks, and there is no metrics strip (per project). "New task" goes straight to the
narrowed (or only) project, otherwise a menu asks which project. The global nav leads with
"Workspace board"; the project board links to it. Full notes: the repository root's
`.claude/docs/FRONTEND.md` → Workspace board.

---

## Design System

**Everything below lives in one file: `packages/ui/src/styles/theme.css`.** Each app's
`globals.css` is three lines — `@import "tailwindcss"`, `@import` that theme, and its
`@source` globs. Do not redefine a token in an app; change it in the theme and every
surface moves together. The file used to be copy-pasted per app and had drifted in both
directions before this was consolidated.

The rules below are enforced by `packages/ui/src/eslint-design-system.mjs`, wired into
`ui/eslint.config.mjs`. A rule firing means the contract should change, not that
the line needs a disable comment.

### Typography — four faces, one job each

| Utility | Face | Used for |
|---|---|---|
| `font-brand` | Unbounded | The word "Orkka". Nothing else, ever — and you should not be typing it by hand; use `<BrandLockup>`. |
| `font-display` | Plus Jakarta Sans | Headings, buttons, labels, nav, tabs. |
| `font-body` | DM Sans | Body copy, descriptions, form values. This is the `<body>` default, so the common case needs no class. |
| `font-mono` | JetBrains Mono | Code, diffs, terminal and log output, commit SHAs, IDs, shell commands, file paths. **Not** section headings, badges, timestamps or counts. |

`font-sans` and `font-serif` are banned — they resolve to a system stack, not the brand.

### Type scale

Nothing in the product renders below **12px**. Arbitrary sizes (`text-[10px]`) are banned.

| Utility | Size | Used for |
|---|---|---|
| `text-label` | 12px, uppercase, 600, tracked | Micro-labels: section captions, badge text, table column heads. One class instead of the four-class incantation it replaced. |
| `text-section` | 14px, uppercase, 600, tracked | Card and page section headings. |
| `text-xs` → `text-7xl` | 12px → | The normal Tailwind scale. |

### Contrast floor

Measured against `--color-surface-app` (`#0b0f18`):

| Token | Ratio | Allowed for |
|---|---|---|
| `text-gray-100` | 16.3:1 | Primary text |
| `text-gray-300` | 12.0:1 | Secondary text |
| `text-gray-400` | 7.2:1 | **Muted text — the lightest colour allowed for anything the user reads** |
| `text-gray-500` | 4.0:1 | Placeholders, list markers, disabled states only |
| `text-gray-600` | 2.5:1 | Borders and dividers. Never text. |

### Colour palette

Seven families, plus tokens. Hard-coded hex in a class (`bg-[#0a0a12]`) is banned.

- **`primary-*`** — the brand blue. Note `blue-*` is byte-identical, so it is a second name for the same colour and is banned.
- **`gray-*`** — neutrals.
- **`emerald` / `amber` / `red`** — success, warning, error. The `--color-success` token is emerald-500 to match.
- **`cyan` / `violet`** — the two secondary accents, also `--color-accent-cyan` / `--color-accent-violet`.
- Banned, with their mapping: `sky`/`teal`→cyan, `fuchsia`/`purple`/`pink`→violet, `orange`/`yellow`→amber, `rose`→red, `green`→emerald, `slate`/`zinc`/`neutral`/`stone`→gray.

**Surfaces** — one ladder, not eight ad-hoc near-blacks:

| Token | Value | Used for |
|---|---|---|
| `surface-app` | `#0b0f18` | Page background (== `gray-950`; also the `themeColor`) |
| `surface-raised` | `#111827` | Cards, panels, sidebar |
| `surface-sunken` | `#070b16` | Terminal, run dock, code wells, the OG card |
| `surface-overlay` | `#0f1420` | Modals, popovers, file trees |

**Lane colours** are `--color-lane-*`, consumed through `LANES` in `packages/core/src/lib/constants.ts`.

**The agent identity palette** is the one sanctioned exception to the seven families:
fourteen agents must be distinguishable at avatar size. It is defined once, in
`AGENT_ROLES` in `packages/core/src/lib/constants.ts`. Surfaces that name agents
("Luna", "Pixel") resolve colour via `agentColorByName()` — never a local hex map.

### The brand

- The wordmark is **`<BrandLockup>`** from `@/components/ui`, never a hand-rolled
  `<span className="font-brand">`. It owns the mark, the gap, the type size and the
  edition pill, across `sm`/`md`/`lg`/`xl`.
- `<BrandMark>` renders the artwork alone: `icon`, `orca`, `lettering`, `full`.
- `orkka-logo-lettering.svg` and `orkka-logo-full.svg` carry the wordmark as **outlined
  Unbounded paths**. They must stay outlined — they load through `<img>`, which cannot
  reach a webfont, so live `<text>` silently falls back to a system face and draws the
  brand in a different typeface on every OS. That is exactly what they used to do.
- The orca is the product's status language, not decoration: `<OrcaStatus lane={…}>`
  dives while an agent works, floats while it waits on you, and casts about while it
  tests. Page-scale states are `OrcaLoading` / `OrcaEmpty` / `OrcaError` / `OrcaSuccess`.

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

`packages/core/src/lib/signalr.ts` owns the one tasks-hub connection. The hub URL is relative (`/hubs/tasks`, same origin), and the local UI passes no token factory: `LocalSignalRProvider` mounts the shared `SignalRProvider` without one, so the connection authenticates with the host's session cookie (`withCredentials`) instead of `accessTokenFactory`.

---

## Auth — local, no accounts

The UI runs behind the **Orkka.Local** host:

- The browser only ever talks to the host at `http://127.0.0.1:6750`, which serves this app's `dist/` (see **Build, dev server and the host contract**).
- **Browser:** the host sets an httpOnly session cookie (`orkka_s`) from a one-time launch code (`GET /launch?code=…`, minted by `./orkka start`/`open`). Client-side fetches and SignalR are same-origin with `credentials: "same-origin"`, send **no** Authorization header, and always send `X-Orkka-Csrf: 1`. `useClientApi()` hands calls an empty token; `doFetch` omits Authorization when the token is empty.
- **There is no UI server.** Pages, layouts and mutations all run in the browser (Core Rules); there is no server-side auth and no secret in the bundle.
- Workspaces are unchanged: the `X-Orkka-Workspace` header / `orkka_ws` cookie pick the job.

```tsx
const { data: projects } = useApiQuery(() => api.projects.list(""), []); // "" = cookie auth
```

Running from source: `npm run build` and let the host serve `dist/`, or `npm run dev` against a running host (apps/local/README.md → Development).

---

## Playwright MCP (Testing)

Use Playwright MCP for end-to-end testing of critical flows:

```
tests/
├── project-crud.spec.ts     # Create, edit, delete project
├── task-creation.spec.ts    # Create task with screenshots (mobile viewport)
├── kanban-board.spec.ts     # Board renders, drag between lanes
├── conversation.spec.ts     # Send message, see agent response
└── mobile/
    ├── task-creation.mobile.spec.ts   # Mobile-specific flows
    └── navigation.mobile.spec.ts      # Mobile nav, gestures
```

Test on multiple viewports: `375x812` (iPhone), `768x1024` (iPad), `1440x900` (desktop). Drive the UI through a sandboxed `./orkka start` so the session cookie is issued the real way.

---

## API Client (Typed)

`packages/core/src/lib/api.ts`, browser-only. URLs are relative: the API is always the page's own origin. Every request goes through `authHeaders(token)` — bearer only when a token is given, `X-Orkka-Csrf: 1` always — with `credentials: "same-origin"`.

---

## Key Dependencies

`vite` 6, `react` 19 (pinned for every dependency with root `overrides`), `react-router` 7,
`use-intl` 4, `@microsoft/signalr` 10, `tailwindcss` 4 (`@tailwindcss/vite`), `vitest` 4,
`@fontsource-variable/*` (see `apps/local/package.json`).
