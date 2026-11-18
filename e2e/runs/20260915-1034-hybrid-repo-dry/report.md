# Run 20260915-1034-hybrid-repo-dry

| | |
|---|---|
| Suite | `hybrid-repo-dry` |
| Target | prod |
| Started | 2026-09-15T13:34:22.916Z |
| Duration | 3m 28s |
| Result | 3 passed · 5 failed · 0 timed out · 5 skipped |
| Spend | $0.00 of $5.00 cap (credits 3294 → 3294) |
| Operator | claude · harness uncommitted · node v24.18.0 · playwright 1.63.0 |

## Notes
- Playwright overall status: failed
- Credentials are not an Owner (role=Member); permission-gated cases may fail.
- Org plan has no team features (not Enterprise); Members cases will be skipped.

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ✅ passed | `HYB-001` | HYB-001 execution preference can be set to MyRunner and read back @hybrid-repo @smoke | 1.6 s | $0.000 | — |
| ✅ passed | `HYB-002` | HYB-002 unknown execution preference target is rejected with 400 | 477 ms | $0.000 | — |
| ❌ failed | `HYB-003` `ONB-020` | HYB-003 create an unmanaged project via API: Ready immediately, key and slug assigned | 1.2 s | $0.000 | e2e-hyb-20260915-1034-zmhy<br>e2e-hyb-20260915-1034-zmhy |
| ⏭️ skipped | `HYB-004` `REPO-001` `REPO-002` | HYB-004 link the GitHub-App fixture repo and see it on the Repos page | 0 ms | — | — |
| ⏭️ skipped | `HYB-005` | HYB-005 repo access preflight answers for the linked repo on the runner | 0 ms | — | — |
| ⏭️ skipped | `HYB-006` | HYB-006 execution-target preview says a Claude task runs on the runner for $0 | 0 ms | — | — |
| ⏭️ skipped | `REPO-006` `REPO-007` | HYB-007 repo file browser serves the fixture's features file from main | 0 ms | — | — |
| ⏭️ skipped | `HYB-046` `SET-040` | HYB-008 deleting the project removes it (404 afterwards) and leaves the GitHub repo untouched | 0 ms | — | — |
| ✅ passed | `RUN-002` `RUN-003` | RUN-002 Runner settings page shows the runner online with its machine and providers | 7.1 s | $0.000 | — |
| ❌ failed | `BRD-001` `BRD-002` `NAV-002` | BRD-001 board renders the nine lanes and the new-task entry point for a fresh project | 19.7 s | $0.000 | e2e-hyb-20260915-1034-pkzd<br>e2e-hyb-20260915-1034-pkzd |
| ❌ failed | `TSK-001` `TSK-002` `TSK-011` `BRD-010` | TSK-001 create a task from the form as a draft and see it on the board and the detail page | 1m 19s | — | e2e-hyb-20260915-1034-7ugy<br>e2e-hyb-20260915-1034-7ugy |
| ❌ failed | `TSK-012` `HYB-013` | TSK-012 task detail shows the runner execution badge for a UserRunner task | 1m 28s | $0.000 | e2e-hyb-20260915-1036-fc8v<br>e2e-hyb-20260915-1036-fc8v |
| ❌ failed | `SET-010` `SET-011` `SET-012` | SET-010 project settings: pipeline toggles persist through the UI and the API sees them | 3.9 s | $0.000 | e2e-hyb-20260915-1037-ypcj<br>e2e-hyb-20260915-1037-ypcj |

## Failures

### ❌ HYB-003 create an unmanaged project via API: Ready immediately, key and slug assigned

- File: `tests/hybrid-repo/project-and-repo.spec.ts`
- Cases: `HYB-003`, `ONB-020`
- Projects: `01M2JMD9XNZGF4REF3AQ4A1RAQ e2e-hyb-20260915-1034-zmhy`, `01M2JMD9XNZGF4REF3AQ4A1RAQ e2e-hyb-20260915-1034-zmhy`
- Trace: `..\current\artifacts\hybrid-repo-project-and-re-de0ee-ately-key-and-slug-assigned-chromium\trace.zip` (open with `npx playwright show-trace <path>`)

```
ApiError: GET /api/projects/e2e-hyb-20260915-1034-zmhy → 404: (empty body)
```
Prod logs: `kubectl --context aks-orkka-prod logs orkka-worker-0 --since=5m | grep -E "01M2JMD9XNZGF4REF3AQ4A1RAQ|01M2JMD9XNZGF4REF3AQ4A1RAQ"`

### ❌ BRD-001 board renders the nine lanes and the new-task entry point for a fresh project

- File: `tests/hybrid-repo/ui-flows.spec.ts`
- Cases: `BRD-001`, `BRD-002`, `NAV-002`
- Projects: `01M2JMDJN04K7CN9YP2SJ9JS6E e2e-hyb-20260915-1034-pkzd`, `01M2JMDJN04K7CN9YP2SJ9JS6E e2e-hyb-20260915-1034-pkzd`
- Trace: `..\current\artifacts\hybrid-repo-ui-flows-hybri-f1d7c-y-point-for-a-fresh-project-chromium\trace.zip` (open with `npx playwright show-trace <path>`)
- Screenshot: `..\current\artifacts\hybrid-repo-ui-flows-hybri-f1d7c-y-point-for-a-fresh-project-chromium\test-failed-1.png`

```
Error: [2mexpect([22m[31mlocator[39m[2m).[22mtoBeVisible[2m([22m[2m)[22m failed

Locator: getByText('Drafting', { exact: true }).first()
Expected: visible
Timeout: 15000ms
Error: element(s) not found

Call log:
[2m  - Expect "toBeVisible" getByText('Drafting', { exact: true }).first() with timeout 15000ms[22m
[2m  - waiting for getByText('Drafting', { exact: true }).first()[22m

```
Prod logs: `kubectl --context aks-orkka-prod logs orkka-worker-0 --since=5m | grep -E "01M2JMDJN04K7CN9YP2SJ9JS6E|01M2JMDJN04K7CN9YP2SJ9JS6E"`

### ❌ TSK-001 create a task from the form as a draft and see it on the board and the detail page

- File: `tests/hybrid-repo/ui-flows.spec.ts`
- Cases: `TSK-001`, `TSK-002`, `TSK-011`, `BRD-010`
- Projects: `01M2JME6XN8D4KE856AXVMZ61M e2e-hyb-20260915-1034-7ugy`, `01M2JME6XN8D4KE856AXVMZ61M e2e-hyb-20260915-1034-7ugy`
- Trace: `..\current\artifacts\hybrid-repo-ui-flows-hybri-ec5c8-e-board-and-the-detail-page-chromium\trace.zip` (open with `npx playwright show-trace <path>`)
- Screenshot: `..\current\artifacts\hybrid-repo-ui-flows-hybri-ec5c8-e-board-and-the-detail-page-chromium\test-failed-1.png`

```
Error: task created through the UI

[2mexpect([22m[31mreceived[39m[2m).[22mtoBeTruthy[2m()[22m

Received: [31mundefined[39m
```
Prod logs: `kubectl --context aks-orkka-prod logs orkka-worker-0 --since=5m | grep -E "01M2JME6XN8D4KE856AXVMZ61M|01M2JME6XN8D4KE856AXVMZ61M"`

### ❌ TSK-012 task detail shows the runner execution badge for a UserRunner task

- File: `tests/hybrid-repo/ui-flows.spec.ts`
- Cases: `TSK-012`, `HYB-013`
- Projects: `01M2JMHTQ7X7WPZRD45GX5FH2J e2e-hyb-20260915-1036-fc8v`, `01M2JMHTQ7X7WPZRD45GX5FH2J e2e-hyb-20260915-1036-fc8v`
- Runner: LAPTOP-B v0.3.154 (started by harness)
- Trace: `..\current\artifacts\hybrid-repo-ui-flows-hybri-4a893-badge-for-a-UserRunner-task-chromium\trace.zip` (open with `npx playwright show-trace <path>`)
- Screenshot: `..\current\artifacts\hybrid-repo-ui-flows-hybri-4a893-badge-for-a-UserRunner-task-chromium\test-failed-1.png`

```
Error: [2mexpect([22m[31mlocator[39m[2m).[22mtoBeVisible[2m([22m[2m)[22m failed

Locator: getByText('Add feature entry "e2e-20260915-1036-8e8qd"').first()
Expected: visible
Timeout: 30000ms
Error: element(s) not found

Call log:
[2m  - Expect "toBeVisible" getByText('Add feature entry "e2e-20260915-1036-8e8qd"').first() with timeout 30000ms[22m
[2m  - waiting for getByText('Add feature entry "e2e-20260915-1036-8e8qd"').first()[22m

```
Prod logs: `kubectl --context aks-orkka-prod logs orkka-worker-0 --since=5m | grep -E "01M2JMHTQ7X7WPZRD45GX5FH2J|01M2JMHTQ7X7WPZRD45GX5FH2J"`

### ❌ SET-010 project settings: pipeline toggles persist through the UI and the API sees them

- File: `tests/hybrid-repo/ui-flows.spec.ts`
- Cases: `SET-010`, `SET-011`, `SET-012`
- Projects: `01M2JMKC46G072F5J03SV4FZC8 e2e-hyb-20260915-1037-ypcj`, `01M2JMKC46G072F5J03SV4FZC8 e2e-hyb-20260915-1037-ypcj`
- Trace: `..\current\artifacts\hybrid-repo-ui-flows-hybri-f7bee-he-UI-and-the-API-sees-them-chromium\trace.zip` (open with `npx playwright show-trace <path>`)
- Screenshot: `..\current\artifacts\hybrid-repo-ui-flows-hybri-f7bee-he-UI-and-the-API-sees-them-chromium\test-failed-1.png`

```
Error: [2mexpect([22m[31mreceived[39m[2m).[22mtoBeGreaterThan[2m([22m[32mexpected[39m[2m)[22m

Expected: > [32m3[39m
Received:   [31m0[39m
```
Prod logs: `kubectl --context aks-orkka-prod logs orkka-worker-0 --since=5m | grep -E "01M2JMKC46G072F5J03SV4FZC8|01M2JMKC46G072F5J03SV4FZC8"`

## Skipped

- HYB-004 link the GitHub-App fixture repo and see it on the Repos page
- HYB-005 repo access preflight answers for the linked repo on the runner
- HYB-006 execution-target preview says a Claude task runs on the runner for $0
- HYB-007 repo file browser serves the fixture's features file from main
- HYB-008 deleting the project removes it (404 afterwards) and leaves the GitHub repo untouched

## Cleanup

- Projects created this run: 5
  - Unmanaged `e2e-hyb-20260915-1034-zmhy` (01M2JMD9XNZGF4REF3AQ4A1RAQ)
  - Unmanaged `e2e-hyb-20260915-1034-pkzd` (01M2JMDJN04K7CN9YP2SJ9JS6E)
  - Unmanaged `e2e-hyb-20260915-1034-7ugy` (01M2JME6XN8D4KE856AXVMZ61M)
  - Unmanaged `e2e-hyb-20260915-1036-fc8v` (01M2JMHTQ7X7WPZRD45GX5FH2J)
  - Unmanaged `e2e-hyb-20260915-1037-ypcj` (01M2JMKC46G072F5J03SV4FZC8)
- Leftovers after teardown: none
