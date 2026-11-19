# CLAUDE.md — orkka-e2e-tests

End-to-end test programme for Orkka **after the 2026-11 local-only pivot**: one machine, no account, no cloud, run from a git clone. `./orkka start` builds the clone and runs ONE foreground process (`dotnet src/Orkka/bin/Release/net10.0/orkka.dll start`): the `Orkka.Local` host (127.0.0.1:6750 by default — the API, the hubs, the built static UI from `ui/apps/local/dist`, SQLite at `~/.orkka/data/orkka.db`) and the runner, in the same process. A project is a base folder (a monorepo or a folder of repos); tickets come from Jira / Azure DevOps / GitHub Projects; agents run on the developer's own AI CLI; reviewed work on a remote is published as a forge PR with the developer's `gh`/`az`/`glab`. Three deliverables live here:

1. **`catalog/`** — the test-case catalogue (`catalog/README.md` is the format contract incl. the **Retired IDs**; `catalog/COVERAGE.md` is generated). `50-local-workflow.md` (`LWF`) covers the local-only workflow.
2. **`tests/` + `src/`** — the Playwright + typed-API harness (`caseIds(...)` links a spec to catalogue IDs), the sandboxed Orkka (`src/local/stack.ts`), and the offline test doubles: a **fake Jira Cloud + Azure DevOps + GitHub (Projects v2 GraphQL) server**, a **stub `gh`**, **local bare git remotes**.
3. **`runs/`** — one report per run (`report.md` + `report.json`) and the append-only `runs/INDEX.md` (rows before 2026-10 are prod runs of the cloud product; 2026-10/11 offline rows predate the local stack).

## Session-start ritual

```
npm run typecheck
npm run test:offline    # fake tracker + stub gh + bare remotes + the runner's tracker/commands CLI; needs nothing running
npm run sync-check      # fails when Orkka grew a page/route/namespace/verb with no case
npm run catalog:index   # validates the catalogue, regenerates index.json / COVERAGE.md / case-ids.d.ts
```

If `sync-check` lists NEW surface: author cases in the right `catalog/NN-*.md`, then `npm run manifest:update`. Never hand-edit `index.json`, `COVERAGE.md`, `surface-manifest.json`. After adding/removing `caseIds(...)` links run `npm run catalog:sync-automation` then `npm run catalog:coverage`.

## The sandboxed Orkka

Build this clone once — `./orkka start` from the repository root (`orkka.cmd start` on Windows; Ctrl+C when it is up), or `(cd ui && npm ci && npm run build) && dotnet build src/Orkka -c Release`. The harness runs that build in place (`E2E_ORKKA_DLL`, default `src/Orkka/bin/Release/net10.0/orkka.dll`); rebuilding needs the sandbox stopped (a running `orkka.dll` locks its `bin/`).

`.env`: `E2E_BASE_URL=E2E_API_URL=http://127.0.0.1:<a free port>` (not 6750 — your own Orkka listens there), `E2E_LOCAL_SANDBOX=<a folder outside your ~/.orkka>`. Then:

| Command | What |
|---|---|
| `npm run stack:start [-- --fresh]` | `dotnet orkka.dll start --no-browser --port <E2E_BASE_URL's port>` as a detached background process with `ORKKA_HOME=<sandbox>/home`, `ORKKA_SECRET_STORE=file`, `ORKKA_TRACKER_ALLOW_INSECURE_SITE=1`, PATH starting with the stub `gh`, git `insteadOf` for the bare remotes; waits for `/health` (`service: orkka`); then mints the harness API key and a browser session (`./orkka open --print` → the launch link). Its console goes to `<sandbox>/orkka-start.out`. `--fresh` stops it and wipes the sandbox home first |
| `npm run stack:auth` | re-mint the API key (`POST /api/me/api-keys`, bearer = install secret) and the session (`open --print`) |
| `npm run stack:status` / `stack:stop` | `./orkka status` / `stop` against the sandbox (`stop` kills the process tree if the process outlives it); `stack:stop` exits 1 if anything still listens on the port |

Auth model: the install secret (`<sandbox>/home/workspaces/_local/secrets/install-secret`) is the root bearer; specs use an `orkka_key_` API key (`.auth/api-key.json`); the browser uses the session cookie (`.auth/storageState.json`). Cookie-authenticated writes need `Origin` + `X-Orkka-Csrf: 1`. Global setup re-mints a stale key/session itself.

| Command | What | Needs |
|---|---|---|
| `npm run test:offline` | fake Jira/ADO/GitHub self-tests, stub gh (incl. draft/ready/edit), bare remotes, `./orkka tracker …` / `commands …` (`dotnet orkka.dll`, skipped when unbuilt) | nothing running |
| `npm run test:smoke` | key + session, workspaces, runner, connections, signed-out page, Host allowlist (421), CSRF | sandbox |
| `npm run test:api` | workspaces (WSP, via the install-secret bearer; WSP-002 creates a second workspace in the sandbox, WSP-006 uses it), ticket-source validation (TSRC), the guided project setup (PSU: steps, Start, the command check, the dry run without a source) | sandbox |
| `npm run test:workflow` | **LWF-002…008**: base folder → project, write-only Jira connection (token only in the secret store — scanned files + the SQLite database and its WAL), ticket source and Start, ADO intake (the dry run lists the work item, nothing is pulled before Start — PSU-009/010). Steps 4–6 (standalone ticket, question routing, Story) run REAL agents on your AI subscription — only with `E2E_WORKFLOW_AGENTS=1` (≈3 small Haiku pipeline runs, 30–60 min; `--grep-invert` the steps you do not want, e.g. only the Story) | sandbox + signed-in AI CLI |
| `npm run test:screens` | the key screens (LWF-009) → `<sandbox>/screens/*.png`; run with `E2E_KEEP_PROJECTS=1 E2E_SCREENS_PROJECT=<slug>` after a workflow run | sandbox |
| `npm run test:ui-inventory` | every local page renders, no 5xx | sandbox |
| `npm run fake-tracker` | the fake Jira/ADO/GitHub standalone on :4010 (GitHub site `<origin>/github`, API `<origin>/github/graphql`) | — |

Never pass `--reporter=…`: it replaces the config's reporters and no run report is written. Every run: pre-sweep of stale `e2e-*` projects → tests → teardown deletes everything created → `runs/<stamp>-<suite>/report.md` + `runs/INDEX.md` row. `E2E_KEEP_PROJECTS=1` skips BOTH the sweep and the teardown (a spec that reads what an earlier run left). Failures: `<sandbox>/home/logs/orkka.log` (host + runner, structured) and `<sandbox>/orkka-start.out` (the console).

## Safety rules (do not relax)

- **Sandbox only.** The harness talks to a loopback Orkka that accepts the SANDBOX's install secret (`assertSandboxStack`) — never your own Orkka (give the sandbox its own port). Never point `E2E_LOCAL_SANDBOX` or `E2E_RUNNER_HOME` at the real `~/.orkka`.
- Only projects named `e2e-*` may be modified or deleted (`src/safety/guards.ts`).
- Tracker tokens: only the fake tracker's random tokens. `ORKKA_SECRET_STORE=file` keeps them (and the sandbox's install secret) out of the OS keychain — check `cmdkey /list | findstr orkka/` after a run.
- No real forge: repos are local bare remotes behind `https://github.com/e2e-local/*` aliases (only the stack's git resolves them) and PRs go to the stub `gh`.
- Real agent runs spend the developer's subscription: keep tasks tiny, agents on Haiku (`useCheapestModel`), and only behind `E2E_WORKFLOW_AGENTS=1`.
- `.auth/`, `.env`, `.runs/` are gitignored — never commit or print them.

## Layout

`config/` the `.env` schema · `src/local` the sandboxed Orkka (`stack.ts`), secret scans (`secret-scan.ts`: files + the SQLite database) · `src/api` typed client · `src/auth` API key / storage state · `src/safety` guards, sweep · `src/runner` the offline `./orkka` CLI sandbox (`dotnet orkka.dll`), presence/capabilities · `src/fakes/tracker` fake Jira Cloud + Azure DevOps + GitHub (`github.ts`: the GraphQL operations of `GitHubProjectsClient`; it also accepts the stub gh's `gho_stub_<login>` token, so a "use my gh login" connection works) · `src/fakes/stub-gh.ts` + `fixtures/stub-gh/` · `src/testdata` bare remotes, fixture apps, base folders (`base-folder.ts`) · `src/flows/local-workflow.ts` workflow building blocks (from-folder, add connection, Discuss, apply, followers) · `src/fixtures/test.ts` Playwright fixtures (`api`, `projects`, `localRunner`, `fakeTracker`, `stubGh`, `bareRemotes`) · `src/reporter` run reports · `src/catalog` parser + manifest · `scripts/` operator commands (`stack.ts`, …).

## Known facts (2026-11-18)

- Since R4 (run from source) the sandbox is ONE process; the R5 run (`runs/20261118-*-run-from-source`) re-ran every suite plus one real Story on it. The 2026-11-17/18 live run on the old 4-process stack (`runs/20261117-2321-workflow-live`) found and fixed 11 product bugs.
- **insteadOf:** the alias remotes rely on git `url.<file URL>.insteadOf https://github.com/e2e-local/` passed as `GIT_CONFIG_*` to the stack; Orkka reads origins as CONFIGURED (`config --get remote.origin.url`), so they classify as GitHub and publish through the stub gh.
- On Windows the runner starts `gh` without a shell (only `.exe`), hence the compiled `gh.exe` shim (`fixtures/stub-gh/GhShim.cs`, built once with the Windows .NET Framework `csc.exe`). The stub's state survives a stack restart (`--fresh` resets it).
- After a restart a ticket source waits up to 5 min for the previous runner's poll lease. `followUntil` tolerates ~5 min of a stopped Orkka, so a spec survives a stop/restart mid-run.
- `node --test test/` fails on Node 24; the API fixture uses `node --test`.
- The product tour covers project pages on a fresh install: specs set `onboardingTourSeen` first.
- A slug of exactly 26 characters 404s (NEG-030, expected to fail).

## Commit policy

Commit catalogue changes, specs, harness code and `runs/**/report.{md,json}` + `runs/INDEX.md`. Never commit `.env`, `.auth/`, `.runs/`, screenshots or traces (`runs/**/artifacts/`).
