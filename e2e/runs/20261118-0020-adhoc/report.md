# Run 20261118-0020-adhoc

| | |
|---|---|
| Suite | `adhoc` |
| Target | http://127.0.0.1:6750 |
| Started | 2026-11-18T03:20:53.794Z |
| Duration | 11.8 s |
| Result | 4 passed · 0 failed · 0 timed out · 3 skipped |
| Operator | claude · harness 7c790e44baab · node v24.18.0 · playwright 1.63.0 |

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ✅ passed | — | dashboard | 2.1 s | — | — |
| ✅ passed | — | create from folder: the preview lists both repositories with their location | 3.1 s | — | — |
| ⏭️ skipped | — | board (with the Story chip when a Story is on it) | 118 ms | — | — |
| ⏭️ skipped | — | task detail with the Story panel, then the Discuss tab | 118 ms | — | — |
| ✅ passed | — | Settings → Trackers | 2.5 s | — | — |
| ⏭️ skipped | — | project settings: the ticket source card with status sync | 89 ms | — | — |
| ✅ passed | — | Machine page | 2.2 s | — | — |

## Skipped

- board (with the Story chip when a Story is on it) — no e2e project — run the workflow spec first
- task detail with the Story panel, then the Discuss tab — no e2e project — run the workflow spec first
- project settings: the ticket source card with status sync — no e2e project — run the workflow spec first

## Cleanup

- Projects created this run: 0
- Leftovers after teardown: none
