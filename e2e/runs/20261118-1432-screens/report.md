# Run 20261118-1432-screens

| | |
|---|---|
| Suite | `screens` |
| Target | http://127.0.0.1:6792 |
| Started | 2026-11-18T17:32:33.288Z |
| Duration | 28.1 s |
| Result | 8 passed · 0 failed · 0 timed out · 0 skipped |
| Operator | claude · harness uncommitted · node v24.18.0 · playwright 1.63.0 |

## Notes
- E2E_KEEP_PROJECTS set: no pre-run sweep, no teardown deletes.

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ✅ passed | `LWF-009` | dashboard | 2.3 s | — | — |
| ✅ passed | — | create from folder: the preview lists both repositories with their location | 3.8 s | — | — |
| ✅ passed | — | board (with the Story chip when a Story is on it) | 3.3 s | — | — |
| ✅ passed | — | board in Portuguese: the top-bar breadcrumb follows the locale | 2.3 s | — | — |
| ✅ passed | — | task detail with the Story panel, then the Discuss tab | 5.0 s | — | — |
| ✅ passed | — | Settings → Trackers | 2.2 s | — | — |
| ✅ passed | — | project settings: the ticket source card with status sync | 5.3 s | — | — |
| ✅ passed | — | Machine page | 2.1 s | — | — |

## Cleanup

- Projects created this run: 0
- Leftovers after teardown: none
