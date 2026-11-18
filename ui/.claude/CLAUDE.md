# CLAUDE.md — Orkka

## Product Vision

Orkka is for **developers**, especially those who work several jobs at once. The tickets assigned to them in each employer's tracker (Jira Cloud, Azure DevOps) are worked by a persistent, named AI dev team — PM, devs, QA, reviewer — that runs on **the developer's own machine** through the `./orkka` runner, with their own git credentials and their own AI CLI subscription (Claude Code, Codex, Copilot).

**Orkka is local-only.** `./orkka start`, run from the repository's clone, runs everything in one process on 127.0.0.1: the Orkka.Local host (API + processing, serving this UI's built files; its database is one SQLite file) and the runner. There is no account, no sign-in and no cloud. One implicit local user owns every workspace (one workspace per job).

**It must pass a company's security review.** Orkka stores no source code and holds no git, forge, tracker or AI credential outside the developer's own machine; the runner only does named operations and runs repository commands only when the repo or the developer vouches for them. orkka-api `.claude/docs/SECURITY.md` is the reference.

---

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Frontend | Vite + React Router single-page app (static `dist/`, served by the Orkka.Local host), use-intl, Tailwind v4, SignalR client |
| Backend API | C# / .NET 10, FastEndpoints, SignalR |
| ORM | Entity Framework Core (SQLite) |
| Agent SDK | Claude Agent SDK (.NET) — agents run inside repo worktrees |
| QA Testing | Playwright MCP — QA agent tests live apps in headless browser |
| Database | SQLite — one file (`~/.orkka/data/orkka.db`) opened by the host; nothing to install |

---

## User Model & Auth

There are no accounts. A single implicit local user owns every workspace. Workspaces stay (one per job), switched with the `X-Orkka-Workspace` header / `orkka_ws` cookie. Members, invites, billing and the audit UI are gone.

How the UI authenticates (contract with the Orkka.Local host):
- The browser talks only to the host at `http://127.0.0.1:6750`. The host serves the UI's built files (`apps/local/dist`) itself: static assets, `index.html` for every other locale-prefixed path, and a redirect of `/` and unprefixed paths to `/{locale}/…` — the full contract is in `apps/local/README.md`.
- **Browser:** httpOnly `orkka_s` session cookie, issued by `GET /launch?code=…`. Client fetches and SignalR are same-origin, `credentials: "same-origin"`, no Authorization header, and always `X-Orkka-Csrf: 1` (`packages/core` `authHeaders`; `useClientApi()` passes an empty token).
- **No UI server.** Every page loads its data in the browser (`useApiQuery`), and mutations (`apps/local/lib/actions.ts`) run there too, followed by `refreshData()` — see `.claude/docs/FRONTEND.md` → Core Rules.

`api.org.me` is loaded by the dashboard layout → `PermissionsProvider` → `usePermissions()`; the local user is Owner, so permission gates are effectively open (the API is the boundary).

---

## AI Agent Team System

Every project gets a persistent AI development team seeded on creation. Agents are named, specialized, and configurable. See orkka-api `.claude/docs/AGENTS.md` for full details.

| Agent | Default Name | Role | Repo Scope |
|-------|-------------|------|------------|
| PM | Luna | Refine tasks, ensure clarity | Cross-repo |
| Designer | Muse | Logos, icons, illustrations, visual assets | Cross-repo |
| Frontend Dev | Pixel | Next.js, React, Tailwind | Assigned to frontend repo |
| Backend Dev | Atlas | .NET, Go, Node — detects from repo | Assigned to backend repo |
| Full Stack Dev | Nova | Cross-stack tasks, fallback dev | Assigned to repo |
| QA | Scout | Write/run tests, validate criteria | Cross-repo |
| Code Reviewer | Sentinel | Review PRs, enforce standards | Cross-repo |
| DevOps | Forge | CI/CD, Docker, Helm, Terraform | Cross-repo |
| Game Dev | Rune | Game programming — gameplay systems, engines, performance | Cross-repo (dev pipeline) |
| Game Designer | Sprite | Game assets — sprites, pixel art, tilesets, game UI | Cross-repo |
| DBA | Sage | Database expertise — SQL/T-SQL, indexing, performance (advisory chat only) | Cross-repo |
| Legal | Lex | Project law advisor — legality questions, terms of use, privacy policy (advisory chat only) | Cross-repo |
| Mobile Dev | Nomad | Android (Kotlin/Compose), iOS (Swift/SwiftUI), React Native/Flutter/MAUI/KMP | Cross-repo (dev pipeline) |

**All available agents are seeded with the project — always.** There is no Add Agent button, no enable/disable toggle, and no optional-agent ghost cards: the team page only lists, configures, and chats with the seeded roster. Whether an agent is used is up to the user.

**Key rules:**
- Users can rename agents, change models, and update instructions — but not add or disable them
- Dev agents are assigned to specific repos — no frontend agent for a Go backend repo
- Cross-repo agents (PM, QA, Reviewer) work across all repos
- Agents are only created if the pipeline has their corresponding lane (no Reviewer if code review is skipped)
- Designer (Muse) creates assets from Team chat (always SVG the agent authors itself — Orkka has no raster image or GIF generation and no image-model picker). Assets appear on the project's Design page (`/p/{projectId}/design`) for approve/reject/iterate; the iterate button deep-links back into Muse's chat with the asset attached

**Multi-agent flow:** PM refines → Designer creates assets (if needed) → Dev plans & codes → QA tests → Reviewer reviews → Done.

---

## Pipeline Lanes

| Lane | Agent | Description |
|------|-------|-------------|
| **Drafting** | — | Human writes the task |
| **Ready** | PM | PM reviews for completeness, asks questions |
| **Planning** | Dev | Dev agent creates execution plan |
| **Plan Ready** | — | Human approves plan |
| **Pending Questions** | Current | Agent needs clarification |
| **Developing** | Dev | Dev agent writes code in worktree |
| **Testing** | QA | QA tests live app via Playwright MCP browser, writes E2E tests |
| **Code Review** | Reviewer | Reviews diff, leaves line comments |
| **Done** | — | Merged into a linked folder, or published as a pull request under the developer's account |

Lanes are configurable per project. Tasks can move backward.

---

## Usage

Agents run on the developer's own AI subscription, so Orkka bills nothing. Usage shown in the UI is an estimate of what the runs cost that subscription, never a charge. There is no billing, credits or plan UI.

---

## Projects

There is one project kind: the user connects their own repositories and agents work on them. See **PROJECTS.md** for full details.
- A project is created from one base folder on the user's machine — a monorepo or a folder of repositories, discovered by the runner; more repos can be added by URL (any https / ssh / scp remote the developer can already clone) or as a local folder — all worked on through the `./orkka` runner with the developer's own git credentials
- Any tech stack — auto-detected from repo analysis
- Dev agents are assigned per repo based on the detected stack (no frontend agent for a Go backend repo)
- The repo's CLAUDE.md, if present, is fed to agents as project context
- Deployment stays with the user's own CI/CD

---

## Repository Management

Everything happens on the developer's machine, in the runner's cache: a bare mirror per repository plus a standalone clone per task. Agents run there with that clone as their working directory, under the runner's guards (no credentials, no other repositories, no pushes — Orkka pushes and opens the PR).

```
~/.orkka/repos/{org_id}/{repo_id}/     (on the developer's machine)
  bare/              ← bare mirror
  worktrees/
    {KEY-n}/         ← standalone clone per task
```

**No forge integration on Orkka's side.** The runner clones and pushes with the developer's own git credentials, and reviewed work on a remote repo is published as a pull request by the developer's own `gh` / `az` / `glab` CLI. Orkka never merges on a forge.

**In-app features:** File tree browser, file viewer, PR diff viewer, line-level comments, approve — all inside Orkka, every read served live by the developer's runner (Orkka keeps no copy of the code; the UI must handle "runner offline" for every one of them). Approved work merges into a local folder, or is published as a forge PR for a remote repo.

---

## Database

One SQLite file (`~/.orkka/data/orkka.db`) that only the host opens. See the repository root's `.claude/docs/DATABASE.md`. Every platform entity still carries `OrgId` (the workspace), enforced by an EF Core global query filter.

---

## Project Structure

This repository (orkka-ui) holds the local web UI and the shared packages:

```
orkka-ui/
├── .claude/CLAUDE.md          # This file
├── apps/local/                # the SPA (@orkka/local-ui): Vite + React Router, built to dist/, served by Orkka.Local
└── packages/
    ├── core/                  # API client, types, hooks
    ├── ui/                    # Shared components (board, code view, task, PR)
    └── i18n/messages/         # en, pt, es, fr, de — common.json + dashboard.json, kept in lockstep (parity tests)
```

`npm run build` (in `ui/`) writes `apps/local/dist/` — `index.html` plus hashed assets. Node is needed to build, never to run.

The host and runner (`src/Orkka.Local`, `src/Orkka.LocalRunner`, run by `src/Orkka` / `./orkka`) live at the repository root; its `.claude/CLAUDE.md` and `.claude/docs/` are the backend reference.

---

## Coding Conventions

### C# / .NET 10
- Latest C# features: primary constructors, collection expressions, pattern matching
- FastEndpoints (no controllers), vertical slice architecture, sealed by default
- EF Core code-first, global query filter for OrgId, async everywhere
- Result pattern for errors, Serilog for logging
- Claude Agent SDK for agent execution with `Cwd` set to worktree

### TypeScript / React
- A static SPA: routes in `apps/local/src/routes.tsx`, page data through `useApiQuery`, refreshed with `refreshData()` after a write; navigation through `@/i18n/navigation` (locale-prefixed) and `@/lib/navigation` (params, query string)
- Tailwind mobile-first, local session-cookie auth (no Clerk), SignalR for realtime
- Mutations in `lib/actions.ts` (browser), Playwright MCP for E2E testing

### Database
- EF Core migrations (SQLite). Every entity: OrgId, CreatedAt, UpdatedAt. ULIDs. JSON as text.

### Git
- `main` = deployable. Branches: `feat/`, `fix/`, `chore/`, `docs/`. Conventional commits.

---

## Companion Docs

UI docs live in `.claude/docs/` here:

| File | Contents |
|------|----------|
| **FRONTEND.md** | Routing, i18n and navigation, build and the host contract, design system (colors, typography), component rules, local auth |
| **PROJECTS.md** | Project model as the UI presents it: base-folder projects, rescan, repos by URL or local folder, stack, agent-repo assignment |

Backend, agents, database and security are documented once, in orkka-api `.claude/docs/` (AGENTS, BACKEND, DATABASE, PROJECTS, SECURITY, TRACKERS, RUNNER_GIT, INTERACTIVE) — read them there rather than keeping copies here, which drift.
