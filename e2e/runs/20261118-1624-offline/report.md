# Run 20261118-1624-offline

| | |
|---|---|
| Suite | `offline` |
| Target | offline |
| Started | 2026-11-18T19:24:19.256Z |
| Duration | 12.7 s |
| Result | 27 passed · 0 failed · 0 timed out · 0 skipped |
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
| ✅ passed | — | epic search: by title prefix, by key, by bare number, unknown key → 400 | 40 ms | — | — |
| ✅ passed | — | polling query: assigned to me, under the epics, not done, extra clause; rendered fields; paging | 57 ms | — | — |
| ✅ passed | — | intake v2: key in (…) tracking, a missing key fails the query like Jira; subtasks, assignee, components | 16 ms | — | — |
| ✅ passed | — | comments newest-first with rendered bodies; attachments via a 303 to the media host | 28 ms | — | — |
| ✅ passed | — | write-back: status read, transitions list, POST transition 204, invalid id 400 | 43 ms | — | — |
| ✅ passed | — | boards: a project's boards, a board's filter, its JQL (ORDER BY stripped) AND-ed onto intake; a gone board 404s | 76 ms | — | — |
| ✅ passed | — | failure injection and an outage | 18 ms | — | — |
| ✅ passed | — | auth: connectionData for the PAT; a wrong PAT gets 203 + HTML like the real service | 7 ms | — | — |
| ✅ passed | — | projects, work item types with state categories, type categories | 6 ms | — | — |
| ✅ passed | — | two-step polling WIQL: hierarchy (MustContain / Recursive), then the flat filter; batch with relations | 110 ms | — | — |
| ✅ passed | — | comments (desc, deleted skipped by the runner), attachments, System.State write-back | 59 ms | — | — |
| ✅ passed | — | boards: the project's teams; a team's area paths become the intake WIQL condition; a gone team 404s | 94 ms | — | — |
| ✅ passed | `RUN-034` | list, approve (asked, then --yes) and revoke a refused repository command | 2.1 s | — | — |
| ✅ passed | `TCLI-007` `TCLI-008` `TCLI-011` | tracker test/list: the runner signs in to the fake Jira and ADO with the seeded tokens; tokens never printed | 642 ms | — | — |
| ✅ passed | `TCLI-008` | a revoked token: tracker test reports failing with Jira's advice and exits 1 | 236 ms | — | — |
| ✅ passed | `TCLI-001` | tracker add reaches a loopback site only with the runner's test opt-ins | 2.6 s | — | — |
| ✅ passed | `TCLI-009` | tracker remove deletes the entry and its token | 261 ms | — | — |
| ✅ passed | — | auth status reports the signed-in accounts the way ForgeCli.SignedInAccounts parses them | 263 ms | — | — |
| ✅ passed | — | publish: pr list finds nothing → pr create prints the URL → a retry finds the open PR (idempotent) | 353 ms | — | — |
| ✅ passed | — | state: pr view --json state --jq .state follows OPEN → MERGED (ForgeCli.NormalizeState → merged) | 363 ms | — | — |
| ✅ passed | — | a Story's pull request: create --draft, edit the checklist, ready, ready --undo (ForgeCli draft/ready/edit) | 552 ms | — | — |
| ✅ passed | — | account scoping: auth token --user prints that account's token; GH_TOKEN reaches the call record | 262 ms | — | — |
| ✅ passed | — | a configured failure and a signed-out CLI fail like gh does | 186 ms | — | — |

## Cleanup

- Projects created this run: 0
- Leftovers after teardown: none
