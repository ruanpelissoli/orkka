# Run 20261118-1324-workflow

| | |
|---|---|
| Suite | `workflow` |
| Target | http://127.0.0.1:6792 |
| Started | 2026-11-18T16:24:48.097Z |
| Duration | 31.5 s |
| Result | 4 passed · 0 failed · 0 timed out · 3 skipped |
| Operator | claude · harness uncommitted · node v24.18.0 · playwright 1.63.0 |

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ✅ passed | `LWF-002` | 1. a base folder becomes a project: api is a remote, web a local folder | 2.3 s | — | — |
| ✅ passed | `LWF-003` | 2. a Jira connection added from Settings → Trackers: the token lives only in the runner's secret store | 162 ms | — | — |
| ✅ passed | `LWF-004` `TSRC-004` | 3. the ticket source: To Do intake and the six-step status map | 209 ms | — | — |
| ⏭️ skipped | `LWF-005` | 4. a standalone ticket: Discuss, plan, build, review, publish; Jira follows | 3 ms | — | — |
| ⏭️ skipped | `LWF-006` | 5. a ticket that names no repository: the question routes it; moving it in Jira does not retire it | 3 ms | — | — |
| ⏭️ skipped | `LWF-007` | 6. a Story with two sub-tasks: one branch, draft PR, the second builds on the first, ready at the end | 3 ms | — | — |
| ✅ passed | `LWF-008` | 7. Azure DevOps: a PAT connection and a work item in intake | 26.2 s | — | — |

## Skipped

- 4. a standalone ticket: Discuss, plan, build, review, publish; Jira follows — real agent runs — set E2E_WORKFLOW_AGENTS=1
- 5. a ticket that names no repository: the question routes it; moving it in Jira does not retire it — real agent runs — set E2E_WORKFLOW_AGENTS=1
- 6. a Story with two sub-tasks: one branch, draft PR, the second builds on the first, ready at the end — real agent runs — set E2E_WORKFLOW_AGENTS=1

## Cleanup

- Projects created this run: 2
  - `e2e-shop-mwmc0qkm` (01M7QQKAZF8K9QSSH38K4K181S)
  - `e2e-shop-ado-mwmc0smg` (01M7QQKDJQ3D6BQ4ZV2S75H8Y2)
- Leftovers after teardown: none
