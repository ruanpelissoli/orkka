# Run 20261119-1452-workflow

| | |
|---|---|
| Suite | `workflow` |
| Target | http://127.0.0.1:6760 |
| Started | 2026-11-19T17:52:36.965Z |
| Duration | 1m 38s |
| Result | 4 passed · 0 failed · 0 timed out · 3 skipped |
| Operator | claude · harness uncommitted · node v24.18.0 · playwright 1.63.0 |

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ✅ passed | `LWF-002` | 1. a base folder becomes a project: api is a remote, web a local folder | 5.3 s | — | — |
| ✅ passed | `LWF-003` | 2. a Jira connection added from Settings → Trackers: the token lives only in the runner's secret store | 447 ms | — | — |
| ✅ passed | `LWF-004` `TSRC-004` | 3. the ticket source: To Do intake and the six-step status map | 273 ms | — | — |
| ⏭️ skipped | `LWF-005` | 4. a standalone ticket: Discuss, plan, build, review, publish; Jira follows | 2 ms | — | — |
| ⏭️ skipped | `LWF-006` | 5. a ticket that names no repository: the question routes it; moving it in Jira does not retire it | 2 ms | — | — |
| ⏭️ skipped | `LWF-007` | 6. a Story with two sub-tasks: one branch, draft PR, the second builds on the first, ready at the end | 3 ms | — | — |
| ✅ passed | `LWF-008` `PSU-009` `PSU-010` | 7. Azure DevOps: a PAT connection, the dry run, nothing before Start, then the work item | 1m 27s | — | — |

## Skipped

- 4. a standalone ticket: Discuss, plan, build, review, publish; Jira follows — real agent runs — set E2E_WORKFLOW_AGENTS=1
- 5. a ticket that names no repository: the question routes it; moving it in Jira does not retire it — real agent runs — set E2E_WORKFLOW_AGENTS=1
- 6. a Story with two sub-tasks: one branch, draft PR, the second builds on the first, ready at the end — real agent runs — set E2E_WORKFLOW_AGENTS=1

## Cleanup

- Projects created this run: 2
  - `e2e-shop-mwnulk72` (01M7TF0XQFVX9C2K857Q2E4665)
  - `e2e-shop-ado-mwnuloqq` (01M7TF136ZBNRF5EEGTE5ES10C)
- Leftovers after teardown: none
