# Run 20261118-1323-smoke

| | |
|---|---|
| Suite | `smoke` |
| Target | http://127.0.0.1:6792 |
| Started | 2026-11-18T16:23:31.131Z |
| Duration | 4.4 s |
| Result | 13 passed · 1 failed · 0 timed out · 0 skipped |
| Operator | claude · harness uncommitted · node v24.18.0 · playwright 1.63.0 |

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ❌ failed | `NEG-030` | NEG-030 a project whose slug is exactly 26 characters must still resolve by slug (ULID-length regression) | 208 ms | — | e2e-neg-20261118-1323xrz9v |
| ✅ passed | `NEG-031` | NEG-031 the removed cloud-execution surface is gone: routes 404 | 137 ms | — | e2e-neg-20261118-1323-zaet8 |
| ✅ passed | — | an unknown project id answers 404, not 403 | 23 ms | — | — |
| ✅ passed | — | API keys cannot mint API keys | 21 ms | — | — |
| ✅ passed | — | health endpoint answers anonymously | 22 ms | — | — |
| ✅ passed | — | the local host refuses a foreign Host header (DNS rebinding) with 421 | 12 ms | — | — |
| ✅ passed | — | a cookie-authenticated write without Origin + X-Orkka-Csrf is refused | 601 ms | — | — |
| ✅ passed | — | API: org/me answers for the harness key and matches the expected workspace @smoke | 17 ms | — | — |
| ✅ passed | `WSP-001` | API: workspaces list answers with exactly one active workspace @smoke | 10 ms | — | — |
| ✅ passed | — | API: runner presence answers, with forge CLI status when online @smoke | 12 ms | — | — |
| ✅ passed | `TSRC-009` | API: tracker connections list answers (possibly empty) @smoke | 16 ms | — | — |
| ✅ passed | `LWF-001` | UI: the launch-code session opens the dashboard through the host's front door @smoke | 513 ms | — | — |
| ✅ passed | `LWF-001` | UI: a browser without the session gets the signed-out page, not the app @smoke | 173 ms | — | — |
| ✅ passed | `SET-012` | UI: project settings page renders for the first project (or onboarding when none) @smoke | 744 ms | — | — |

## Failures

### ❌ NEG-030 a project whose slug is exactly 26 characters must still resolve by slug (ULID-length regression)

- File: `tests/smoke/negatives.spec.ts`
- Cases: `NEG-030`
- Projects: `01M7QQGYJP3MQ8V3GAQCDQ8JZY e2e-neg-20261118-1323xrz9v`

```
Error: project reachable by its 26-char slug

[2mexpect([22m[31mreceived[39m[2m).[22mnot[2m.[22mtoBeInstanceOf[2m([22m[32mexpected[39m[2m)[22m

Expected constructor: not [32mApiError[39m

```
Logs: API + Worker console output of the stack at http://127.0.0.1:6792; runner log .runs/runner.log (grep for 01M7QQGYJP3MQ8V3GAQCDQ8JZY)

## Cleanup

- Projects created this run: 3
  - `e2e-neg-20261118-1323xrz9v` (01M7QQGYJP3MQ8V3GAQCDQ8JZY)
  - `e2e-neg-20261118-1323xrz9v` (01M7QQGYJP3MQ8V3GAQCDQ8JZY)
  - `e2e-neg-20261118-1323-zaet8` (01M7QQGYQVM1J3M6DM99887MV6)
- Leftovers after teardown: none
