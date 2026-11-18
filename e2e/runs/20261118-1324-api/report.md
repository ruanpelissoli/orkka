# Run 20261118-1324-api

| | |
|---|---|
| Suite | `api` |
| Target | http://127.0.0.1:6792 |
| Started | 2026-11-18T16:24:41.529Z |
| Duration | 2.6 s |
| Result | 14 passed · 0 failed · 0 timed out · 1 skipped |
| Operator | claude · harness uncommitted · node v24.18.0 · playwright 1.63.0 |

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ✅ passed | `TSRC-001` | TSRC-001 a project with no ticket source answers 204 | 120 ms | — | e2e-tsrc-20261118-1324-dkgf |
| ✅ passed | `TSRC-002` | TSRC-002 PUT with a connection that is not mine → 400 on trackerConnectionId | 130 ms | — | e2e-tsrc-20261118-1324-hsgp |
| ✅ passed | `TSRC-003` | TSRC-003 validation: poll interval, branch template, epics, subfolder, scope | 268 ms | — | e2e-tsrc-20261118-1324-629h |
| ✅ passed | `TSRC-005` | TSRC-005 DELETE without a ticket source is a no-op 204; GET stays 204 | 120 ms | — | e2e-tsrc-20261118-1324-n8q6 |
| ✅ passed | `TSRC-007` | TSRC-007 tracker query with an unknown kind → 400 | 11 ms | — | — |
| ✅ passed | `TSRC-008` | TSRC-008 tracker query on an unknown connection → 404 (not 403) | 4 ms | — | — |
| ✅ passed | `TSRC-009` | TSRC-009 GET /api/trackers/connections lists my connections with metadata only | 11 ms | — | — |
| ⏭️ skipped | `TSRC-006` | TSRC-006 a query while my runner is offline answers supported:false, not an error | 28 ms | — | — |
| ✅ passed | `WSP-001` | WSP-001 GET /api/me/workspaces lists my workspaces with exactly one active | 31 ms | — | — |
| ✅ passed | `WSP-003` | WSP-003 switching the active workspace answers 204; an unknown org answers 404 | 58 ms | — | — |
| ✅ passed | `WSP-004` | WSP-004 a non-ULID X-Orkka-Workspace answers 400 Invalid workspace. | 10 ms | — | — |
| ✅ passed | `WSP-005` | WSP-005 naming a workspace I am not in answers 403 | 13 ms | — | — |
| ✅ passed | `WSP-006` | WSP-006 the same call in two workspaces returns disjoint projects | 57 ms | — | — |
| ✅ passed | `WSP-007` | WSP-007 an API key ignores X-Orkka-Workspace and stays in its own org | 34 ms | — | — |
| ✅ passed | `WSP-002` | WSP-002 creating a workspace makes it mine and active (restores the previous active one) | 76 ms | — | — |

## Skipped

- TSRC-006 a query while my runner is offline answers supported:false, not an error — the runner is online — stop it (npm run runner:status) to exercise this case

## Cleanup

- Projects created this run: 4
  - `e2e-tsrc-20261118-1324-dkgf` (01M7QQK34TNE2C7PTC5QA29EXH)
  - `e2e-tsrc-20261118-1324-hsgp` (01M7QQK3936KBD47J38PSE2WKZ)
  - `e2e-tsrc-20261118-1324-629h` (01M7QQK3DS98H4JMMYS994N55M)
  - `e2e-tsrc-20261118-1324-n8q6` (01M7QQK3P3MGMXJCEQBGZPVHVM)
- Leftovers after teardown: none
