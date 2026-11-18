# Run 20261118-0059-api

| | |
|---|---|
| Suite | `api` |
| Target | http://127.0.0.1:6750 |
| Started | 2026-11-18T03:59:14.494Z |
| Duration | 3.5 s |
| Result | 12 passed · 1 failed · 0 timed out · 2 skipped |
| Operator | claude · harness 7c790e44baab · node v24.18.0 · playwright 1.63.0 |

## Notes
- Playwright overall status: failed

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ✅ passed | `TSRC-001` | TSRC-001 a project with no ticket source answers 204 | 106 ms | — | e2e-tsrc-20261118-0059-r6au |
| ✅ passed | `TSRC-002` | TSRC-002 PUT with a connection that is not mine → 400 on trackerConnectionId | 126 ms | — | e2e-tsrc-20261118-0059-kf2j |
| ❌ failed | `TSRC-003` | TSRC-003 validation: poll interval, branch template, epics, subfolder, scope | 255 ms | — | e2e-tsrc-20261118-0059-8g9s |
| ✅ passed | `TSRC-005` | TSRC-005 DELETE without a ticket source is a no-op 204; GET stays 204 | 105 ms | — | e2e-tsrc-20261118-0059-vab3 |
| ✅ passed | `TSRC-007` | TSRC-007 tracker query with an unknown kind → 400 | 10 ms | — | — |
| ✅ passed | `TSRC-008` | TSRC-008 tracker query on an unknown connection → 404 (not 403) | 11 ms | — | — |
| ✅ passed | `TSRC-009` | TSRC-009 GET /api/trackers/connections lists my connections with metadata only | 11 ms | — | — |
| ⏭️ skipped | `TSRC-006` | TSRC-006 a query while my runner is offline answers supported:false, not an error | 24 ms | — | — |
| ✅ passed | `WSP-001` | WSP-001 GET /api/me/workspaces lists my workspaces with exactly one active | 33 ms | — | — |
| ✅ passed | `WSP-003` | WSP-003 switching the active workspace answers 204; an unknown org answers 404 | 49 ms | — | — |
| ✅ passed | `WSP-004` | WSP-004 a non-ULID X-Orkka-Workspace answers 400 Invalid workspace. | 15 ms | — | — |
| ✅ passed | `WSP-005` | WSP-005 naming a workspace I am not in answers 403 | 9 ms | — | — |
| ⏭️ skipped | `WSP-006` | WSP-006 the same call in two workspaces returns disjoint projects | 27 ms | — | — |
| ✅ passed | `WSP-007` | WSP-007 an API key ignores X-Orkka-Workspace and stays in its own org | 15 ms | — | — |
| ✅ passed | `WSP-002` | WSP-002 creating a workspace makes it mine and active (restores the previous active one) | 104 ms | — | — |

## Failures

### ❌ TSRC-003 validation: poll interval, branch template, epics, subfolder, scope

- File: `tests/api/ticket-source.spec.ts`
- Cases: `TSRC-003`
- Projects: `01M7PCY501MV3JS3081SCMGBK6 e2e-tsrc-20261118-0059-8g9s`
- Trace: `..\current\artifacts\api-ticket-source-ticket-s-fe17a-plate-epics-subfolder-scope-chromium\trace.zip` (open with `npx playwright show-trace <path>`)

```
Error: [2mexpect([22m[31mreceived[39m[2m).[22mtoMatch[2m([22m[32mexpected[39m[2m)[22m

Expected pattern: [32m/Pick the Jira project or Azure DevOps project/[39m
Received string:  [31m"PUT /api/projects/01M7PCY501MV3JS3081SCMGBK6/tracker → 400: scope: 'scope' must not be empty."[39m
```
Logs: API + Worker console output of the stack at http://127.0.0.1:6750; runner log .runs/runner.log (grep for 01M7PCY501MV3JS3081SCMGBK6)

## Skipped

- TSRC-006 a query while my runner is offline answers supported:false, not an error — the runner is online — stop it (npm run runner:status) to exercise this case
- WSP-006 the same call in two workspaces returns disjoint projects — only one workspace exists (WSP-002 creates a second one)

## Cleanup

- Projects created this run: 4
  - `e2e-tsrc-20261118-0059-r6au` (01M7PCY4R8HBV5757NDD6TD3GM)
  - `e2e-tsrc-20261118-0059-kf2j` (01M7PCY4VKACNS57PBPZF1SSJN)
  - `e2e-tsrc-20261118-0059-8g9s` (01M7PCY501MV3JS3081SCMGBK6)
  - `e2e-tsrc-20261118-0059-vab3` (01M7PCY5XH1PAR8CH75BPZ45WB)
- Leftovers after teardown: none
