# Run 20261118-1710-offline

| | |
|---|---|
| Suite | `offline` |
| Target | offline |
| Started | 2026-11-18T20:10:30.555Z |
| Duration | 27.4 s |
| Result | 22 passed · 0 failed · 0 timed out · 5 skipped |
| Operator | claude · harness uncommitted · node v24.18.0 · playwright 1.63.0 |

## Notes
- Offline run: only offline specs ran; environment-bound specs were skipped.

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ✅ passed | — | seeded from the web fixture; the alias URL clones, a branch pushes, a merge lands on main | 6.4 s | — | — |
| ✅ passed | — | custom files and default branch; a teammate's commit moves the head | 3.6 s | — | — |
| ✅ passed | — | auth: /myself answers for email+token and 401s anything else | 111 ms | — | — |
| ✅ passed | — | scopes, statuses and the Acceptance Criteria field lookup | 41 ms | — | — |
| ✅ passed | — | epic search: by title prefix, by key, by bare number, unknown key → 400 | 97 ms | — | — |
| ✅ passed | — | polling query: assigned to me, under the epics, not done, extra clause; rendered fields; paging | 129 ms | — | — |
| ✅ passed | — | intake v2: key in (…) tracking, a missing key fails the query like Jira; subtasks, assignee, components | 51 ms | — | — |
| ✅ passed | — | comments newest-first with rendered bodies; attachments via a 303 to the media host | 71 ms | — | — |
| ✅ passed | — | write-back: status read, transitions list, POST transition 204, invalid id 400 | 84 ms | — | — |
| ✅ passed | — | boards: a project's boards, a board's filter, its JQL (ORDER BY stripped) AND-ed onto intake; a gone board 404s | 75 ms | — | — |
| ✅ passed | — | failure injection and an outage | 41 ms | — | — |
| ✅ passed | — | auth: connectionData for the PAT; a wrong PAT gets 203 + HTML like the real service | 22 ms | — | — |
| ✅ passed | — | projects, work item types with state categories, type categories | 66 ms | — | — |
| ✅ passed | — | two-step polling WIQL: hierarchy (MustContain / Recursive), then the flat filter; batch with relations | 146 ms | — | — |
| ✅ passed | — | comments (desc, deleted skipped by the runner), attachments, System.State write-back | 97 ms | — | — |
| ✅ passed | — | boards: the project's teams; a team's area paths become the intake WIQL condition; a gone team 404s | 93 ms | — | — |
| ⏭️ skipped | `RUN-034` | list, approve (asked, then --yes) and revoke a refused repository command | 4 ms | — | — |
| ⏭️ skipped | `TCLI-007` `TCLI-008` `TCLI-011` | tracker test/list: the runner signs in to the fake Jira and ADO with the seeded tokens; tokens never printed | 6 ms | — | — |
| ⏭️ skipped | `TCLI-008` | a revoked token: tracker test reports failing with Jira's advice and exits 1 | 5 ms | — | — |
| ⏭️ skipped | `TCLI-001` | tracker add reaches a loopback site only with the runner's test opt-ins | 5 ms | — | — |
| ⏭️ skipped | `TCLI-009` | tracker remove deletes the entry and its token | 9 ms | — | — |
| ✅ passed | — | auth status reports the signed-in accounts the way ForgeCli.SignedInAccounts parses them | 2.5 s | — | — |
| ✅ passed | — | publish: pr list finds nothing → pr create prints the URL → a retry finds the open PR (idempotent) | 1.4 s | — | — |
| ✅ passed | — | state: pr view --json state --jq .state follows OPEN → MERGED (ForgeCli.NormalizeState → merged) | 1.4 s | — | — |
| ✅ passed | — | a Story's pull request: create --draft, edit the checklist, ready, ready --undo (ForgeCli draft/ready/edit) | 2.1 s | — | — |
| ✅ passed | — | account scoping: auth token --user prints that account's token; GH_TOKEN reaches the call record | 923 ms | — | — |
| ✅ passed | — | a configured failure and a signed-out CLI fail like gh does | 557 ms | — | — |

## Skipped

- list, approve (asked, then --yes) and revoke a refused repository command — no ./orkka with the commands verb (no build at C:\Projects\orkka\orkka-storyfix\src\Orkka\bin\Release\net10.0\orkka.dll)
- tracker test/list: the runner signs in to the fake Jira and ADO with the seeded tokens; tokens never printed — no ./orkka with tracker support (no build at C:\Projects\orkka\orkka-storyfix\src\Orkka\bin\Release\net10.0\orkka.dll)
- a revoked token: tracker test reports failing with Jira's advice and exits 1 — no ./orkka with tracker support
- tracker add reaches a loopback site only with the runner's test opt-ins — no ./orkka with tracker support
- tracker remove deletes the entry and its token — no ./orkka with tracker support

## Cleanup

- Projects created this run: 0
- Leftovers after teardown: none
