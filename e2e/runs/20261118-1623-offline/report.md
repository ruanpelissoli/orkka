# Run 20261118-1623-offline

| | |
|---|---|
| Suite | `offline` |
| Target | offline |
| Started | 2026-11-18T19:23:40.783Z |
| Duration | 7.4 s |
| Result | 22 passed · 0 failed · 0 timed out · 5 skipped |
| Operator | claude · harness uncommitted · node v24.18.0 · playwright 1.63.0 |

## Notes
- Offline run: only offline specs ran; environment-bound specs were skipped.

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ✅ passed | — | seeded from the web fixture; the alias URL clones, a branch pushes, a merge lands on main | 1.8 s | — | — |
| ✅ passed | — | custom files and default branch; a teammate's commit moves the head | 1.0 s | — | — |
| ✅ passed | — | auth: /myself answers for email+token and 401s anything else | 24 ms | — | — |
| ✅ passed | — | scopes, statuses and the Acceptance Criteria field lookup | 30 ms | — | — |
| ✅ passed | — | epic search: by title prefix, by key, by bare number, unknown key → 400 | 45 ms | — | — |
| ✅ passed | — | polling query: assigned to me, under the epics, not done, extra clause; rendered fields; paging | 46 ms | — | — |
| ✅ passed | — | intake v2: key in (…) tracking, a missing key fails the query like Jira; subtasks, assignee, components | 19 ms | — | — |
| ✅ passed | — | comments newest-first with rendered bodies; attachments via a 303 to the media host | 28 ms | — | — |
| ✅ passed | — | write-back: status read, transitions list, POST transition 204, invalid id 400 | 62 ms | — | — |
| ✅ passed | — | boards: a project's boards, a board's filter, its JQL (ORDER BY stripped) AND-ed onto intake; a gone board 404s | 52 ms | — | — |
| ✅ passed | — | failure injection and an outage | 7 ms | — | — |
| ✅ passed | — | auth: connectionData for the PAT; a wrong PAT gets 203 + HTML like the real service | 4 ms | — | — |
| ✅ passed | — | projects, work item types with state categories, type categories | 37 ms | — | — |
| ✅ passed | — | two-step polling WIQL: hierarchy (MustContain / Recursive), then the flat filter; batch with relations | 74 ms | — | — |
| ✅ passed | — | comments (desc, deleted skipped by the runner), attachments, System.State write-back | 70 ms | — | — |
| ✅ passed | — | boards: the project's teams; a team's area paths become the intake WIQL condition; a gone team 404s | 69 ms | — | — |
| ⏭️ skipped | `RUN-034` | list, approve (asked, then --yes) and revoke a refused repository command | 0 ms | — | — |
| ⏭️ skipped | `TCLI-007` `TCLI-008` `TCLI-011` | tracker test/list: the runner signs in to the fake Jira and ADO with the seeded tokens; tokens never printed | 1 ms | — | — |
| ⏭️ skipped | `TCLI-008` | a revoked token: tracker test reports failing with Jira's advice and exits 1 | 1 ms | — | — |
| ⏭️ skipped | `TCLI-001` | tracker add reaches a loopback site only with the runner's test opt-ins | 1 ms | — | — |
| ⏭️ skipped | `TCLI-009` | tracker remove deletes the entry and its token | 1 ms | — | — |
| ✅ passed | — | auth status reports the signed-in accounts the way ForgeCli.SignedInAccounts parses them | 602 ms | — | — |
| ✅ passed | — | publish: pr list finds nothing → pr create prints the URL → a retry finds the open PR (idempotent) | 354 ms | — | — |
| ✅ passed | — | state: pr view --json state --jq .state follows OPEN → MERGED (ForgeCli.NormalizeState → merged) | 399 ms | — | — |
| ✅ passed | — | a Story's pull request: create --draft, edit the checklist, ready, ready --undo (ForgeCli draft/ready/edit) | 521 ms | — | — |
| ✅ passed | — | account scoping: auth token --user prints that account's token; GH_TOKEN reaches the call record | 266 ms | — | — |
| ✅ passed | — | a configured failure and a signed-out CLI fail like gh does | 177 ms | — | — |

## Skipped

- list, approve (asked, then --yes) and revoke a refused repository command — no ./orkka with the commands verb (no build at C:\Projects\orkka\orkka-boards\src\Orkka\bin\Release\net10.0\orkka.dll)
- tracker test/list: the runner signs in to the fake Jira and ADO with the seeded tokens; tokens never printed — no ./orkka with tracker support (no build at C:\Projects\orkka\orkka-boards\src\Orkka\bin\Release\net10.0\orkka.dll)
- a revoked token: tracker test reports failing with Jira's advice and exits 1 — no ./orkka with tracker support
- tracker add reaches a loopback site only with the runner's test opt-ins — no ./orkka with tracker support
- tracker remove deletes the entry and its token — no ./orkka with tracker support

## Cleanup

- Projects created this run: 0
- Leftovers after teardown: none
