# Run index

Every run of the harness appends one row. Reports live next to this file.

| When (local) | Suite | Target | Result | Spend | Report | Note |
|---|---|---|---|---|---|---|
| 2026-09-15 10:12 | adhoc | prod | 6/6 passed | $0.00 | [20260915-1012-adhoc](20260915-1012-adhoc/report.md) | Credentials are not an Owner (role=Member); permission-gated cases may fail. |
| 2026-09-15 10:34 | hybrid-repo-dry | prod | 3/13 passed, 5 failed, 5 skipped | $0.00 | [20260915-1034-hybrid-repo-dry](20260915-1034-hybrid-repo-dry/report.md) | Playwright overall status: failed |
| 2026-09-15 11:47 | smoke | prod | 6/6 passed | $0.00 | [20260915-1147-smoke](20260915-1147-smoke/report.md) | Credentials are not an Owner (role=Member); permission-gated cases may fail. |
| 2026-09-15 11:48 | hybrid-repo | prod | 16/16 passed | $0.00 | [20260915-1148-hybrid-repo](20260915-1148-hybrid-repo/report.md) | Credentials are not an Owner (role=Member); permission-gated cases may fail. |
| 2026-09-15 11:59 | ui-inventory | prod | 49/49 passed | $0.00 | [20260915-1159-ui-inventory](20260915-1159-ui-inventory/report.md) | Credentials are not an Owner (role=Member); permission-gated cases may fail. |
| 2026-09-15 12:05 | cloud-repo | prod | 2/2 passed | $0.48 | [20260915-1205-cloud-repo](20260915-1205-cloud-repo/report.md) | Harness API key acts as a Member with all 18 permissions (by design); Owner-only endpoints (org rename/settings, role changes, admin) use the Clerk session. |
| 2026-09-15 12:12 | hybrid-folder | prod | 0/9 passed, 9 skipped | $0.00 | [20260915-1212-hybrid-folder](20260915-1212-hybrid-folder/report.md) | Harness API key acts as a Member with all 18 permissions (by design); Owner-only endpoints (org rename/settings, role changes, admin) use the Clerk session. |
| 2026-09-15 12:12 | hybrid-hosted | prod | 0/6 passed, 6 skipped | $0.00 | [20260915-1212-hybrid-hosted](20260915-1212-hybrid-hosted/report.md) | Harness API key acts as a Member with all 18 permissions (by design); Owner-only endpoints (org rename/settings, role changes, admin) use the Clerk session. |
| 2026-10-26 22:33 | offline | offline | 17/22 passed, 2 failed, 3 skipped | — | [20261026-2233-offline](20261026-2233-offline/report.md) | Playwright overall status: failed |
| 2026-10-26 22:34 | offline | offline | 22/22 passed | — | [20261026-2234-offline](20261026-2234-offline/report.md) | No environment configured (E2E_BASE_URL unset): only offline specs ran; environment-bound specs were skipped. |
| 2026-10-26 22:40 | adhoc | offline | 22/124 passed, 102 skipped | — | [20261026-2240-adhoc](20261026-2240-adhoc/report.md) | No environment configured (E2E_BASE_URL unset): only offline specs ran; environment-bound specs were skipped. |
| 2026-10-27 00:09 | offline | offline | 18/22 passed, 1 failed, 3 skipped | — | [20261027-0009-offline](20261027-0009-offline/report.md) | Playwright overall status: failed |
| 2026-10-27 00:10 | adhoc | offline | 0/4 passed, 1 failed, 3 skipped | — | [20261027-0010-adhoc](20261027-0010-adhoc/report.md) | Playwright overall status: failed |
| 2026-10-27 00:10 | adhoc | offline | 4/4 passed | — | [20261027-0010-adhoc](20261027-0010-adhoc/report.md) | No environment configured (E2E_BASE_URL unset): only offline specs ran; environment-bound specs were skipped. |
| 2026-10-27 00:10 | offline | offline | 22/22 passed | — | [20261027-0010-offline](20261027-0010-offline/report.md) | No environment configured (E2E_BASE_URL unset): only offline specs ran; environment-bound specs were skipped. |
| 2026-11-17 17:01 | offline | offline | 23/23 passed | — | [20261117-1701-offline](20261117-1701-offline/report.md) | No environment configured (E2E_BASE_URL unset): only offline specs ran; environment-bound specs were skipped. |
| 2026-11-18 00:20 | adhoc | http://127.0.0.1:6750 | 4/7 passed, 3 skipped | — | [20261118-0020-adhoc](20261118-0020-adhoc/report.md) |  |
| 2026-11-18 00:36 | adhoc | http://127.0.0.1:6750 | 3/7 passed, 1 failed, 3 skipped | — | [20261118-0036-adhoc](20261118-0036-adhoc/report.md) | Playwright overall status: failed |
| 2026-11-18 00:37 | adhoc | http://127.0.0.1:6750 | 3/7 passed, 1 failed, 3 skipped | — | [20261118-0037-adhoc](20261118-0037-adhoc/report.md) | Playwright overall status: failed |
| 2026-11-18 00:38 | adhoc | http://127.0.0.1:6750 | 7/7 passed | — | [20261118-0038-adhoc](20261118-0038-adhoc/report.md) | E2E_KEEP_PROJECTS set: no pre-run sweep, no teardown deletes. |
| 2026-11-18 00:39 | adhoc | http://127.0.0.1:6750 | 7/7 passed | — | [20261118-0039-adhoc](20261118-0039-adhoc/report.md) | E2E_KEEP_PROJECTS set: no pre-run sweep, no teardown deletes. |
| 2026-11-18 00:55 | adhoc | http://127.0.0.1:6750 | 7/7 passed | — | [20261118-0055-adhoc](20261118-0055-adhoc/report.md) | E2E_KEEP_PROJECTS set: no pre-run sweep, no teardown deletes. |
| 2026-11-18 00:58 | smoke | http://127.0.0.1:6750 | 12/14 passed, 2 failed | — | [20261118-0058-smoke](20261118-0058-smoke/report.md) | Playwright overall status: failed |
| 2026-11-18 00:58 | adhoc | http://127.0.0.1:6750 | 0/2 passed, 2 failed | — | [20261118-0058-adhoc](20261118-0058-adhoc/report.md) | Playwright overall status: failed |
| 2026-11-18 00:58 | adhoc | http://127.0.0.1:6750 | 1/2 passed, 1 failed | — | [20261118-0058-adhoc](20261118-0058-adhoc/report.md) |  |
| 2026-11-18 00:58 | api | http://127.0.0.1:6750 | 6/15 passed, 2 failed, 7 skipped | — | [20261118-0058-api](20261118-0058-api/report.md) | Playwright overall status: failed |
| 2026-11-18 00:59 | adhoc | http://127.0.0.1:6750 | 0/2 passed, 2 failed | — | [20261118-0059-adhoc](20261118-0059-adhoc/report.md) | Playwright overall status: failed |
| 2026-11-18 00:59 | api | http://127.0.0.1:6750 | 12/15 passed, 1 failed, 2 skipped | — | [20261118-0059-api](20261118-0059-api/report.md) | Playwright overall status: failed |
| 2026-11-18 01:01 | api | http://127.0.0.1:6750 | 14/15 passed, 1 skipped | — | [20261118-0101-api](20261118-0101-api/report.md) |  |
| 2026-11-18 01:01 | workflow | http://127.0.0.1:6750 | 4/7 passed, 3 skipped | — | [20261118-0101-workflow](20261118-0101-workflow/report.md) |  |
| 2026-11-18 01:02 | offline | http://127.0.0.1:6750 | 25/25 passed | — | [20261118-0102-offline](20261118-0102-offline/report.md) | Offline run: only offline specs ran; environment-bound specs were skipped. |
| 2026-11-18 01:02 | ui-inventory | http://127.0.0.1:6750 | 37/37 passed | — | [20261118-0102-ui-inventory](20261118-0102-ui-inventory/report.md) |  |
| 2026-11-17 23:21 | workflow (live) | http://127.0.0.1:6750 | all 8 steps passed after 11 product + 6 harness fixes | 3 agent runs (Haiku) | [20261117-2321-workflow-live](20261117-2321-workflow-live/report.md) | Live step-by-step run of tests/local/workflow.spec.ts with real agents; fixes in orkka-api/orkka-ui (see report) |
| 2026-11-18 13:23 | offline | http://127.0.0.1:6792 | 25/25 passed | — | [20261118-1323-offline](20261118-1323-offline/report.md) | Offline run: only offline specs ran; environment-bound specs were skipped. |
| 2026-11-18 13:23 | smoke | http://127.0.0.1:6792 | 13/14 passed, 1 failed | — | [20261118-1323-smoke](20261118-1323-smoke/report.md) |  |
| 2026-11-18 13:23 | api | http://127.0.0.1:6792 | 13/15 passed, 2 skipped | — | [20261118-1323-api](20261118-1323-api/report.md) |  |
| 2026-11-18 13:23 | ui-inventory | http://127.0.0.1:6792 | 37/37 passed | — | [20261118-1323-ui-inventory](20261118-1323-ui-inventory/report.md) |  |
| 2026-11-18 13:24 | api | http://127.0.0.1:6792 | 14/15 passed, 1 skipped | — | [20261118-1324-api](20261118-1324-api/report.md) |  |
| 2026-11-18 13:24 | workflow | http://127.0.0.1:6792 | 4/7 passed, 3 skipped | — | [20261118-1324-workflow](20261118-1324-workflow/report.md) |  |
| 2026-11-18 13:29 | workflow | http://127.0.0.1:6792 | 3/5 passed, 1 failed, 1 skipped | — | [20261118-1329-workflow](20261118-1329-workflow/report.md) | Playwright overall status: failed |
| 2026-11-18 13:43 | workflow | http://127.0.0.1:6792 | 3/5 passed, 1 failed, 1 skipped | — | [20261118-1343-workflow](20261118-1343-workflow/report.md) | Playwright overall status: failed |
| 2026-11-18 14:14 | offline | offline | 25/25 passed | — | [20261118-1414-offline](20261118-1414-offline/report.md) | Offline run: only offline specs ran; environment-bound specs were skipped. |
| 2026-11-18 14:14 | workflow | http://127.0.0.1:6792 | 5/5 passed | — | [20261118-1414-workflow](20261118-1414-workflow/report.md) | Offline run: only offline specs ran; environment-bound specs were skipped. |
| 2026-11-18 14:28 | screens | http://127.0.0.1:6792 | 1/8 passed, 1 failed, 6 skipped | — | [20261118-1428-screens](20261118-1428-screens/report.md) | Playwright overall status: failed |
| 2026-11-18 14:28 | screens | http://127.0.0.1:6792 | 8/8 passed | — | [20261118-1428-screens](20261118-1428-screens/report.md) | E2E_KEEP_PROJECTS set: no pre-run sweep, no teardown deletes. |
| 2026-11-18 14:32 | screens | http://127.0.0.1:6792 | 8/8 passed | — | [20261118-1432-screens](20261118-1432-screens/report.md) | E2E_KEEP_PROJECTS set: no pre-run sweep, no teardown deletes. |
| 2026-11-18 14:33 | smoke | http://127.0.0.1:6792 | 13/14 passed, 1 failed | — | [20261118-1433-smoke](20261118-1433-smoke/report.md) | Pre-run sweep left 2 recent e2e project(s) alone (possible concurrent run): e2e-shop-ado-mwme00e2, e2e-shop-mwmdsqt5 |
| 2026-11-18 14:33 | api | http://127.0.0.1:6792 | 13/15 passed, 2 skipped | — | [20261118-1433-api](20261118-1433-api/report.md) |  |
| 2026-11-18 14:33 | ui-inventory | http://127.0.0.1:6792 | 37/37 passed | — | [20261118-1433-ui-inventory](20261118-1433-ui-inventory/report.md) |  |
| 2026-11-18 14:34 | api | http://127.0.0.1:6792 | 14/15 passed, 1 skipped | — | [20261118-1434-api](20261118-1434-api/report.md) |  |
| 2026-11-18 13:23 | run-from-source (R5) | http://127.0.0.1:6792 | all suites green on the final build; 5 product + 4 harness fixes | Story ×2 + 1 stopped retry (Haiku, ≈$1.85) | [20261118-1323-run-from-source](20261118-1323-run-from-source/report.md) | one-process orkka.dll sandbox; loose ends a–f |
| 2026-11-18 16:23 | offline | offline | 22/27 passed, 5 skipped | — | [20261118-1623-offline](20261118-1623-offline/report.md) | Offline run: only offline specs ran; environment-bound specs were skipped. |
| 2026-11-18 16:24 | offline | offline | 27/27 passed | — | [20261118-1624-offline](20261118-1624-offline/report.md) | Offline run: only offline specs ran; environment-bound specs were skipped. |
| 2026-11-18 17:10 | offline | offline | 22/27 passed, 5 skipped | — | [20261118-1710-offline](20261118-1710-offline/report.md) | Offline run: only offline specs ran; environment-bound specs were skipped. |