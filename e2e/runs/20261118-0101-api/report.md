# Run 20261118-0101-api

| | |
|---|---|
| Suite | `api` |
| Target | http://127.0.0.1:6750 |
| Started | 2026-11-18T04:01:38.498Z |
| Duration | 3.2 s |
| Result | 14 passed · 0 failed · 0 timed out · 1 skipped |
| Operator | claude · harness 7c790e44baab · node v24.18.0 · playwright 1.63.0 |

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ✅ passed | `TSRC-001` | TSRC-001 a project with no ticket source answers 204 | 291 ms | — | e2e-tsrc-20261118-0101-3pf6 |
| ✅ passed | `TSRC-002` | TSRC-002 PUT with a connection that is not mine → 400 on trackerConnectionId | 236 ms | — | e2e-tsrc-20261118-0101-z8vz |
| ✅ passed | `TSRC-003` | TSRC-003 validation: poll interval, branch template, epics, subfolder, scope | 265 ms | — | e2e-tsrc-20261118-0101-kzys |
| ✅ passed | `TSRC-005` | TSRC-005 DELETE without a ticket source is a no-op 204; GET stays 204 | 135 ms | — | e2e-tsrc-20261118-0101-8ftt |
| ✅ passed | `TSRC-007` | TSRC-007 tracker query with an unknown kind → 400 | 13 ms | — | — |
| ✅ passed | `TSRC-008` | TSRC-008 tracker query on an unknown connection → 404 (not 403) | 22 ms | — | — |
| ✅ passed | `TSRC-009` | TSRC-009 GET /api/trackers/connections lists my connections with metadata only | 33 ms | — | — |
| ⏭️ skipped | `TSRC-006` | TSRC-006 a query while my runner is offline answers supported:false, not an error | 45 ms | — | — |
| ✅ passed | `WSP-001` | WSP-001 GET /api/me/workspaces lists my workspaces with exactly one active | 34 ms | — | — |
| ✅ passed | `WSP-003` | WSP-003 switching the active workspace answers 204; an unknown org answers 404 | 69 ms | — | — |
| ✅ passed | `WSP-004` | WSP-004 a non-ULID X-Orkka-Workspace answers 400 Invalid workspace. | 9 ms | — | — |
| ✅ passed | `WSP-005` | WSP-005 naming a workspace I am not in answers 403 | 19 ms | — | — |
| ✅ passed | `WSP-006` | WSP-006 the same call in two workspaces returns disjoint projects | 85 ms | — | — |
| ✅ passed | `WSP-007` | WSP-007 an API key ignores X-Orkka-Workspace and stays in its own org | 38 ms | — | — |
| ✅ passed | `WSP-002` | WSP-002 creating a workspace makes it mine and active (restores the previous active one) | 139 ms | — | — |

## Skipped

- TSRC-006 a query while my runner is offline answers supported:false, not an error — the runner is online — stop it (npm run runner:status) to exercise this case

## Cleanup

- Projects created this run: 4
  - `e2e-tsrc-20261118-0101-3pf6` (01M7PD2H8BPVPEV5EYYGSEENNF)
  - `e2e-tsrc-20261118-0101-z8vz` (01M7PD2HFBTDXDNMFAEJ7GPV1V)
  - `e2e-tsrc-20261118-0101-kzys` (01M7PD2HQ891PTST94VBC0DZH7)
  - `e2e-tsrc-20261118-0101-8ftt` (01M7PD2HZRTAT3RBE8VQG9V96T)
- Leftovers after teardown: none
