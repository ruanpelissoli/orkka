# Run 20261120-1415-offline

| | |
|---|---|
| Suite | `offline` |
| Target | http://127.0.0.1:6760 |
| Started | 2026-11-20T17:15:53.594Z |
| Duration | 24.1 s |
| Result | 35 passed · 1 failed · 0 timed out · 0 skipped |
| Operator | claude · harness uncommitted · node v24.18.0 · playwright 1.63.0 |

## Notes
- Playwright overall status: failed
- Offline run: only offline specs ran; environment-bound specs were skipped.

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ✅ passed | — | seeded from the web fixture; the alias URL clones, a branch pushes, a merge lands on main | 2.1 s | — | — |
| ✅ passed | — | custom files and default branch; a teammate's commit moves the head | 1.1 s | — | — |
| ✅ passed | — | auth and scopes: viewer for the Bearer token with X-OAuth-Scopes; anything else 401 | 48 ms | — | — |
| ✅ passed | — | owners, an owner's open and closed projects, a project's single-select fields | 28 ms | — | — |
| ❌ failed | — | items: paged, every type with its status; drafts and pull requests have no Issue content | 14 ms | — | — |
| ✅ passed | — | issues by key: aliased batch; unknown repository or number → null + NOT_FOUND; status on the project | 43 ms | — | — |
| ✅ passed | — | comments newest last; write-back looks the item up, adds a missing one, sets the Status option | 31 ms | — | — |
| ✅ passed | — | error knobs: SAML SSO FORBIDDEN, a secondary rate limit with Retry-After, an outage | 46 ms | — | — |
| ✅ passed | — | auth: /myself answers for email+token and 401s anything else | 8 ms | — | — |
| ✅ passed | — | scopes, statuses and the Acceptance Criteria field lookup | 29 ms | — | — |
| ✅ passed | — | epic search: by title prefix, by key, by bare number, unknown key → 400 | 35 ms | — | — |
| ✅ passed | — | polling query: assigned to me, under the epics, not done, extra clause; rendered fields; paging | 58 ms | — | — |
| ✅ passed | — | intake v2: key in (…) tracking, a missing key fails the query like Jira; subtasks, assignee, components | 11 ms | — | — |
| ✅ passed | — | comments newest-first with rendered bodies; attachments via a 303 to the media host | 40 ms | — | — |
| ✅ passed | — | write-back: status read, transitions list, POST transition 204, invalid id 400 | 70 ms | — | — |
| ✅ passed | — | boards: a project's boards, a board's filter, its JQL (ORDER BY stripped) AND-ed onto intake; a gone board 404s | 88 ms | — | — |
| ✅ passed | — | failure injection and an outage | 25 ms | — | — |
| ✅ passed | — | auth: connectionData for the PAT; a wrong PAT gets 203 + HTML like the real service | 5 ms | — | — |
| ✅ passed | — | projects, work item types with state categories, type categories | 31 ms | — | — |
| ✅ passed | — | two-step polling WIQL: hierarchy (MustContain / Recursive), then the flat filter; batch with relations | 75 ms | — | — |
| ✅ passed | — | comments (desc, deleted skipped by the runner), attachments, System.State write-back | 51 ms | — | — |
| ✅ passed | — | boards: the project's teams; a team's area paths become the intake WIQL condition; a gone team 404s | 105 ms | — | — |
| ✅ passed | `RUN-034` | list, approve (asked, then --yes) and revoke a refused repository command | 1.5 s | — | — |
| ✅ passed | `TCLI-007` `TCLI-008` `TCLI-011` | tracker test/list: the runner signs in to the fake Jira and ADO with the seeded tokens; tokens never printed | 732 ms | — | — |
| ✅ passed | `TCLI-008` | a revoked token: tracker test reports failing with Jira's advice and exits 1 | 518 ms | — | — |
| ✅ passed | `TCLI-001` | tracker add reaches a loopback site only with the runner's test opt-ins | 3.7 s | — | — |
| ✅ passed | `TCLI-014` | tracker add github --token: the token is tested against the fake GitHub and stored; never printed | 1.3 s | — | — |
| ✅ passed | `TCLI-015` | tracker add github without --token uses the gh login: nothing stored; a missing project scope says what to run | 3.3 s | — | — |
| ✅ passed | `TCLI-014` | tracker add github refuses a GitHub Enterprise Server host | 284 ms | — | — |
| ✅ passed | `TCLI-009` | tracker remove deletes the entry and its token | 857 ms | — | — |
| ✅ passed | — | auth status reports the signed-in accounts the way ForgeCli.SignedInAccounts parses them | 978 ms | — | — |
| ✅ passed | — | publish: pr list finds nothing → pr create prints the URL → a retry finds the open PR (idempotent) | 1.1 s | — | — |
| ✅ passed | — | state: pr view --json state --jq .state follows OPEN → MERGED (ForgeCli.NormalizeState → merged) | 911 ms | — | — |
| ✅ passed | — | a Story's pull request: create --draft, edit the checklist, ready, ready --undo (ForgeCli draft/ready/edit) | 1.1 s | — | — |
| ✅ passed | — | account scoping: auth token --user prints that account's token; GH_TOKEN reaches the call record | 541 ms | — | — |
| ✅ passed | — | a configured failure and a signed-out CLI fail like gh does | 344 ms | — | — |

## Failures

### ❌ items: paged, every type with its status; drafts and pull requests have no Issue content

- File: `tests/offline/fake-github.spec.ts`
- Trace: `..\current\artifacts\offline-fake-github-fake-G-a329b-uests-have-no-Issue-content-chromium\trace.zip` (open with `npx playwright show-trace <path>`)

```
Error: [2mexpect([22m[31mreceived[39m[2m).[22mtoEqual[2m([22m[32mexpected[39m[2m) // deep equality[22m

[32m- Expected  - 0[39m
[31m+ Received  + 6[39m

[33m@@ -2,13 +2,19 @@[39m
[2m    Object {[22m
[2m      "number": 5,[22m
[2m      "repository": Object {[22m
[2m        "nameWithOwner": "e2e-local/app",[22m
[2m      },[22m
[31m+     "subIssuesSummary": Object {[39m
```
Logs: API + Worker console output of the stack at http://127.0.0.1:6760; runner log .runs/runner.log

## Cleanup

- Projects created this run: 0
- Leftovers after teardown: none
