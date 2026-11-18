# Run 20261118-0037-adhoc

| | |
|---|---|
| Suite | `adhoc` |
| Target | http://127.0.0.1:6750 |
| Started | 2026-11-18T03:37:34.079Z |
| Duration | 27.8 s |
| Result | 3 passed · 1 failed · 0 timed out · 3 skipped |
| Operator | claude · harness 7c790e44baab · node v24.18.0 · playwright 1.63.0 |

## Notes
- Playwright overall status: failed
- E2E_KEEP_PROJECTS set: no pre-run sweep, no teardown deletes.

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ✅ passed | — | dashboard | 2.6 s | — | — |
| ✅ passed | — | create from folder: the preview lists both repositories with their location | 3.2 s | — | — |
| ✅ passed | — | board (with the Story chip when a Story is on it) | 2.4 s | — | — |
| ❌ failed | — | task detail with the Story panel, then the Discuss tab | 17.8 s | — | — |
| ⏭️ skipped | — | Settings → Trackers | 0 ms | — | — |
| ⏭️ skipped | — | project settings: the ticket source card with status sync | 0 ms | — | — |
| ⏭️ skipped | — | Machine page | 0 ms | — | — |

## Failures

### ❌ task detail with the Story panel, then the Discuss tab

- File: `tests/local/screens.spec.ts`
- Trace: `..\current\artifacts\local-screens-key-screens--881b3--panel-then-the-Discuss-tab-chromium\trace.zip` (open with `npx playwright show-trace <path>`)
- Screenshot: `..\current\artifacts\local-screens-key-screens--881b3--panel-then-the-Discuss-tab-chromium\attachments\04-task-detail-story-6bd7d6338b53f7306e3e51ea169aa87583dfe3ae.png`
- Screenshot: `..\current\artifacts\local-screens-key-screens--881b3--panel-then-the-Discuss-tab-chromium\test-failed-1.png`

```
TimeoutError: locator.click: Timeout 15000ms exceeded.
Call log:
[2m  - waiting for getByRole('tab', { name: /discuss/i }).or(getByRole('button', { name: /^discuss$/i })).first()[22m

```
Logs: API + Worker console output of the stack at http://127.0.0.1:6750; runner log .runs/runner.log

## Skipped

- Settings → Trackers
- project settings: the ticket source card with status sync
- Machine page

## Cleanup

- Projects created this run: 0
- Leftovers after teardown: none
