# Run 20261119-1452-api

| | |
|---|---|
| Suite | `api` |
| Target | http://127.0.0.1:6760 |
| Started | 2026-11-19T17:52:25.320Z |
| Duration | 4.7 s |
| Result | 19 passed · 0 failed · 0 timed out · 1 skipped |
| Operator | claude · harness uncommitted · node v24.18.0 · playwright 1.63.0 |

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ✅ passed | `PSU-002` | PSU-002 a new project starts with its setup open | 145 ms | — | e2e-psu-20261119-1452-wfjb8 |
| ✅ passed | `PSU-003` | PSU-003 steps are confirmed and taken back; an unknown step is refused | 143 ms | — | e2e-psu-20261119-1452-9xw3f |
| ✅ passed | `PSU-011` | PSU-011 Start records the start once and confirms every step | 118 ms | — | e2e-psu-20261119-1452-kpbt5 |
| ✅ passed | `PSU-006` | PSU-006 the command check: nothing to vet without repository commands | 202 ms | — | e2e-psu-20261119-1452-d27ak |
| ✅ passed | `PSU-014` | PSU-014 the dry run needs a saved ticket source | 133 ms | — | e2e-psu-20261119-1452-4n464 |
| ✅ passed | `TSRC-001` | TSRC-001 a project with no ticket source answers 204 | 133 ms | — | e2e-tsrc-20261119-1452-4qm3 |
| ✅ passed | `TSRC-002` | TSRC-002 PUT with a connection that is not mine → 400 on trackerConnectionId | 198 ms | — | e2e-tsrc-20261119-1452-m6na |
| ✅ passed | `TSRC-003` | TSRC-003 validation: poll interval, branch template, epics, subfolder, scope | 276 ms | — | e2e-tsrc-20261119-1452-5by6 |
| ✅ passed | `TSRC-005` | TSRC-005 DELETE without a ticket source is a no-op 204; GET stays 204 | 96 ms | — | e2e-tsrc-20261119-1452-599y |
| ✅ passed | `TSRC-007` | TSRC-007 tracker query with an unknown kind → 400 | 30 ms | — | — |
| ✅ passed | `TSRC-008` | TSRC-008 tracker query on an unknown connection → 404 (not 403) | 27 ms | — | — |
| ✅ passed | `TSRC-009` | TSRC-009 GET /api/trackers/connections lists my connections with metadata only | 16 ms | — | — |
| ⏭️ skipped | `TSRC-006` | TSRC-006 a query while my runner is offline answers supported:false, not an error | 16 ms | — | — |
| ✅ passed | `WSP-001` | WSP-001 GET /api/me/workspaces lists my workspaces with exactly one active | 35 ms | — | — |
| ✅ passed | `WSP-003` | WSP-003 switching the active workspace answers 204; an unknown org answers 404 | 54 ms | — | — |
| ✅ passed | `WSP-004` | WSP-004 a non-ULID X-Orkka-Workspace answers 400 Invalid workspace. | 11 ms | — | — |
| ✅ passed | `WSP-005` | WSP-005 naming a workspace I am not in answers 403 | 10 ms | — | — |
| ✅ passed | `WSP-002` | WSP-002 creating a workspace makes it mine and active (restores the previous active one) | 107 ms | — | — |
| ✅ passed | `WSP-006` | WSP-006 the same call in two workspaces returns disjoint projects | 72 ms | — | — |
| ✅ passed | `WSP-007` | WSP-007 an API key ignores X-Orkka-Workspace and stays in its own org | 27 ms | — | — |

## Skipped

- TSRC-006 a query while my runner is offline answers supported:false, not an error — the runner is online — stop it (npm run runner:status) to exercise this case

## Cleanup

- Projects created this run: 9
  - `e2e-psu-20261119-1452-wfjb8` (01M7TF0FRH89NJFSS1M1AH30MB)
  - `e2e-psu-20261119-1452-9xw3f` (01M7TF0FX15QJ59ME9K7HYAD5T)
  - `e2e-psu-20261119-1452-kpbt5` (01M7TF0G25475CRH48PCVA6Q99)
  - `e2e-psu-20261119-1452-d27ak` (01M7TF0G5H64Q2NE8XBG4H5FF8)
  - `e2e-psu-20261119-1452-4n464` (01M7TF0GCJCWXT652AC8TNEPSP)
  - `e2e-tsrc-20261119-1452-4qm3` (01M7TF0GGZD4XEXHJZTH8G0J53)
  - `e2e-tsrc-20261119-1452-m6na` (01M7TF0GNG0B46DHP8QD76R8ZZ)
  - `e2e-tsrc-20261119-1452-5by6` (01M7TF0GVYPFR6EV24YC8PQW1F)
  - `e2e-tsrc-20261119-1452-599y` (01M7TF0H4C93QYTG3WNV71KKG8)
- Leftovers after teardown: none
