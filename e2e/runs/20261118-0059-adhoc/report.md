# Run 20261118-0059-adhoc

| | |
|---|---|
| Suite | `adhoc` |
| Target | http://127.0.0.1:6750 |
| Started | 2026-11-18T03:59:00.856Z |
| Duration | 2.5 s |
| Result | 0 passed · 2 failed · 0 timed out · 0 skipped |
| Operator | claude · harness 7c790e44baab · node v24.18.0 · playwright 1.63.0 |

## Notes
- Playwright overall status: failed

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ❌ failed | `TSRC-003` | TSRC-003 validation: poll interval, branch template, epics, subfolder, scope | 152 ms | — | e2e-tsrc-20261118-0059-r65v |
| ❌ failed | `WSP-001` | WSP-001 GET /api/me/workspaces lists my workspaces with exactly one active | 39 ms | — | — |

## Failures

### ❌ TSRC-003 validation: poll interval, branch template, epics, subfolder, scope

- File: `tests/api/ticket-source.spec.ts`
- Cases: `TSRC-003`
- Projects: `01M7PCXPWJX96KD43BY8W1V260 e2e-tsrc-20261118-0059-r65v`
- Trace: `..\current\artifacts\api-ticket-source-ticket-s-fe17a-plate-epics-subfolder-scope-chromium\trace.zip` (open with `npx playwright show-trace <path>`)

```
Error: [2mexpect([22m[31mreceived[39m[2m).[22mtoMatch[2m([22m[32mexpected[39m[2m)[22m

Expected pattern: [32m/at least one epic/[39m
Received string:  [31m"PUT /api/projects/01M7PCXPWJX96KD43BY8W1V260/tracker → 400: trackerConnectionId: Pick one of your own tracker connections — add one in Settings → Trackers (or `orkka-local tracker add`)."[39m
```
Logs: API + Worker console output of the stack at http://127.0.0.1:6750; runner log .runs/runner.log (grep for 01M7PCXPWJX96KD43BY8W1V260)

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

## Cleanup

- Projects created this run: 1
  - `e2e-tsrc-20261118-0059-r65v` (01M7PCXPWJX96KD43BY8W1V260)
- Leftovers after teardown: none
