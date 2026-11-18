# Run 20261117-1701-offline

| | |
|---|---|
| Suite | `offline` |
| Target | offline |
| Started | 2026-11-17T20:01:22.581Z |
| Duration | 11.3 s |
| Result | 23 passed · 0 failed · 0 timed out · 0 skipped |
| Operator | claude · harness 26f20cbbc33d · node v24.18.0 · playwright 1.63.0 |

## Notes
- No environment configured (E2E_BASE_URL unset): only offline specs ran; environment-bound specs were skipped.

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ✅ passed | — | seeded from the web fixture; the alias URL clones, a branch pushes, a merge lands on main | 1.6 s | — | — |
| ✅ passed | — | custom files and default branch; a teammate's commit moves the head | 931 ms | — | — |
| ✅ passed | — | auth: /myself answers for email+token and 401s anything else | 24 ms | — | — |
| ✅ passed | — | scopes, statuses and the Acceptance Criteria field lookup | 30 ms | — | — |
| ✅ passed | — | epic search: by title prefix, by key, by bare number, unknown key → 400 | 41 ms | — | — |
| ✅ passed | — | polling query: assigned to me, under the epics, not done, extra clause; rendered fields; paging | 28 ms | — | — |
| ✅ passed | — | comments newest-first with rendered bodies; attachments via a 303 to the media host | 25 ms | — | — |
| ✅ passed | — | write-back: status read, transitions list, POST transition 204, invalid id 400 | 36 ms | — | — |
| ✅ passed | — | failure injection and an outage | 10 ms | — | — |
| ✅ passed | — | auth: connectionData for the PAT; a wrong PAT gets 203 + HTML like the real service | 3 ms | — | — |
| ✅ passed | — | projects, work item types with state categories, type categories | 38 ms | — | — |
| ✅ passed | — | two-step polling WIQL: hierarchy (MustContain / Recursive), then the flat filter; batch with relations | 73 ms | — | — |
| ✅ passed | — | comments (desc, deleted skipped by the runner), attachments, System.State write-back | 45 ms | — | — |
| ✅ passed | `RUN-034` | list, approve (asked, then --yes) and revoke a refused repository command | 1.2 s | — | — |
| ✅ passed | `TCLI-007` `TCLI-008` `TCLI-011` | tracker test/list: the runner signs in to the fake Jira and ADO with the seeded tokens; tokens never printed | 552 ms | — | — |
| ✅ passed | `TCLI-008` | a revoked token: tracker test reports failing with Jira's advice and exits 1 | 229 ms | — | — |
| ✅ passed | `TCLI-001` | tracker add reaches a loopback site only with the runner's test opt-ins | 2.6 s | — | — |
| ✅ passed | `TCLI-009` | tracker remove deletes the entry and its token | 277 ms | — | — |
| ✅ passed | — | auth status reports the signed-in accounts the way ForgeCli.SignedInAccounts parses them | 288 ms | — | — |
| ✅ passed | — | publish: pr list finds nothing → pr create prints the URL → a retry finds the open PR (idempotent) | 327 ms | — | — |
| ✅ passed | — | state: pr view --json state --jq .state follows OPEN → MERGED (ForgeCli.NormalizeState → merged) | 329 ms | — | — |
| ✅ passed | — | account scoping: auth token --user prints that account's token; GH_TOKEN reaches the call record | 247 ms | — | — |
| ✅ passed | — | a configured failure and a signed-out CLI fail like gh does | 167 ms | — | — |

## Cleanup

- Projects created this run: 0
- Leftovers after teardown: none
