# PROJECTS.md — Orkka Projects

## Overview

Orkka has a single project kind: the user connects their own repositories and the project's agent team works on them. Orkka does not create repos, scaffold apps, host deployments, or provision databases for the user's code — the repos, their stack, and their CI/CD stay the user's.

---

## Creating a Project from a Folder

A project is created from **one base folder** on the developer's machine (`/projects/new`): the user picks it with the folder picker, the runner discovers what it holds (`POST /api/runners/fs/discover`, read-only) — one repository (monorepo) or the repositories up to two levels below it — and the page previews each one: its path, a **Remote** badge with the host (has an origin: branch push + forge PR) or a **Local folder** badge (no origin: squash-merged into the folder), its branch, dirty/unpushed warnings, description and stack hint. Create (`POST /api/projects/from-folder`) registers them all and opens the board; a repo with no commits is skipped with a warning, and an offline runner is a 422 that names `./orkka start`.

The Repos page shows the base folder with a **Rescan** button (`POST /api/projects/{id}/rescan`) summarising added / missing / restored repos and warnings; opening the page sends a throttled, fire-and-forget `rescan?auto=true`. Each repo has Missing (`missingSince`), Dirty/Unpushed (from the rescan) and Remote/Local badges, and an inline-editable description agents use to pick the right repo.

## Linking More Repositories

More repos can be added to an existing project from the Repos page. Each comes from one of two sources:

| Source | How It Works |
|--------|-------------|
| **Remote by URL** | User pastes any https / ssh / scp-style remote they can already clone (ssh host aliases included), picks the forge when it can't be read from the host, and optionally the forge CLI account. The runner clones and pushes with the developer's own git credentials; reviewed work is published as a forge PR by the developer's own `gh` / `az` / `glab` CLI. Orkka never merges on a forge and holds no forge credentials |
| **Local folder** | User links a folder on their own machine. Work on it runs through the runner (`./orkka start`), so the code never has to leave that machine |

Agents work in an isolated worktree per task (see **Repository Management** in CLAUDE.md). Deployment is the user's own CI/CD — the runner pushes branches / opens PRs and the user's existing pipeline takes it from there.

---

## Stack Awareness

Any tech stack is supported — .NET, Go, Node, Python, Next.js, whatever is in the repo. Repo indicators used to pick the right dev agent:

- `package.json` + a frontend framework (Next.js, React, Vue, Angular) → Frontend
- `*.csproj` / `go.mod` / `requirements.txt` / `pyproject.toml` → Backend
- Both frontend and backend indicators → FullStack
- Only `terraform/` / `infra/` / `helm/` / `Dockerfile` → Infrastructure

---

## Agent-Repo Assignment

Every project is seeded with the full agent roster (see **AGENTS.md**). Dev agents can be scoped to specific repos; cross-repo agents work across all repos in the project.

| Agent Role | Repo Scope | Reasoning |
|-----------|-----------|-----------|
| **PM** | Cross-repo (null) | Reviews all tasks regardless of repo |
| **FrontendDev** | Assigned to frontend repo | Only works in frontend codebases |
| **BackendDev** | Assigned to backend repo | Only works in backend codebases |
| **FullStackDev** | Assigned to repo | Works in its assigned repo; fallback dev agent |
| **QA** | Cross-repo (null) | Tests can span repos, validates integration |
| **CodeReviewer** | Cross-repo (null) | Reviews PRs from any repo |
| **DevOps** | Cross-repo (null) | Infra spans all repos |

When a task targets a repo and has no manually assigned agent, the worker picks the dev agent scoped to that repo, falling back to the project's FullStackDev.

---

## CLAUDE.md Context

If a linked repo has a `CLAUDE.md` (or `.claude/CLAUDE.md`), agents read it as project context — it is layered into the system prompt (see **System Prompt Assembly** in AGENTS.md) and picked up automatically by the Agent SDK running in the worktree. The more detail it holds about conventions, commands, and architecture, the better the agents perform.
