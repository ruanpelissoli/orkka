# Run 20261026-2233-offline

| | |
|---|---|
| Suite | `offline` |
| Target | offline |
| Started | 2026-10-27T01:33:55.559Z |
| Duration | 13.3 s |
| Result | 17 passed · 2 failed · 0 timed out · 3 skipped |
| Operator | claude · harness uncommitted · node v24.18.0 · playwright 1.63.0 |

## Notes
- Playwright overall status: failed
- No environment configured (E2E_BASE_URL unset): only offline specs ran; environment-bound specs were skipped.

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ❌ failed | — | seeded from the web fixture; the alias URL clones, a branch pushes, a merge lands on main | 1.0 s | — | — |
| ✅ passed | — | custom files and default branch; a teammate's commit moves the head | 1.4 s | — | — |
| ✅ passed | — | auth: /myself answers for email+token and 401s anything else | 63 ms | — | — |
| ✅ passed | — | scopes, statuses and the Acceptance Criteria field lookup | 28 ms | — | — |
| ✅ passed | — | epic search: by title prefix, by key, by bare number, unknown key → 400 | 47 ms | — | — |
| ✅ passed | — | polling query: assigned to me, under the epics, not done, extra clause; rendered fields; paging | 30 ms | — | — |
| ✅ passed | — | comments newest-first with rendered bodies; attachments via a 303 to the media host | 27 ms | — | — |
| ✅ passed | — | write-back: status read, transitions list, POST transition 204, invalid id 400 | 61 ms | — | — |
| ✅ passed | — | failure injection and an outage | 9 ms | — | — |
| ✅ passed | — | auth: connectionData for the PAT; a wrong PAT gets 203 + HTML like the real service | 8 ms | — | — |
| ✅ passed | — | projects, work item types with state categories, type categories | 20 ms | — | — |
| ✅ passed | — | two-step polling WIQL: hierarchy (MustContain / Recursive), then the flat filter; batch with relations | 48 ms | — | — |
| ✅ passed | — | comments (desc, deleted skipped by the runner), attachments, System.State write-back | 47 ms | — | — |
| ❌ failed | `TCLI-007` `TCLI-008` `TCLI-011` | tracker test/list: the runner signs in to the fake Jira and ADO with the seeded tokens; tokens never printed | 1.8 s | — | — |
| ⏭️ skipped | `TCLI-008` | a revoked token: tracker test reports failing with Jira's advice and exits 1 | 0 ms | — | — |
| ⏭️ skipped | — | tracker add cannot target a loopback site: --site http://127.0.0.1:<port> is rewritten to https://127.0.0.1 (no port) | 0 ms | — | — |
| ⏭️ skipped | `TCLI-009` | tracker remove deletes the entry and its token | 0 ms | — | — |
| ✅ passed | — | auth status reports the signed-in accounts the way ForgeCli.SignedInAccounts parses them | 992 ms | — | — |
| ✅ passed | — | publish: pr list finds nothing → pr create prints the URL → a retry finds the open PR (idempotent) | 603 ms | — | — |
| ✅ passed | — | state: pr view --json state --jq .state follows OPEN → MERGED (ForgeCli.NormalizeState → merged) | 590 ms | — | — |
| ✅ passed | — | account scoping: auth token --user prints that account's token; GH_TOKEN reaches the call record | 466 ms | — | — |
| ✅ passed | — | a configured failure and a signed-out CLI fail like gh does | 294 ms | — | — |

## Failures

### ❌ seeded from the web fixture; the alias URL clones, a branch pushes, a merge lands on main

- File: `tests/offline/bare-remote.spec.ts`
- Trace: `..\current\artifacts\offline-bare-remote-bare-r-0664c-ushes-a-merge-lands-on-main-chromium\trace.zip` (open with `npx playwright show-trace <path>`)

```
Error: origin keeps the alias; only transport is rewritten

[2mexpect([22m[31mreceived[39m[2m).[22mtoBe[2m([22m[32mexpected[39m[2m) // Object.is equality[22m

Expected: [32m"[7mhttps://github.com/e2e-local[27m/e2e-web-mvq0ib20-39b3.git"[39m
Received: [31m"[7mfile:///C:/Projects/orkka/orkka-e2e-tests/.runs/scratch/remotes[27m/e2e-web-mvq0ib20-39b3.git"[39m
```
Logs: API + Worker console output of the stack at http://127.0.0.1:9; runner log .runs/runner.log

### ❌ tracker test/list: the runner signs in to the fake Jira and ADO with the seeded tokens; tokens never printed

- File: `tests/offline/runner-tracker-cli.spec.ts`
- Cases: `TCLI-007`, `TCLI-008`, `TCLI-011`
- Trace: `..\current\artifacts\offline-runner-tracker-cli-83bcb-tokens-tokens-never-printed-chromium\trace.zip` (open with `npx playwright show-trace <path>`)

```
Error: [2mexpect([22m[31mreceived[39m[2m).[22mtoContain[2m([22m[32mexpected[39m[2m) // indexOf[22m

Expected substring: [32m"jira-fake: ok — signed in as E2E Developer"[39m
Received string:    [31m"jira-fake: ok - signed in as E2E Developer"[39m
```
Logs: API + Worker console output of the stack at http://127.0.0.1:9; runner log .runs/runner.log

## Skipped

- a revoked token: tracker test reports failing with Jira's advice and exits 1
- tracker add cannot target a loopback site: --site http://127.0.0.1:<port> is rewritten to https://127.0.0.1 (no port)
- tracker remove deletes the entry and its token

## Cleanup

- Projects created this run: 0
- Leftovers after teardown: none
