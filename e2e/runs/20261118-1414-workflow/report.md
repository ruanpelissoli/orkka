# Run 20261118-1414-workflow

| | |
|---|---|
| Suite | `workflow` |
| Target | http://127.0.0.1:6792 |
| Started | 2026-11-18T17:14:34.219Z |
| Duration | 6m 8s |
| Result | 5 passed · 0 failed · 0 timed out · 0 skipped |
| Operator | claude · harness uncommitted · node v24.18.0 · playwright 1.63.0 |

## Notes
- Offline run: only offline specs ran; environment-bound specs were skipped.

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ✅ passed | `LWF-002` | 1. a base folder becomes a project: api is a remote, web a local folder | 2.7 s | — | — |
| ✅ passed | `LWF-003` | 2. a Jira connection added from Settings → Trackers: the token lives only in the runner's secret store | 161 ms | — | — |
| ✅ passed | `LWF-004` `TSRC-004` | 3. the ticket source: To Do intake and the six-step status map | 162 ms | — | — |
| ✅ passed | `LWF-007` | 6. a Story with two sub-tasks: one branch, draft PR, the second builds on the first, ready at the end | 5m 36s | — | — |
| ✅ passed | `LWF-008` | 7. Azure DevOps: a PAT connection and a work item in intake | 26.2 s | — | — |

## Cleanup

- Projects created this run: 1
  - `e2e-shop-ado-mwme00e2` (01M7QTRT9ZPFS6CBJGBY7063Y7)
- Leftovers after teardown: none
