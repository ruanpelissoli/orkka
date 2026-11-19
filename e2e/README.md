# orkka-e2e-tests

End-to-end tests for Orkka, the local-only developer harness: a **test-case catalogue** (`catalog/`), a **Playwright + API harness** that drives a **sandboxed Orkka** (`orkka.dll start` in its own `ORKKA_HOME`, on its own port), offline **test doubles** (fake Jira Cloud / Azure DevOps / GitHub Projects, stub `gh`, local bare git remotes), and **run reports** (`runs/`).

## One-time setup

```bash
npm install
npx playwright install chromium
cp .env.example .env      # E2E_BASE_URL / E2E_API_URL = http://127.0.0.1:<a free port>, E2E_LOCAL_SANDBOX = a scratch folder
# build Orkka from this clone once (from the repository root):
#   ./orkka start   (orkka.cmd start on Windows; Ctrl+C after it is up)
#   or: (cd ui && npm ci && npm run build) && dotnet build src/Orkka -c Release
npm run stack:start -- --fresh   # starts the sandbox, mints the harness API key and a browser session
```

There is no account: the sandbox's install secret is the root credential; the harness mints an `orkka_key_` API key with it and signs a browser in with the link `./orkka open --print` prints. The sandbox has its own port, so your own Orkka can keep running.

## Every session

```bash
npm run typecheck
npm run test:offline     # needs nothing running
npm run sync-check
npm run stack:start      # then any of the suites below
```

## Suites

`test:offline` · `test:smoke` · `test:api` · `test:workflow` (real agents only with `E2E_WORKFLOW_AGENTS=1`) · `test:screens` · `test:ui-inventory` — see `CLAUDE.md` for what each needs. Reports land in `runs/<timestamp>-<suite>/report.md`; `runs/INDEX.md` lists every run. `npm run stack:stop` stops the sandbox and checks that nothing listens on its port any more.

## Catalogue

`catalog/NN-*.md` hold the cases (format and retired IDs: `catalog/README.md`). `npm run catalog:index` validates them and writes `catalog/index.json` + `catalog/COVERAGE.md`; `npm run manifest` scans this repository (the backend under `src/`, the UI under `ui/`); `npm run sync-check` fails when the product has surface no case covers.
