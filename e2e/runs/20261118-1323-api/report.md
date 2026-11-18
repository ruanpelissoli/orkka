# Run 20261118-1323-api

| | |
|---|---|
| Suite | `api` |
| Target | http://127.0.0.1:6792 |
| Started | 2026-11-18T16:23:39.826Z |
| Duration | 2.7 s |
| Result | 13 passed · 0 failed · 0 timed out · 2 skipped |
| Operator | claude · harness uncommitted · node v24.18.0 · playwright 1.63.0 |

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ✅ passed | `TSRC-001` | TSRC-001 a project with no ticket source answers 204 | 153 ms | — | e2e-tsrc-20261118-1323-5adt |
| ✅ passed | `TSRC-002` | TSRC-002 PUT with a connection that is not mine → 400 on trackerConnectionId | 198 ms | — | e2e-tsrc-20261118-1323-35yb |
| ✅ passed | `TSRC-003` | TSRC-003 validation: poll interval, branch template, epics, subfolder, scope | 250 ms | — | e2e-tsrc-20261118-1323-uwkg |
| ✅ passed | `TSRC-005` | TSRC-005 DELETE without a ticket source is a no-op 204; GET stays 204 | 105 ms | — | e2e-tsrc-20261118-1323-7wbj |
| ✅ passed | `TSRC-007` | TSRC-007 tracker query with an unknown kind → 400 | 11 ms | — | — |
| ✅ passed | `TSRC-008` | TSRC-008 tracker query on an unknown connection → 404 (not 403) | 22 ms | — | — |
| ✅ passed | `TSRC-009` | TSRC-009 GET /api/trackers/connections lists my connections with metadata only | 19 ms | — | — |
| ⏭️ skipped | `TSRC-006` | TSRC-006 a query while my runner is offline answers supported:false, not an error | 25 ms | — | — |
| ✅ passed | `WSP-001` | WSP-001 GET /api/me/workspaces lists my workspaces with exactly one active | 32 ms | — | — |
| ✅ passed | `WSP-003` | WSP-003 switching the active workspace answers 204; an unknown org answers 404 | 46 ms | — | — |
| ✅ passed | `WSP-004` | WSP-004 a non-ULID X-Orkka-Workspace answers 400 Invalid workspace. | 7 ms | — | — |
| ✅ passed | `WSP-005` | WSP-005 naming a workspace I am not in answers 403 | 11 ms | — | — |
| ⏭️ skipped | `WSP-006` | WSP-006 the same call in two workspaces returns disjoint projects | 27 ms | — | — |
| ✅ passed | `WSP-007` | WSP-007 an API key ignores X-Orkka-Workspace and stays in its own org | 23 ms | — | — |
| ✅ passed | `WSP-002` | WSP-002 creating a workspace makes it mine and active (restores the previous active one) | 90 ms | — | — |

## Skipped

- TSRC-006 a query while my runner is offline answers supported:false, not an error — the runner is online — stop it (npm run runner:status) to exercise this case
- WSP-006 the same call in two workspaces returns disjoint projects — only one workspace exists (WSP-002 creates a second one)

## Cleanup

- Projects created this run: 4
  - `e2e-tsrc-20261118-1323-5adt` (01M7QQH6YVD1FDTG69PQRXRYYG)
  - `e2e-tsrc-20261118-1323-35yb` (01M7QQH73N4H909N3Y36K9WJ4Y)
  - `e2e-tsrc-20261118-1323-uwkg` (01M7QQH7A2NYSSV9VWKX077PZG)
  - `e2e-tsrc-20261118-1323-7wbj` (01M7QQH7J1T8VFTWSFA99W243S)
- Leftovers after teardown: none
