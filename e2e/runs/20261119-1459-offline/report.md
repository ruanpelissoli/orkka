# Run 20261119-1459-offline

| | |
|---|---|
| Suite | `offline` |
| Target | http://127.0.0.1:6760 |
| Started | 2026-11-19T17:59:30.437Z |
| Duration | 17.3 s |
| Result | 36 passed · 0 failed · 0 timed out · 0 skipped |
| Operator | claude · harness uncommitted · node v24.18.0 · playwright 1.63.0 |

## Notes
- Offline run: only offline specs ran; environment-bound specs were skipped.

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ✅ passed | — | seeded from the web fixture; the alias URL clones, a branch pushes, a merge lands on main | 2.6 s | — | — |
| ✅ passed | — | custom files and default branch; a teammate's commit moves the head | 1.5 s | — | — |
| ✅ passed | — | auth and scopes: viewer for the Bearer token with X-OAuth-Scopes; anything else 401 | 45 ms | — | — |
| ✅ passed | — | owners, an owner's open and closed projects, a project's single-select fields | 49 ms | — | — |
| ✅ passed | — | items: paged, every type with its status; drafts and pull requests have no Issue content | 12 ms | — | — |
| ✅ passed | — | issues by key: aliased batch; unknown repository or number → null + NOT_FOUND; status on the project | 9 ms | — | — |
| ✅ passed | — | comments newest last; write-back looks the item up, adds a missing one, sets the Status option | 31 ms | — | — |
| ✅ passed | — | error knobs: SAML SSO FORBIDDEN, a secondary rate limit with Retry-After, an outage | 38 ms | — | — |
| ✅ passed | — | auth: /myself answers for email+token and 401s anything else | 8 ms | — | — |
| ✅ passed | — | scopes, statuses and the Acceptance Criteria field lookup | 29 ms | — | — |
| ✅ passed | — | epic search: by title prefix, by key, by bare number, unknown key → 400 | 42 ms | — | — |
| ✅ passed | — | polling query: assigned to me, under the epics, not done, extra clause; rendered fields; paging | 53 ms | — | — |
| ✅ passed | — | intake v2: key in (…) tracking, a missing key fails the query like Jira; subtasks, assignee, components | 23 ms | — | — |
| ✅ passed | — | comments newest-first with rendered bodies; attachments via a 303 to the media host | 28 ms | — | — |
| ✅ passed | — | write-back: status read, transitions list, POST transition 204, invalid id 400 | 53 ms | — | — |
| ✅ passed | — | boards: a project's boards, a board's filter, its JQL (ORDER BY stripped) AND-ed onto intake; a gone board 404s | 85 ms | — | — |
| ✅ passed | — | failure injection and an outage | 20 ms | — | — |
| ✅ passed | — | auth: connectionData for the PAT; a wrong PAT gets 203 + HTML like the real service | 5 ms | — | — |
| ✅ passed | — | projects, work item types with state categories, type categories | 37 ms | — | — |
| ✅ passed | — | two-step polling WIQL: hierarchy (MustContain / Recursive), then the flat filter; batch with relations | 76 ms | — | — |
| ✅ passed | — | comments (desc, deleted skipped by the runner), attachments, System.State write-back | 75 ms | — | — |
| ✅ passed | — | boards: the project's teams; a team's area paths become the intake WIQL condition; a gone team 404s | 71 ms | — | — |
| ✅ passed | `RUN-034` | list, approve (asked, then --yes) and revoke a refused repository command | 1.6 s | — | — |
| ✅ passed | `TCLI-007` `TCLI-008` `TCLI-011` | tracker test/list: the runner signs in to the fake Jira and ADO with the seeded tokens; tokens never printed | 769 ms | — | — |
| ✅ passed | `TCLI-008` | a revoked token: tracker test reports failing with Jira's advice and exits 1 | 285 ms | — | — |
| ✅ passed | `TCLI-001` | tracker add reaches a loopback site only with the runner's test opt-ins | 2.7 s | — | — |
| ✅ passed | `TCLI-014` | tracker add github --token: the token is tested against the fake GitHub and stored; never printed | 377 ms | — | — |
| ✅ passed | `TCLI-015` | tracker add github without --token uses the gh login: nothing stored; a missing project scope says what to run | 1.2 s | — | — |
| ✅ passed | `TCLI-014` | tracker add github refuses a GitHub Enterprise Server host | 133 ms | — | — |
| ✅ passed | `TCLI-009` | tracker remove deletes the entry and its token | 386 ms | — | — |
| ✅ passed | — | auth status reports the signed-in accounts the way ForgeCli.SignedInAccounts parses them | 375 ms | — | — |
| ✅ passed | — | publish: pr list finds nothing → pr create prints the URL → a retry finds the open PR (idempotent) | 490 ms | — | — |
| ✅ passed | — | state: pr view --json state --jq .state follows OPEN → MERGED (ForgeCli.NormalizeState → merged) | 516 ms | — | — |
| ✅ passed | — | a Story's pull request: create --draft, edit the checklist, ready, ready --undo (ForgeCli draft/ready/edit) | 778 ms | — | — |
| ✅ passed | — | account scoping: auth token --user prints that account's token; GH_TOKEN reaches the call record | 406 ms | — | — |
| ✅ passed | — | a configured failure and a signed-out CLI fail like gh does | 257 ms | — | — |

## Cleanup

- Projects created this run: 0
- Leftovers after teardown: none
