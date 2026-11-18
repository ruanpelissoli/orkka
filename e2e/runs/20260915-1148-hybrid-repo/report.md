# Run 20260915-1148-hybrid-repo

| | |
|---|---|
| Suite | `hybrid-repo` |
| Target | prod |
| Started | 2026-09-15T14:48:14.866Z |
| Duration | 9m 53s |
| Result | 16 passed · 0 failed · 0 timed out · 0 skipped |
| Spend | $0.00 of $5.00 cap (credits 3294 → 3294) |
| Operator | claude · harness uncommitted · node v24.18.0 · playwright 1.63.0 |

## Notes
- Credentials are not an Owner (role=Member); permission-gated cases may fail.
- Org plan has no team features (not Enterprise); Members cases will be skipped.

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ✅ passed | `HYB-010` `HYB-011` `HYB-012` `HYB-014` `PR-010` `TSK-010` | HYB-010 hands-free task → Done: PR merged into the fixture repo, $0 spend | 4m 31s | $0.000 | e2e-hyb-20260915-1148-zqgnj<br>e2e-hyb-20260915-1148-zqgnj |
| ✅ passed | `HYB-020` `PLN-001` `PLN-003` `PLN-004` `PR-005` `PR-006` | HYB-020 human loop: plan parks in PlanReady, approval resumes, PR review request_changes then approve | 4m 34s | $0.000 | e2e-hyb-20260915-1152-7n565<br>e2e-hyb-20260915-1152-7n565 |
| ✅ passed | `HYB-030` `TSK-030` | HYB-030 a task created while preferring the runner shows its target and can be swapped before start | 2.6 s | $0.000 | e2e-hyb-20260915-1157-2fxk9<br>e2e-hyb-20260915-1157-2fxk9 |
| ✅ passed | `HYB-001` | HYB-001 execution preference can be set to MyRunner and read back @hybrid-repo @smoke | 762 ms | $0.000 | — |
| ✅ passed | `HYB-002` | HYB-002 unknown execution preference target is rejected with 400 | 472 ms | $0.000 | — |
| ✅ passed | `HYB-003` `ONB-020` | HYB-003 create an unmanaged project via API: Ready immediately, key and slug assigned | 1.4 s | $0.000 | e2e-hyb-20260915-1157-srf2c<br>e2e-hyb-20260915-1157-srf2c |
| ✅ passed | `HYB-004` `REPO-001` `REPO-002` | HYB-004 link the GitHub-App fixture repo and see it on the Repos page | 4.8 s | $0.000 | e2e-hyb-20260915-1157-fujjc<br>e2e-hyb-20260915-1157-fujjc |
| ✅ passed | `HYB-005` | HYB-005 repo access preflight answers for the linked repo on the runner | 2.8 s | $0.000 | e2e-hyb-20260915-1157-dyq8x<br>e2e-hyb-20260915-1157-dyq8x |
| ✅ passed | `HYB-006` | HYB-006 execution-target preview says a Claude task runs on the runner for $0 | 1.7 s | $0.000 | e2e-hyb-20260915-1157-c9azq<br>e2e-hyb-20260915-1157-c9azq |
| ✅ passed | `REPO-006` `REPO-007` | HYB-007 repo file browser serves the fixture's features file from main | 2.2 s | $0.000 | e2e-hyb-20260915-1157-gxcxa<br>e2e-hyb-20260915-1157-gxcxa |
| ✅ passed | `HYB-046` `SET-040` | HYB-008 deleting the project removes it (404 afterwards) and leaves the GitHub repo untouched | 1.9 s | $0.000 | e2e-hyb-20260915-1157-jnfxw<br>e2e-hyb-20260915-1157-jnfxw |
| ✅ passed | `RUN-002` `RUN-003` | RUN-002 Runner settings page shows the runner online with its machine and providers | 2.7 s | $0.000 | — |
| ✅ passed | `BRD-001` `BRD-002` `NAV-002` | BRD-001 board renders the nine lanes and the new-task entry point for a fresh project | 3.3 s | $0.000 | e2e-hyb-20260915-1157-r8rky<br>e2e-hyb-20260915-1157-r8rky |
| ✅ passed | `TSK-001` `TSK-002` `TSK-011` `BRD-010` | TSK-001 create a task from the form as a draft and see it on the board and the detail page | 7.1 s | $0.000 | e2e-hyb-20260915-1157-knrp8<br>e2e-hyb-20260915-1157-knrp8 |
| ✅ passed | `TSK-012` `HYB-013` | TSK-012 task detail shows the runner execution badge for a UserRunner task | 4.8 s | $0.000 | e2e-hyb-20260915-1157-wp4m8<br>e2e-hyb-20260915-1157-wp4m8 |
| ✅ passed | `SET-010` `SET-011` `SET-012` | SET-010 project settings: pipeline toggles persist through the UI and the API sees them | 4.9 s | $0.000 | e2e-hyb-20260915-1158-zvhe8<br>e2e-hyb-20260915-1158-zvhe8 |

## Cleanup

- Projects created this run: 13
  - Unmanaged `e2e-hyb-20260915-1148-zqgnj` (01M2JRMKEDY5TVEN9NCDM9D0DH)
  - Unmanaged `e2e-hyb-20260915-1152-7n565` (01M2JRWV1FEM9623H25D38BGFH)
  - Unmanaged `e2e-hyb-20260915-1157-2fxk9` (01M2JS56HVMZ86C3VBTEEJ1PMZ)
  - Unmanaged `e2e-hyb-20260915-1157-srf2c` (01M2JS5A0D73556JX38PW12WRB)
  - Unmanaged `e2e-hyb-20260915-1157-fujjc` (01M2JS5BH6G6D6R01ZYS667PJ6)
  - Unmanaged `e2e-hyb-20260915-1157-dyq8x` (01M2JS5G6P9E6XZCSF1JJYX6AG)
  - Unmanaged `e2e-hyb-20260915-1157-c9azq` (01M2JS5K1VYQ7PZFN1D28NFEPT)
  - Unmanaged `e2e-hyb-20260915-1157-gxcxa` (01M2JS5MDT0Y1GE3KSFGC1SVH9)
  - Unmanaged `e2e-hyb-20260915-1157-jnfxw` (01M2JS5PHG6FVG8QBWTGQ4JN6V)
  - Unmanaged `e2e-hyb-20260915-1157-r8rky` (01M2JS5V4ZTH1R06WS5VE2SGCF)
  - Unmanaged `e2e-hyb-20260915-1157-knrp8` (01M2JS5YD6T8ATSKG70XTTDBVR)
  - Unmanaged `e2e-hyb-20260915-1157-wp4m8` (01M2JS65NMKA9KAPXYQF86RGXB)
  - Unmanaged `e2e-hyb-20260915-1158-zvhe8` (01M2JS6A06AP409M7WZMH1XXKJ)
- Leftovers after teardown: none
