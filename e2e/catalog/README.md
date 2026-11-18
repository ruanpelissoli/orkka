# Orkka E2E test-case catalogue — format

The catalogue is the source of truth for **what Orkka does and how we prove it**. Every page, menu, button, textbox, setting, CLI verb and API behaviour has a case here. Playwright specs link to cases by ID; `scripts/catalog-index.ts` validates the files and computes coverage; `scripts/sync-check.ts` fails when the product grows a page/endpoint no case references.

## Files

One markdown file per area: `catalog/NN-<area>.md`. Each file starts with YAML frontmatter:

```md
---
area: HYB
title: Hybrid — bring your own repo
modes-default: [hybrid-repo]
---
```

`area` is the ID prefix for every case in the file. `modes-default` is applied to cases that omit `modes`.

## Case

One case = one `##` heading `## <AREA>-<NNN> <Title>` followed by exactly one fenced `yaml` metadata block, then the prose sections.

````md
## HYB-008 Create a project and add the fixture repo by URL
```yaml
priority: P0
modes: [hybrid-repo]
surface: [api, ui]
automation: automated
refs:
  - orkka-api:POST /api/projects
  - orkka-api:POST /api/projects/{projectId}/repos
  - orkka-ui:/p/[projectId]/repos
  - i18n:addRepoByUrl
```
**Preconditions**: runner online; the developer's own git can read the fixture remote.
**Steps**:
1. `POST /api/projects {name:"e2e-…", key:"E2E"}`.
2. `POST /api/projects/{id}/repos {gitUrl, name, defaultBranch:"main"}`.
3. Open `/p/{slug}/repos`.
**Expected**:
- 201; `GET …/repos` lists the repo with `provider` detected from the host.
- Repos page lists the repo with its forge icon and an "Open remote" link.
**Notes**: cleanup deletes the project.
````

### Metadata fields

| Field | Required | Values |
|---|---|---|
| `priority` | yes | `P0` (must pass every run of its mode), `P1`, `P2`, `P3` |
| `modes` | yes (or file default) | subset of `hybrid-repo` (a remote repo on the developer's runner), `hybrid-folder` (a linked local folder), `tracker` (a project fed by a Jira/ADO ticket source — the fake tracker locally), `offline` (needs no deployed Orkka: CLI, fake servers, stubs), `all` |
| `surface` | yes | subset of `ui`, `api`, `cli`, `mobile`, `admin`, `hub` |
| `automation` | yes | `automated` (a spec links it), `planned` (will be automated), `manual` (needs a human or a device), `blocked` (feature unreachable; say why in Notes), `retired` |
| `refs` | yes, ≥ 1 | see forms below |
| `blockedBy` | no | free text |
| `costUsd` | no | estimated cloud spend when run |
| `since` | no | `YYYY-MM-DD` the case was added |

### Ref forms (what `sync-check` matches against the surface manifest)

| Form | Example | Matches |
|---|---|---|
| `orkka-api:METHOD /path` | `orkka-api:PATCH /api/tasks/{id}/lane` | an API route — FastEndpoints in `src/Modules`, minimal APIs in `src/Orkka.Local` (`/local/*`, `/launch`, `/blobs/*`) and `src/Orkka.Runners.Server` (`/internal/runner/*`) (route constraints like `:ulid` are ignored; any `{param}` name matches any other) |
| `orkka-api:<file>` | `orkka-api:src/Modules/Orkka.Modules.Tasks/Features/Tasks/CreateTaskEndpoint.cs` | documentation — the file must exist (relative to the repository root) |
| `orkka-ui:<route>` | `orkka-ui:/p/[projectId]/board` | a page the `ui/apps/local` SPA routes (`src/routes.tsx`; the page modules live under `app/[locale]/(dashboard)/…/page.tsx`, and the route is written in that folder form) |
| `orkka-ui:<file>` | `orkka-ui:packages/ui/src/components/board/TaskBoard.tsx` | documentation — the file must exist (relative to `ui/`) |
| `i18n:<namespace>` | `i18n:taskCreateForm` | a top-level key of `packages/i18n/messages/en/dashboard.json` (or `common.json`, `landing.json`) |
| `hub:<HubName>.<Event>` | `hub:TaskHub.LaneChanged` | a SignalR server→client event |
| `cli:<verb>` | `cli:connect` | a `./orkka <verb>` CLI verb (`src/Orkka/Program.cs`) |

A ref must point at surface that EXISTS: `sync-check` fails on a live (non-retired) case whose route, page, namespace, verb, hub event or source file is gone. A case that deliberately names removed surface (e.g. asserts a route answers 404) lists that ref in `sync-ignore.json` → `danglingRefs` as `"<CASE-ID> <ref>": "reason"`; an allowance nothing uses any more fails too.

## Writing rules

- Steps are **click-by-click** for UI (name the control as the user sees it, in English) and **request-by-request** for API. Expected results are observable facts (status codes, visible text, DB-visible state via another request), not intentions.
- One behaviour per case. A form with 12 fields gets one "happy path" case plus one case per field that has validation or side effects.
- Negative cases (403 for a missing permission, 402 plan limits, 404 for a foreign project, offline runner, capability missing) live next to the positive case in the same area, except cross-cutting ones which live in `95-negatives.md` (`NEG-`).
- Nothing spends credits any more: agents run on the developer's own AI CLI and cloud execution was removed (orkka-api e3a3a06, 2026-11-17). `costUsd`, when present, is `0` (the parser still rejects a non-zero cost outside a `cloud-*` mode).
- Never reuse an ID — see **Retired IDs** below. A case removed one at a time may keep its heading with `automation: retired` and a one-line Notes; a feature removed wholesale is deleted and its IDs are recorded below.
- Keep `refs` honest: only routes/pages the steps actually touch.

## Retired IDs

The 2026-10-26 pivot (tracker-driven, runner-only) deleted these cases. Their numbers are never reused.

- Whole areas — the prefixes themselves are retired: `HOS` (hosted apps, 1–55), `CLD` (cloud runs, 1–31), `DEP` (Vercel deployments, 1–13), `LOC` (Orkka Local stack and Local UI, 1–45), `SDK` (platform SDK, 1–15).
- Inside surviving areas the deleted numbers stay unused; a new case takes the next number above the highest the area ever had: ONB 004–019, 023, 028, 032, 035 · HYB 007, 031–037 · HYF 009, 016, 017, 023 · BRD 018 · TSK 008, 022, 032, 033, 048 · TEAM 002, 010 · MEM 016 · SKL 013 · ARC 012 · AUTO 025, 026, 029 · RDM 016, 017, 020 · REPO 003–005, 011, 012, 020, 025 · INT 013–016 · RUN 018 · SET 007, 021, 031–035, 039 · NAV 002, 003, 005, 008, 020 · BIL 020, 027 · ADM 021 · MOB 003 · MKT 007 · I18N 005 · NEG 021.
- Removed with cloud execution (orkka-api e3a3a06, 2026-11-17 — the execution preference, the execution-target preview/switch and the top-bar execution badge): HYB 001–006, 045 · TSK 055, 058 · AIR 006 · RUN 006, 007.
- Removed with the cloud product's leftovers (2026-11-18 catalogue audit — every ref pointed at a page, route or message namespace that no longer exists): ONB 001, 020–022, 026, 027, 029–031, 033, 034, 036–045 (the `/onboarding` wizard; a project is created from a folder on `/projects/new` — ONB-024/025, LWF-002, the first run is NAV-026/LWF-014) · WSP 010 (invites) · AUD 001–009, 016 (the audit page, chain verify and export; `GET /api/audit` remains, no UI), 011 (member permissions), 014 (verify/export 403) · RUN 002 (token generator — `./orkka start` mints tokens) · SET 002 (Account tab / Clerk profile), 003 (org rename on Account — LWF-013), 010 (Organization tab), 011 (org runners roster — RUN-013 is API-only), 040 (Free-plan limits) · KEY 012 (platform admin), 013 (member permission intersection), 019 (public docs page — the MCP command is on `/settings/api-keys`, KEY-001) · NAV 016 (bug reports) · NTF 008 (push devices) · I18N 008 (marketing pages), 010 (BRL pricing), 012 (sign-up consent) · REPO 006 (plan limits) · NEG 009 (billing writes), 014 (402 plan limits), 015, 016 (rate limiting — no limiter in the host), 017, 018 (org suspension), 026 (OpenAPI/Scalar — never mapped by the host) · AUD 015 (retention sweeper — gone; the audit chain is kept whole).
- Areas added by the pivot: `WSP` (11), `RURL` (16), `FRG` (17), `TSRC` (18), `TCLI` (19), `TING` (27), `TWB` (28), `AIR` (29), `GRD` (96).
- Removed when Orkka moved to running from source (R4, 2026-11-18 — no release binary, no newer-release check, no signed-release install, no `path add`, no `--detach`): RUN 010 (update nudge on My Runner), 021 (`--no-update-check` / `ORKKA_NO_UPDATE_CHECK`), 041 (`update` installs only a signature-verified release), 042 (`connect` announces a newer release). Rewritten in place: RUN-020 (`update` prints the git-pull hint), TCLI-013 (`uninstall` deletes nothing), LWF-001 (one process), LWF-015 (`open`; the PATH entry is gone).
- The 2026-11-17 local-only pivot deleted the cloud product; these whole areas are retired with their files: `MBR` (members & invites, 40), `BIL` (billing — the slim usage page is `LWF-012`, 41), `INT` (Slack, webhooks, integrations, 44), `ADM` (admin console, 60), `MOB` (mobile app, 70), `MKT` (marketing site & Clerk sign-in, 80). Added: `LWF` (local-only workflow, 50).

## Regenerate

```
npm run catalog:index      # validates + writes catalog/index.json and catalog/COVERAGE.md
npm run manifest           # rescans src/ and ui/ (E2E_SRC_API / E2E_SRC_UI) and diffs against catalog/surface-manifest.json; manifest:update writes it
npm run sync-check         # fails on any page/route/namespace/verb without a case
```
