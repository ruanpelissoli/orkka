# Run 20260915-1212-hybrid-hosted

| | |
|---|---|
| Suite | `hybrid-hosted` |
| Target | prod |
| Started | 2026-09-15T15:12:52.377Z |
| Duration | 9.8 s |
| Result | 0 passed · 0 failed · 0 timed out · 6 skipped |
| Spend | $0.00 of $5.00 cap (credits 2814 → 2814) |
| Operator | claude · harness uncommitted · node v24.18.0 · playwright 1.63.0 |

## Notes
- Harness API key acts as a Member with all 18 permissions (by design); Owner-only endpoints (org rename/settings, role changes, admin) use the Clerk session.
- Org plan has no team features (not Enterprise); Members cases will be skipped.

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ⏭️ skipped | `HOS-001` `HOS-002` | HOS-001 start a brief: project in Briefing with an app slug reserved | 1.6 s | $0.000 | — |
| ⏭️ skipped | `HOS-004` `HOS-005` `HOS-010` `HOS-012` | HOS-004 chat with the PO agent over SSE until the brief is ready, then skip design and finalize | 318 ms | $0.000 | — |
| ⏭️ skipped | `HOS-020` `HOS-021` `HOS-025` | HOS-020 provisioning runs every step and the project becomes Ready with a live URL | 303 ms | $0.000 | — |
| ⏭️ skipped | `HOS-030` `SET-020` | HOS-030 a managed project defaults to simplified settings and publishes automatically | 575 ms | $0.000 | — |
| ⏭️ skipped | `HOS-036` `HOS-037` `DEP-001` | HOS-036 a task on the hosted app runs on the user's runner (managed-git) and deploys | 445 ms | $0.000 | — |
| ⏭️ skipped | `HOS-050` `HOS-051` | HOS-050 deleting the hosted app deprovisions it and frees the trial slot | 316 ms | $0.000 | — |

## Skipped

- HOS-001 start a brief: project in Briefing with an app slug reserved — hosted trial slot is occupied by another account — delete it or add a card before running hosted suites
- HOS-004 chat with the PO agent over SSE until the brief is ready, then skip design and finalize — no briefing project
- HOS-020 provisioning runs every step and the project becomes Ready with a live URL — no project
- HOS-030 a managed project defaults to simplified settings and publishes automatically — no project
- HOS-036 a task on the hosted app runs on the user's runner (managed-git) and deploys — no project
- HOS-050 deleting the hosted app deprovisions it and frees the trial slot — no project

## Cleanup

- Projects created this run: 0
- Leftovers after teardown: none
