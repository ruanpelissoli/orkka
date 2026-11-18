# Run 20261026-2234-offline

| | |
|---|---|
| Suite | `offline` |
| Target | offline |
| Started | 2026-10-27T01:34:22.920Z |
| Duration | 12.8 s |
| Result | 22 passed · 0 failed · 0 timed out · 0 skipped |
| Operator | claude · harness uncommitted · node v24.18.0 · playwright 1.63.0 |

## Notes
- No environment configured (E2E_BASE_URL unset): only offline specs ran; environment-bound specs were skipped.

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ✅ passed | — | seeded from the web fixture; the alias URL clones, a branch pushes, a merge lands on main | 2.0 s | — | — |
| ✅ passed | — | custom files and default branch; a teammate's commit moves the head | 1.1 s | — | — |
| ✅ passed | — | auth: /myself answers for email+token and 401s anything else | 39 ms | — | — |
| ✅ passed | — | scopes, statuses and the Acceptance Criteria field lookup | 24 ms | — | — |
| ✅ passed | — | epic search: by title prefix, by key, by bare number, unknown key → 400 | 19 ms | — | — |
| ✅ passed | — | polling query: assigned to me, under the epics, not done, extra clause; rendered fields; paging | 30 ms | — | — |
| ✅ passed | — | comments newest-first with rendered bodies; attachments via a 303 to the media host | 18 ms | — | — |
| ✅ passed | — | write-back: status read, transitions list, POST transition 204, invalid id 400 | 35 ms | — | — |
| ✅ passed | — | failure injection and an outage | 17 ms | — | — |
| ✅ passed | — | auth: connectionData for the PAT; a wrong PAT gets 203 + HTML like the real service | 6 ms | — | — |
| ✅ passed | — | projects, work item types with state categories, type categories | 21 ms | — | — |
| ✅ passed | — | two-step polling WIQL: hierarchy (MustContain / Recursive), then the flat filter; batch with relations | 54 ms | — | — |
| ✅ passed | — | comments (desc, deleted skipped by the runner), attachments, System.State write-back | 35 ms | — | — |
| ✅ passed | `TCLI-007` `TCLI-008` `TCLI-011` | tracker test/list: the runner signs in to the fake Jira and ADO with the seeded tokens; tokens never printed | 1.5 s | — | — |
| ✅ passed | `TCLI-008` | a revoked token: tracker test reports failing with Jira's advice and exits 1 | 697 ms | — | — |
| ✅ passed | — | tracker add cannot target a loopback site: --site http://127.0.0.1:<port> is rewritten to https://127.0.0.1 (no port) | 2.3 s | — | — |
| ✅ passed | `TCLI-009` | tracker remove deletes the entry and its token | 753 ms | — | — |
| ✅ passed | — | auth status reports the signed-in accounts the way ForgeCli.SignedInAccounts parses them | 289 ms | — | — |
| ✅ passed | — | publish: pr list finds nothing → pr create prints the URL → a retry finds the open PR (idempotent) | 399 ms | — | — |
| ✅ passed | — | state: pr view --json state --jq .state follows OPEN → MERGED (ForgeCli.NormalizeState → merged) | 396 ms | — | — |
| ✅ passed | — | account scoping: auth token --user prints that account's token; GH_TOKEN reaches the call record | 299 ms | — | — |
| ✅ passed | — | a configured failure and a signed-out CLI fail like gh does | 197 ms | — | — |

## Cleanup

- Projects created this run: 0
- Leftovers after teardown: none
