# Run 20261119-1459-smoke

| | |
|---|---|
| Suite | `smoke` |
| Target | http://127.0.0.1:6760 |
| Started | 2026-11-19T17:59:20.515Z |
| Duration | 7.1 s |
| Result | 14 passed · 0 failed · 0 timed out · 0 skipped |
| Operator | claude · harness uncommitted · node v24.18.0 · playwright 1.63.0 |

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ✅ passed | `NEG-030` | NEG-030 a project whose slug is exactly 26 characters must still resolve by slug (ULID-length regression) | 400 ms | — | e2e-neg-20261119-14597mryd |
| ✅ passed | `NEG-031` | NEG-031 the removed cloud-execution surface is gone: routes 404 | 118 ms | — | e2e-neg-20261119-1459-rj7ab |
| ✅ passed | — | an unknown project id answers 404, not 403 | 37 ms | — | — |
| ✅ passed | — | API keys cannot mint API keys | 26 ms | — | — |
| ✅ passed | — | health endpoint answers anonymously | 17 ms | — | — |
| ✅ passed | — | the local host refuses a foreign Host header (DNS rebinding) with 421 | 12 ms | — | — |
| ✅ passed | — | a cookie-authenticated write without Origin + X-Orkka-Csrf is refused | 999 ms | — | — |
| ✅ passed | — | API: org/me answers for the harness key and matches the expected workspace @smoke | 15 ms | — | — |
| ✅ passed | `WSP-001` | API: workspaces list answers with exactly one active workspace @smoke | 32 ms | — | — |
| ✅ passed | — | API: runner presence answers, with forge CLI status when online @smoke | 14 ms | — | — |
| ✅ passed | `TSRC-009` | API: tracker connections list answers (possibly empty) @smoke | 36 ms | — | — |
| ✅ passed | `LWF-001` | UI: the launch-code session opens the dashboard through the host's front door @smoke | 904 ms | — | — |
| ✅ passed | `LWF-001` | UI: a browser without the session gets the signed-out page, not the app @smoke | 287 ms | — | — |
| ✅ passed | `SET-012` | UI: project settings page renders for the first project (or onboarding when none) @smoke | 1.1 s | — | — |

## Cleanup

- Projects created this run: 3
  - `e2e-neg-20261119-14597mryd` (01M7TFD5B5XZWYH4VCKMJWX8BJ)
  - `e2e-neg-20261119-14597mryd` (01M7TFD5B5XZWYH4VCKMJWX8BJ)
  - `e2e-neg-20261119-1459-rj7ab` (01M7TFD5M0JHPWEJP5NWZ02YG4)
- Leftovers after teardown: none
