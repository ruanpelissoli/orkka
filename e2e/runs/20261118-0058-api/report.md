# Run 20261118-0058-api

| | |
|---|---|
| Suite | `api` |
| Target | http://127.0.0.1:6750 |
| Started | 2026-11-18T03:58:53.507Z |
| Duration | 3.0 s |
| Result | 6 passed · 2 failed · 0 timed out · 7 skipped |
| Operator | claude · harness 7c790e44baab · node v24.18.0 · playwright 1.63.0 |

## Notes
- Playwright overall status: failed

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ✅ passed | `TSRC-001` | TSRC-001 a project with no ticket source answers 204 | 105 ms | — | e2e-tsrc-20261118-0058-gztm |
| ✅ passed | `TSRC-002` | TSRC-002 PUT with a connection that is not mine → 400 on trackerConnectionId | 161 ms | — | e2e-tsrc-20261118-0058-jpwv |
| ❌ failed | `TSRC-003` | TSRC-003 validation: poll interval, branch template, epics, subfolder, scope | 170 ms | — | e2e-tsrc-20261118-0058-xjxc |
| ✅ passed | `TSRC-005` | TSRC-005 DELETE without a ticket source is a no-op 204; GET stays 204 | 138 ms | — | e2e-tsrc-20261118-0058-rf82 |
| ✅ passed | `TSRC-007` | TSRC-007 tracker query with an unknown kind → 400 | 9 ms | — | — |
| ✅ passed | `TSRC-008` | TSRC-008 tracker query on an unknown connection → 404 (not 403) | 10 ms | — | — |
| ✅ passed | `TSRC-009` | TSRC-009 GET /api/trackers/connections lists my connections with metadata only | 9 ms | — | — |
| ⏭️ skipped | `TSRC-006` | TSRC-006 a query while my runner is offline answers supported:false, not an error | 25 ms | — | — |
| ❌ failed | `WSP-001` | WSP-001 GET /api/me/workspaces lists my workspaces with exactly one active | 36 ms | — | — |
| ⏭️ skipped | `WSP-003` | WSP-003 switching the active workspace answers 204; an unknown org answers 404 | 0 ms | — | — |
| ⏭️ skipped | `WSP-004` | WSP-004 a non-ULID X-Orkka-Workspace answers 400 Invalid workspace. | 0 ms | — | — |
| ⏭️ skipped | `WSP-005` | WSP-005 naming a workspace I am not in answers 403 | 0 ms | — | — |
| ⏭️ skipped | `WSP-006` | WSP-006 the same call in two workspaces returns disjoint projects | 0 ms | — | — |
| ⏭️ skipped | `WSP-007` | WSP-007 an API key ignores X-Orkka-Workspace and stays in its own org | 0 ms | — | — |
| ⏭️ skipped | `WSP-002` | WSP-002 creating a workspace makes it mine and active (restores the previous active one) | 0 ms | — | — |

## Failures

### ❌ TSRC-003 validation: poll interval, branch template, epics, subfolder, scope

- File: `tests/api/ticket-source.spec.ts`
- Cases: `TSRC-003`
- Projects: `01M7PCXG3GJQ2H41JHA2TFDFB7 e2e-tsrc-20261118-0058-xjxc`
- Trace: `..\current\artifacts\api-ticket-source-ticket-s-fe17a-plate-epics-subfolder-scope-chromium\trace.zip` (open with `npx playwright show-trace <path>`)

```
Error: [2mexpect([22m[31mreceived[39m[2m).[22mtoMatch[2m([22m[32mexpected[39m[2m)[22m

Expected pattern: [32m/at least one epic/[39m
Received string:  [31m"PUT /api/projects/01M7PCXG3GJQ2H41JHA2TFDFB7/tracker → 400: trackerConnectionId: Pick one of your own tracker connections — add one in Settings → Trackers (or `orkka-local tracker add`)."[39m
```
Logs: API + Worker console output of the stack at http://127.0.0.1:6750; runner log .runs/runner.log (grep for 01M7PCXG3GJQ2H41JHA2TFDFB7)

### ❌ WSP-001 GET /api/me/workspaces lists my workspaces with exactly one active

- File: `tests/api/workspaces.spec.ts`
- Cases: `WSP-001`
- Trace: `..\current\artifacts\api-workspaces-workspaces--25a0e-ces-with-exactly-one-active-chromium\trace.zip` (open with `npx playwright show-trace <path>`)

```
Error: [2mexpect([22m[31mreceived[39m[2m).[22mtoMatchObject[2m([22m[32mexpected[39m[2m)[22m

[32m- Expected  - 1[39m
[31m+ Received  + 0[39m

[2m  Object {[22m
[2m    "name": Any<String>,[22m
[2m    "orgId": Any<String>,[22m
[2m    "role": StringMatching /Owner|Member/,[22m
[2m    "slug": Any<String>,[22m
[32m-   "suspended": Any<Boolean>,[39m
[2m  }[22m
```
Logs: API + Worker console output of the stack at http://127.0.0.1:6750; runner log .runs/runner.log

## Skipped

- TSRC-006 a query while my runner is offline answers supported:false, not an error — the runner is online — stop it (npm run runner:status) to exercise this case
- WSP-003 switching the active workspace answers 204; an unknown org answers 404
- WSP-004 a non-ULID X-Orkka-Workspace answers 400 Invalid workspace.
- WSP-005 naming a workspace I am not in answers 403
- WSP-006 the same call in two workspaces returns disjoint projects
- WSP-007 an API key ignores X-Orkka-Workspace and stays in its own org
- WSP-002 creating a workspace makes it mine and active (restores the previous active one)

## Cleanup

- Projects created this run: 4
  - `e2e-tsrc-20261118-0058-gztm` (01M7PCXFTX6K8H3PHQG5D1EWYE)
  - `e2e-tsrc-20261118-0058-jpwv` (01M7PCXFYHKS71W8WPBAQM5HSK)
  - `e2e-tsrc-20261118-0058-xjxc` (01M7PCXG3GJQ2H41JHA2TFDFB7)
  - `e2e-tsrc-20261118-0058-rf82` (01M7PCXH1R3583ENNQ9RBAJJ1D)
- Leftovers after teardown: none
