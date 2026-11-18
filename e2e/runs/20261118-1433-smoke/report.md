# Run 20261118-1433-smoke

| | |
|---|---|
| Suite | `smoke` |
| Target | http://127.0.0.1:6792 |
| Started | 2026-11-18T17:33:14.744Z |
| Duration | 4.2 s |
| Result | 13 passed · 1 failed · 0 timed out · 0 skipped |
| Operator | claude · harness uncommitted · node v24.18.0 · playwright 1.63.0 |

## Notes
- Pre-run sweep left 2 recent e2e project(s) alone (possible concurrent run): e2e-shop-ado-mwme00e2, e2e-shop-mwmdsqt5

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ❌ failed | `NEG-030` | NEG-030 a project whose slug is exactly 26 characters must still resolve by slug (ULID-length regression) | 167 ms | — | e2e-neg-20261118-143348ngc |
| ✅ passed | `NEG-031` | NEG-031 the removed cloud-execution surface is gone: routes 404 | 129 ms | — | e2e-neg-20261118-1433-azdad |
| ✅ passed | — | an unknown project id answers 404, not 403 | 10 ms | — | — |
| ✅ passed | — | API keys cannot mint API keys | 13 ms | — | — |
| ✅ passed | — | health endpoint answers anonymously | 7 ms | — | — |
| ✅ passed | — | the local host refuses a foreign Host header (DNS rebinding) with 421 | 6 ms | — | — |
| ✅ passed | — | a cookie-authenticated write without Origin + X-Orkka-Csrf is refused | 579 ms | — | — |
| ✅ passed | — | API: org/me answers for the harness key and matches the expected workspace @smoke | 14 ms | — | — |
| ✅ passed | `WSP-001` | API: workspaces list answers with exactly one active workspace @smoke | 10 ms | — | — |
| ✅ passed | — | API: runner presence answers, with forge CLI status when online @smoke | 11 ms | — | — |
| ✅ passed | `TSRC-009` | API: tracker connections list answers (possibly empty) @smoke | 10 ms | — | — |
| ✅ passed | `LWF-001` | UI: the launch-code session opens the dashboard through the host's front door @smoke | 496 ms | — | — |
| ✅ passed | `LWF-001` | UI: a browser without the session gets the signed-out page, not the app @smoke | 156 ms | — | — |
| ✅ passed | `SET-012` | UI: project settings page renders for the first project (or onboarding when none) @smoke | 793 ms | — | — |

## Failures

### ❌ NEG-030 a project whose slug is exactly 26 characters must still resolve by slug (ULID-length regression)

- File: `tests/smoke/negatives.spec.ts`
- Cases: `NEG-030`
- Projects: `01M7QVGKZ5CDY4BT3451JV24DX e2e-neg-20261118-143348ngc`

```
Error: project reachable by its 26-char slug

[2mexpect([22m[31mreceived[39m[2m).[22mnot[2m.[22mtoBeInstanceOf[2m([22m[32mexpected[39m[2m)[22m

Expected constructor: not [32mApiError[39m

```
Logs: API + Worker console output of the stack at http://127.0.0.1:6792; runner log .runs/runner.log (grep for 01M7QVGKZ5CDY4BT3451JV24DX)

## Cleanup

- Projects created this run: 3
  - `e2e-neg-20261118-143348ngc` (01M7QVGKZ5CDY4BT3451JV24DX)
  - `e2e-neg-20261118-143348ngc` (01M7QVGKZ5CDY4BT3451JV24DX)
  - `e2e-neg-20261118-1433-azdad` (01M7QVGM3QX7KY0N0ZMXBPB7BB)
- Leftovers after teardown: none
