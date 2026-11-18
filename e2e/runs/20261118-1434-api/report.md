# Run 20261118-1434-api

| | |
|---|---|
| Suite | `api` |
| Target | http://127.0.0.1:6792 |
| Started | 2026-11-18T17:34:37.416Z |
| Duration | 2.6 s |
| Result | 14 passed · 0 failed · 0 timed out · 1 skipped |
| Operator | claude · harness uncommitted · node v24.18.0 · playwright 1.63.0 |

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ✅ passed | `TSRC-001` | TSRC-001 a project with no ticket source answers 204 | 104 ms | — | e2e-tsrc-20261118-1434-5hby |
| ✅ passed | `TSRC-002` | TSRC-002 PUT with a connection that is not mine → 400 on trackerConnectionId | 118 ms | — | e2e-tsrc-20261118-1434-gnca |
| ✅ passed | `TSRC-003` | TSRC-003 validation: poll interval, branch template, epics, subfolder, scope | 287 ms | — | e2e-tsrc-20261118-1434-5g8v |
| ✅ passed | `TSRC-005` | TSRC-005 DELETE without a ticket source is a no-op 204; GET stays 204 | 100 ms | — | e2e-tsrc-20261118-1434-pg8g |
| ✅ passed | `TSRC-007` | TSRC-007 tracker query with an unknown kind → 400 | 11 ms | — | — |
| ✅ passed | `TSRC-008` | TSRC-008 tracker query on an unknown connection → 404 (not 403) | 19 ms | — | — |
| ✅ passed | `TSRC-009` | TSRC-009 GET /api/trackers/connections lists my connections with metadata only | 13 ms | — | — |
| ⏭️ skipped | `TSRC-006` | TSRC-006 a query while my runner is offline answers supported:false, not an error | 27 ms | — | — |
| ✅ passed | `WSP-001` | WSP-001 GET /api/me/workspaces lists my workspaces with exactly one active | 31 ms | — | — |
| ✅ passed | `WSP-003` | WSP-003 switching the active workspace answers 204; an unknown org answers 404 | 55 ms | — | — |
| ✅ passed | `WSP-004` | WSP-004 a non-ULID X-Orkka-Workspace answers 400 Invalid workspace. | 10 ms | — | — |
| ✅ passed | `WSP-005` | WSP-005 naming a workspace I am not in answers 403 | 9 ms | — | — |
| ✅ passed | `WSP-002` | WSP-002 creating a workspace makes it mine and active (restores the previous active one) | 59 ms | — | — |
| ✅ passed | `WSP-006` | WSP-006 the same call in two workspaces returns disjoint projects | 87 ms | — | — |
| ✅ passed | `WSP-007` | WSP-007 an API key ignores X-Orkka-Workspace and stays in its own org | 23 ms | — | — |

## Skipped

- TSRC-006 a query while my runner is offline answers supported:false, not an error — the runner is online — stop it (npm run runner:status) to exercise this case

## Cleanup

- Projects created this run: 4
  - `e2e-tsrc-20261118-1434-5hby` (01M7QVK4Q7Z19EZDKAJ1FY331N)
  - `e2e-tsrc-20261118-1434-gnca` (01M7QVK4TH07364K12QQAWACZY)
  - `e2e-tsrc-20261118-1434-5g8v` (01M7QVK4YZK9S8AZKAPX4J60NT)
  - `e2e-tsrc-20261118-1434-pg8g` (01M7QVK587PXGYMZZKRFXBPWFX)
- Leftovers after teardown: none
