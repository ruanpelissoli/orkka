# Run 20261027-0009-offline

| | |
|---|---|
| Suite | `offline` |
| Target | offline |
| Started | 2026-10-27T03:09:55.077Z |
| Duration | 7.3 s |
| Result | 18 passed · 1 failed · 0 timed out · 3 skipped |
| Operator | claude · harness uncommitted · node v24.18.0 · playwright 1.63.0 |

## Notes
- Playwright overall status: failed
- No environment configured (E2E_BASE_URL unset): only offline specs ran; environment-bound specs were skipped.

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ✅ passed | — | seeded from the web fixture; the alias URL clones, a branch pushes, a merge lands on main | 1.9 s | — | — |
| ✅ passed | — | custom files and default branch; a teammate's commit moves the head | 1.3 s | — | — |
| ✅ passed | — | auth: /myself answers for email+token and 401s anything else | 27 ms | — | — |
| ✅ passed | — | scopes, statuses and the Acceptance Criteria field lookup | 20 ms | — | — |
| ✅ passed | — | epic search: by title prefix, by key, by bare number, unknown key → 400 | 20 ms | — | — |
| ✅ passed | — | polling query: assigned to me, under the epics, not done, extra clause; rendered fields; paging | 20 ms | — | — |
| ✅ passed | — | comments newest-first with rendered bodies; attachments via a 303 to the media host | 17 ms | — | — |
| ✅ passed | — | write-back: status read, transitions list, POST transition 204, invalid id 400 | 41 ms | — | — |
| ✅ passed | — | failure injection and an outage | 10 ms | — | — |
| ✅ passed | — | auth: connectionData for the PAT; a wrong PAT gets 203 + HTML like the real service | 5 ms | — | — |
| ✅ passed | — | projects, work item types with state categories, type categories | 18 ms | — | — |
| ✅ passed | — | two-step polling WIQL: hierarchy (MustContain / Recursive), then the flat filter; batch with relations | 54 ms | — | — |
| ✅ passed | — | comments (desc, deleted skipped by the runner), attachments, System.State write-back | 43 ms | — | — |
| ❌ failed | `TCLI-007` `TCLI-008` `TCLI-011` | tracker test/list: the runner signs in to the fake Jira and ADO with the seeded tokens; tokens never printed | 179 ms | — | — |
| ⏭️ skipped | `TCLI-008` | a revoked token: tracker test reports failing with Jira's advice and exits 1 | 0 ms | — | — |
| ⏭️ skipped | — | tracker add cannot target a loopback site: --site http://127.0.0.1:<port> is rewritten to https://127.0.0.1 (no port) | 0 ms | — | — |
| ⏭️ skipped | `TCLI-009` | tracker remove deletes the entry and its token | 0 ms | — | — |
| ✅ passed | — | auth status reports the signed-in accounts the way ForgeCli.SignedInAccounts parses them | 329 ms | — | — |
| ✅ passed | — | publish: pr list finds nothing → pr create prints the URL → a retry finds the open PR (idempotent) | 411 ms | — | — |
| ✅ passed | — | state: pr view --json state --jq .state follows OPEN → MERGED (ForgeCli.NormalizeState → merged) | 424 ms | — | — |
| ✅ passed | — | account scoping: auth token --user prints that account's token; GH_TOKEN reaches the call record | 326 ms | — | — |
| ✅ passed | — | a configured failure and a signed-out CLI fail like gh does | 201 ms | — | — |

## Failures

### ❌ tracker test/list: the runner signs in to the fake Jira and ADO with the seeded tokens; tokens never printed

- File: `tests/offline/runner-tracker-cli.spec.ts`
- Cases: `TCLI-007`, `TCLI-008`, `TCLI-011`
- Trace: `..\current\artifacts\offline-runner-tracker-cli-83bcb-tokens-tokens-never-printed-chromium\trace.zip` (open with `npx playwright show-trace <path>`)

```
Error: jira-fake: failing - The token for 'jira-fake' is missing from Windows Credential Manager on this machine - run `orkka-local tracker add jira --site http://127.0.0.1:59646 --email dev@e2e.test` again.

[2mexpect([22m[31mreceived[39m[2m).[22mtoBe[2m([22m[32mexpected[39m[2m) // Object.is equality[22m

Expected: [32m0[39m
Received: [31m1[39m
```
Logs: API + Worker console output of the stack at http://127.0.0.1:9; runner log .runs/runner.log

## Skipped

- a revoked token: tracker test reports failing with Jira's advice and exits 1
- tracker add cannot target a loopback site: --site http://127.0.0.1:<port> is rewritten to https://127.0.0.1 (no port)
- tracker remove deletes the entry and its token

## Cleanup

- Projects created this run: 0
- Leftovers after teardown: none
