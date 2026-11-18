# Run 20260915-1205-cloud-repo

| | |
|---|---|
| Suite | `cloud-repo` |
| Target | prod |
| Started | 2026-09-15T15:05:28.394Z |
| Duration | 6m 9s |
| Result | 2 passed · 0 failed · 0 timed out · 0 skipped |
| Spend | $0.48 of $3.00 cap (credits 3294 → 2814) |
| Operator | claude · harness uncommitted · node v24.18.0 · playwright 1.63.0 |

## Notes
- Harness API key acts as a Member with all 18 permissions (by design); Owner-only endpoints (org rename/settings, role changes, admin) use the Clerk session.
- Org plan has no team features (not Enterprise); Members cases will be skipped.

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ✅ passed | `CLD-002` `CLD-003` `CLD-005` `CLD-006` `BIL-003` | CLD-002 preference Cloud: a task on the fixture repo runs in Orkka's cloud and merges; usage rows appear | 6m 0s | $0.480 | e2e-cld-20260915-1205-x92qg |
| ✅ passed | `CLD-010` `HYB-006` | CLD-010 execution-target preview under Cloud preference says Cloud with CloudPreference | 1.7 s | $0.000 | e2e-cld-20260915-1211-8f86n |

## Cleanup

- Projects created this run: 2
  - Unmanaged `e2e-cld-20260915-1205-x92qg` (01M2JSM52B5TYVP5EJ9A21KJ6Y)
  - Unmanaged `e2e-cld-20260915-1211-8f86n` (01M2JSZ41QKB7HQX3HPQ8MKME1)
- Leftovers after teardown: none
