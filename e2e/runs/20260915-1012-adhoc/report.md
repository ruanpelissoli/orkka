# Run 20260915-1012-adhoc

| | |
|---|---|
| Suite | `adhoc` |
| Target | prod |
| Started | 2026-09-15T13:12:37.468Z |
| Duration | 20.1 s |
| Result | 6 passed · 0 failed · 0 timed out · 0 skipped |
| Spend | $0.00 of $5.00 cap (credits 0 → 0) |
| Operator | claude · harness uncommitted · node v24.18.0 · playwright 1.63.0 |

## Notes
- Credentials are not an Owner (role=Member); permission-gated cases may fail.
- Org plan has no team features (not Enterprise); Members cases will be skipped.

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ✅ passed | `KEY-001` `NEG-002` | API: org/me answers for the harness key and matches the expected org @smoke | 1.0 s | $0.000 | — |
| ✅ passed | `BIL-001` | API: credits balance and usage summary are readable @smoke | 664 ms | $0.000 | — |
| ✅ passed | `CLD-001` | API: models catalogue lists at least one enabled cloud model @smoke | 600 ms | $0.000 | — |
| ✅ passed | `RUN-001` | API: runner presence endpoint answers @smoke | 480 ms | $0.000 | — |
| ✅ passed | `NAV-001` `ONB-001` | UI: dashboard loads for the saved session and shows the shell @smoke | 3.8 s | $0.000 | — |
| ✅ passed | `SET-001` | UI: project settings page renders for the first project (or onboarding when none) @smoke | 3.0 s | $0.000 | — |

## Cleanup

- Projects created this run: 0
- Leftovers after teardown: none
