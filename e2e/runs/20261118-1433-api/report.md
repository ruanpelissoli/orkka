# Run 20261118-1433-api

| | |
|---|---|
| Suite | `api` |
| Target | http://127.0.0.1:6792 |
| Started | 2026-11-18T17:33:20.385Z |
| Duration | 2.6 s |
| Result | 13 passed · 0 failed · 0 timed out · 2 skipped |
| Operator | claude · harness uncommitted · node v24.18.0 · playwright 1.63.0 |

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ✅ passed | `TSRC-001` | TSRC-001 a project with no ticket source answers 204 | 116 ms | — | e2e-tsrc-20261118-1433-7j8p |
| ✅ passed | `TSRC-002` | TSRC-002 PUT with a connection that is not mine → 400 on trackerConnectionId | 166 ms | — | e2e-tsrc-20261118-1433-pdz5 |
| ✅ passed | `TSRC-003` | TSRC-003 validation: poll interval, branch template, epics, subfolder, scope | 246 ms | — | e2e-tsrc-20261118-1433-hqxc |
| ✅ passed | `TSRC-005` | TSRC-005 DELETE without a ticket source is a no-op 204; GET stays 204 | 121 ms | — | e2e-tsrc-20261118-1433-tjaz |
| ✅ passed | `TSRC-007` | TSRC-007 tracker query with an unknown kind → 400 | 9 ms | — | — |
| ✅ passed | `TSRC-008` | TSRC-008 tracker query on an unknown connection → 404 (not 403) | 7 ms | — | — |
| ✅ passed | `TSRC-009` | TSRC-009 GET /api/trackers/connections lists my connections with metadata only | 12 ms | — | — |
| ⏭️ skipped | `TSRC-006` | TSRC-006 a query while my runner is offline answers supported:false, not an error | 25 ms | — | — |
| ✅ passed | `WSP-001` | WSP-001 GET /api/me/workspaces lists my workspaces with exactly one active | 30 ms | — | — |
| ✅ passed | `WSP-003` | WSP-003 switching the active workspace answers 204; an unknown org answers 404 | 57 ms | — | — |
| ✅ passed | `WSP-004` | WSP-004 a non-ULID X-Orkka-Workspace answers 400 Invalid workspace. | 11 ms | — | — |
| ✅ passed | `WSP-005` | WSP-005 naming a workspace I am not in answers 403 | 11 ms | — | — |
| ⏭️ skipped | `WSP-006` | WSP-006 the same call in two workspaces returns disjoint projects | 15 ms | — | — |
| ✅ passed | `WSP-007` | WSP-007 an API key ignores X-Orkka-Workspace and stays in its own org | 15 ms | — | — |
| ✅ passed | `WSP-002` | WSP-002 creating a workspace makes it mine and active (restores the previous active one) | 91 ms | — | — |

## Skipped

- TSRC-006 a query while my runner is offline answers supported:false, not an error — the runner is online — stop it (npm run runner:status) to exercise this case
- WSP-006 the same call in two workspaces returns disjoint projects — only one workspace exists (WSP-002 creates a second one)

## Cleanup

- Projects created this run: 4
  - `e2e-tsrc-20261118-1433-7j8p` (01M7QVGSJ6CFM8ZCJHKGBE6GWQ)
  - `e2e-tsrc-20261118-1433-pdz5` (01M7QVGSPB0MK1FKCHYVJSE6KH)
  - `e2e-tsrc-20261118-1433-hqxc` (01M7QVGSVBAQ6PC0AV5N0FBEDY)
  - `e2e-tsrc-20261118-1433-tjaz` (01M7QVGT37NPKDAV74W2HTR693)
- Leftovers after teardown: none
