# Run 20261027-0010-adhoc

| | |
|---|---|
| Suite | `adhoc` |
| Target | offline |
| Started | 2026-10-27T03:10:39.130Z |
| Duration | 5.1 s |
| Result | 4 passed · 0 failed · 0 timed out · 0 skipped |
| Operator | claude · harness uncommitted · node v24.18.0 · playwright 1.63.0 |

## Notes
- No environment configured (E2E_BASE_URL unset): only offline specs ran; environment-bound specs were skipped.

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ✅ passed | `TCLI-007` `TCLI-008` `TCLI-011` | tracker test/list: the runner signs in to the fake Jira and ADO with the seeded tokens; tokens never printed | 660 ms | — | — |
| ✅ passed | `TCLI-008` | a revoked token: tracker test reports failing with Jira's advice and exits 1 | 253 ms | — | — |
| ✅ passed | `TCLI-001` | tracker add reaches a loopback site only with the runner's test opt-ins | 2.7 s | — | — |
| ✅ passed | `TCLI-009` | tracker remove deletes the entry and its token | 327 ms | — | — |

## Cleanup

- Projects created this run: 0
- Leftovers after teardown: none
