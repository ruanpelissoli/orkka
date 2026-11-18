# Run 20261118-0058-smoke

| | |
|---|---|
| Suite | `smoke` |
| Target | http://127.0.0.1:6750 |
| Started | 2026-11-18T03:58:27.103Z |
| Duration | 5.6 s |
| Result | 12 passed · 2 failed · 0 timed out · 0 skipped |
| Operator | claude · harness 7c790e44baab · node v24.18.0 · playwright 1.63.0 |

## Notes
- Playwright overall status: failed
- Pre-run sweep deleted 1 stale e2e project(s): e2e-shop
- Pre-run sweep left 1 recent e2e project(s) alone (possible concurrent run): e2e-shop-ado

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ❌ failed | `NEG-030` | NEG-030 a project whose slug is exactly 26 characters must still resolve by slug (ULID-length regression) | 135 ms | — | e2e-neg-20261118-0058faxt9 |
| ❌ failed | `NEG-031` | NEG-031 the removed cloud-execution surface is gone: routes 404 | 80 ms | — | e2e-neg-20261118-0058-dk5kc |
| ✅ passed | — | an unknown project id answers 404, not 403 | 23 ms | — | — |
| ✅ passed | — | API keys cannot mint API keys | 25 ms | — | — |
| ✅ passed | — | health endpoint answers anonymously | 17 ms | — | — |
| ✅ passed | — | the local host refuses a foreign Host header (DNS rebinding) with 421 | 17 ms | — | — |
| ✅ passed | — | a cookie-authenticated write without Origin + X-Orkka-Csrf is refused | 839 ms | — | — |
| ✅ passed | — | API: org/me answers for the harness key and matches the expected workspace @smoke | 17 ms | — | — |
| ✅ passed | `WSP-001` | API: workspaces list answers with exactly one active workspace @smoke | 6 ms | — | — |
| ✅ passed | — | API: runner presence answers, with forge CLI status when online @smoke | 16 ms | — | — |
| ✅ passed | `TSRC-009` | API: tracker connections list answers (possibly empty) @smoke | 11 ms | — | — |
| ✅ passed | `LWF-001` | UI: the launch-code session opens the dashboard through the host's front door @smoke | 816 ms | — | — |
| ✅ passed | `LWF-001` | UI: a browser without the session gets the signed-out page, not the app @smoke | 157 ms | — | — |
| ✅ passed | `SET-012` | UI: project settings page renders for the first project (or onboarding when none) @smoke | 634 ms | — | — |

## Failures

### ❌ NEG-030 a project whose slug is exactly 26 characters must still resolve by slug (ULID-length regression)

- File: `tests/smoke/negatives.spec.ts`
- Cases: `NEG-030`
- Projects: `01M7PCWPD8B2ZN39H1AK9AA5GW e2e-neg-20261118-0058faxt9`

```
Error: project reachable by its 26-char slug

[2mexpect([22m[31mreceived[39m[2m).[22mnot[2m.[22mtoBeInstanceOf[2m([22m[32mexpected[39m[2m)[22m

Expected constructor: not [32mApiError[39m

```
Logs: API + Worker console output of the stack at http://127.0.0.1:6750; runner log .runs/runner.log (grep for 01M7PCWPD8B2ZN39H1AK9AA5GW)

### ❌ NEG-031 the removed cloud-execution surface is gone: routes 404

- File: `tests/smoke/negatives.spec.ts`
- Cases: `NEG-031`
- Projects: `01M7PCWPH0KMW2D956YY7117H9 e2e-neg-20261118-0058-dk5kc`
- Trace: `..\current\artifacts\smoke-negatives-cross-cutt-5d007--surface-is-gone-routes-404-chromium\trace.zip` (open with `npx playwright show-trace <path>`)

```
ApiError: PUT /api/me/execution-preference → 404: (empty body)
```
Logs: API + Worker console output of the stack at http://127.0.0.1:6750; runner log .runs/runner.log (grep for 01M7PCWPH0KMW2D956YY7117H9)

## Cleanup

- Projects created this run: 3
  - `e2e-neg-20261118-0058faxt9` (01M7PCWPD8B2ZN39H1AK9AA5GW)
  - `e2e-neg-20261118-0058faxt9` (01M7PCWPD8B2ZN39H1AK9AA5GW)
  - `e2e-neg-20261118-0058-dk5kc` (01M7PCWPH0KMW2D956YY7117H9)
- Leftovers after teardown: none
