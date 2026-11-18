# Run 20261118-0038-adhoc

| | |
|---|---|
| Suite | `adhoc` |
| Target | http://127.0.0.1:6750 |
| Started | 2026-11-18T03:38:18.414Z |
| Duration | 21.5 s |
| Result | 7 passed · 0 failed · 0 timed out · 0 skipped |
| Operator | claude · harness 7c790e44baab · node v24.18.0 · playwright 1.63.0 |

## Notes
- E2E_KEEP_PROJECTS set: no pre-run sweep, no teardown deletes.

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ✅ passed | — | dashboard | 2.5 s | — | — |
| ✅ passed | — | create from folder: the preview lists both repositories with their location | 3.1 s | — | — |
| ✅ passed | — | board (with the Story chip when a Story is on it) | 2.4 s | — | — |
| ✅ passed | — | task detail with the Story panel, then the Discuss tab | 5.1 s | — | — |
| ✅ passed | — | Settings → Trackers | 2.4 s | — | — |
| ✅ passed | — | project settings: the ticket source card with status sync | 2.4 s | — | — |
| ✅ passed | — | Machine page | 2.1 s | — | — |

## Cleanup

- Projects created this run: 0
- Leftovers after teardown: none
