# Run 20261118-1329-workflow

| | |
|---|---|
| Suite | `workflow` |
| Target | http://127.0.0.1:6792 |
| Started | 2026-11-18T16:29:47.454Z |
| Duration | 8m 39s |
| Result | 3 passed · 1 failed · 0 timed out · 1 skipped |
| Operator | claude · harness uncommitted · node v24.18.0 · playwright 1.63.0 |

## Notes
- Playwright overall status: failed
- E2E_KEEP_PROJECTS set: no pre-run sweep, no teardown deletes.

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ✅ passed | `LWF-002` | 1. a base folder becomes a project: api is a remote, web a local folder | 2.6 s | — | — |
| ✅ passed | `LWF-003` | 2. a Jira connection added from Settings → Trackers: the token lives only in the runner's secret store | 58 ms | — | — |
| ✅ passed | `LWF-004` `TSRC-004` | 3. the ticket source: To Do intake and the six-step status map | 41 ms | — | — |
| ❌ failed | `LWF-007` | 6. a Story with two sub-tasks: one branch, draft PR, the second builds on the first, ready at the end | 8m 34s | — | — |
| ⏭️ skipped | `LWF-008` | 7. Azure DevOps: a PAT connection and a work item in intake | 0 ms | — | — |

## Failures

### ❌ 6. a Story with two sub-tasks: one branch, draft PR, the second builds on the first, ready at the end

- File: `tests/local/workflow.spec.ts`
- Cases: `LWF-007`
- Trace: `..\current\artifacts\local-workflow-the-develop-72a14--the-first-ready-at-the-end-chromium\trace.zip` (open with `npx playwright show-trace <path>`)

```
Error: [2mexpect([22m[31mreceived[39m[2m).[22mnot[2m.[22mtoMatch[2m([22m[32mexpected[39m[2m)[22m

Expected pattern: not [32m/export function div/[39m
Received string:      [31m"{\"id\":\"01M7QRB1C0CN1HTYZTAJ2EDTEN\",\"capturedAt\":\"2026-11-18T16:37:47.5205008+00:00\",\"branchName\":\"feature/SHOP-20-math-helpers-for-the-api-service\",\"baseBranch\":\"main\",\"isTruncated\":false,\"diff\":\"diff --git a/src/math.js b/src/math.js\\nindex 3c2d838..370bf55 100644\\n--- a/src/math.js\\n+++ b/src/math.js\\n@@ -6,3 +6,7 @@ export function sub(a, b) {\\n [7mexport function div[27m(a, b) {\\n   return a / b;\\n }\\n+\\n+export function neg(a) {\\n+  return -a;\\n+}\\ndiff --git a/test/math.test.js b/test/math.test.js\\nindex 4adfc00..b35e372 100644\\n--- a/test/math.test.js\\n+++ b/test/math.test.js\\n@@ -1,6 +1,6 @@\\n import { test } from \\\"node:test\\\";\\n import assert from \\\"node:assert/strict\\\";\\n-import { sub, div } from \\\"../src/math.js\\\";\\n+import { sub, div, neg } from \\\"../src/math.js\\\";\\n \\n test(\\\"sub\\\", () => {\\n   assert.equal(sub(5, 3), 2);\\n@@ -13,3 +13,15 @@ test(\\\"div\\\", () => {\\n test(\\\"div by zero\\\", () => {\\n   assert.equal(div(5, 0), Infinity);\\n });\\n+\\n+test(\\\"neg\\\", () => {\\n+  assert.equal(neg(5), -5);\\n+});\\n+\\n+test(\\\"neg negative\\\", () => {\\n+  assert.equal(neg(-3), 3);\\n+});\\n+\\n+test(\\\"neg zero\\\", () => {\\n+  assert.equal(neg(0), -0);\\n+});\\n\",\"isLive\":false,\"revision\":0,\"currentBranch\":true,\"unavailable\":null}"[39m
```
Logs: API + Worker console output of the stack at http://127.0.0.1:6792; runner log .runs/runner.log

## Skipped

- 7. Azure DevOps: a PAT connection and a work item in intake

## Cleanup

- Projects created this run: 1
  - `e2e-shop-mwmc75r5` (01M7QQWFHHXG8HZSN5KQNK45N1)
- Leftovers after teardown: none
