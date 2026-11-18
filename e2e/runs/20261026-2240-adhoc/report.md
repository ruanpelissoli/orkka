# Run 20261026-2240-adhoc

| | |
|---|---|
| Suite | `adhoc` |
| Target | offline |
| Started | 2026-10-27T01:40:21.073Z |
| Duration | 13.2 s |
| Result | 22 passed · 0 failed · 0 timed out · 102 skipped |
| Operator | claude · harness uncommitted · node v24.18.0 · playwright 1.63.0 |

## Notes
- No environment configured (E2E_BASE_URL unset): only offline specs ran; environment-bound specs were skipped.

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ⏭️ skipped | `TSRC-001` | TSRC-001 a project with no ticket source answers 204 | 0 ms | — | — |
| ⏭️ skipped | `TSRC-002` | TSRC-002 PUT with a connection that is not mine → 400 on trackerConnectionId | 0 ms | — | — |
| ⏭️ skipped | `TSRC-003` | TSRC-003 validation: poll interval, branch template, epics, subfolder, scope | 0 ms | — | — |
| ⏭️ skipped | `TSRC-005` | TSRC-005 DELETE without a ticket source is a no-op 204; GET stays 204 | 0 ms | — | — |
| ⏭️ skipped | `TSRC-007` | TSRC-007 tracker query with an unknown kind → 400 | 0 ms | — | — |
| ⏭️ skipped | `TSRC-008` | TSRC-008 tracker query on an unknown connection → 404 (not 403) | 0 ms | — | — |
| ⏭️ skipped | `TSRC-009` | TSRC-009 GET /api/trackers/connections lists my connections with metadata only | 0 ms | — | — |
| ⏭️ skipped | `TSRC-006` | TSRC-006 a query while my runner is offline answers supported:false, not an error | 0 ms | — | — |
| ⏭️ skipped | `WSP-001` | WSP-001 GET /api/me/workspaces lists my workspaces with exactly one active | 0 ms | — | — |
| ⏭️ skipped | `WSP-003` | WSP-003 switching the active workspace answers 204; an unknown org answers 404 | 0 ms | — | — |
| ⏭️ skipped | `WSP-004` | WSP-004 a non-ULID X-Orkka-Workspace answers 400 Invalid workspace. | 0 ms | — | — |
| ⏭️ skipped | `WSP-005` | WSP-005 naming a workspace I am not in answers 403 | 0 ms | — | — |
| ⏭️ skipped | `WSP-006` | WSP-006 the same call in two workspaces returns disjoint projects | 0 ms | — | — |
| ⏭️ skipped | `WSP-007` | WSP-007 an API key ignores X-Orkka-Workspace and stays in its own org | 0 ms | — | — |
| ⏭️ skipped | `WSP-002` | WSP-002 creating a workspace makes it mine and active (restores the previous active one) | 0 ms | — | — |
| ⏭️ skipped | `HYF-001` | HYF-001 browse the runner's file system through the relay | 0 ms | — | — |
| ⏭️ skipped | `HYF-004` | HYF-004 link a git folder from this machine: provider local, execution on the runner | 0 ms | — | — |
| ⏭️ skipped | `HYF-005` | HYF-005 link a plain (non-git) folder: Orkka initialises the repository in place | 0 ms | — | — |
| ⏭️ skipped | `HYF-006` | HYF-006 linking a folder that does not exist answers 4xx with a reason | 0 ms | — | — |
| ⏭️ skipped | `HYF-010` | HYF-010 sync-status of a freshly linked folder: no remote, nothing to push | 0 ms | — | — |
| ⏭️ skipped | `HYF-013` `HYF-011` | HYF-013 connect the folder to an EMPTY remote the developer created and push | 0 ms | — | — |
| ⏭️ skipped | `HYF-014` | HYF-014 connecting to a remote that already has commits is refused with a reason | 0 ms | — | — |
| ⏭️ skipped | `HYF-024` `HYF-018` `FRG-013` | HYF-024 a task on a linked folder lands in the working copy on this machine | 0 ms | — | — |
| ⏭️ skipped | `HYB-016` `HYB-017` `HYB-019` `HYB-046` `FRG-001` `FRG-002` `PR-006` | HYB-046 hands-free task → Done → PR published via gh on the developer's account | 0 ms | — | — |
| ⏭️ skipped | `HYB-024` `PLN-001` `PLN-003` `HYB-027` `HYB-021` `PR-007` | HYB-024 human loop: plan waits in PlanReady, approval resumes, request changes, approve publishes | 0 ms | — | — |
| ⏭️ skipped | `FRG-003` | FRG-003 gh signed out: publishing fails with the gh auth login remedy and nothing is created | 0 ms | — | — |
| ⏭️ skipped | `HYB-002` | HYB-002 unknown execution preference target is rejected with 400 | 0 ms | — | — |
| ⏭️ skipped | `HYB-008` `RURL-001` | HYB-008 create a project and add a remote by URL: GitHub detected from the host | 0 ms | — | — |
| ⏭️ skipped | `RURL-002` | RURL-002 ssh, scp-style and host-alias remotes are accepted; an alias reads as Other | 0 ms | — | — |
| ⏭️ skipped | `RURL-003` | RURL-003 a folder path in the URL field is refused (link folders through the runner) | 0 ms | — | — |
| ⏭️ skipped | `RURL-004` | RURL-004 provider override and CLI account are stored | 0 ms | — | — |
| ⏭️ skipped | `RURL-005` | RURL-005 credentials embedded in an https URL are refused | 0 ms | — | — |
| ⏭️ skipped | `HYB-009` `RURL-008` | HYB-009 access preflight: the sandboxed runner reads the remote with its own git | 0 ms | — | — |
| ⏭️ skipped | `HYB-010` `RURL-009` | HYB-010 access preflight names an unreadable remote with a reason | 0 ms | — | — |
| ⏭️ skipped | `HYB-004` | HYB-004 execution-target preview: a task runs on the developer's runner | 0 ms | — | — |
| ⏭️ skipped | `REPO-008` | REPO-008 the file viewer reads the remote through the runner relay | 0 ms | — | — |
| ⏭️ skipped | `RUN-001` | RUN-001 Runner settings page shows the runner online with its machine and providers | 0 ms | — | — |
| ⏭️ skipped | `BRD-001` | BRD-001 board renders the nine lanes and the new-task entry point for a fresh project | 0 ms | — | — |
| ⏭️ skipped | — | create a task from the form as a draft and see it on the board and the detail page | 0 ms | — | — |
| ⏭️ skipped | — | task detail shows the runner execution badge for a UserRunner task | 0 ms | — | — |
| ⏭️ skipped | `SET-012` | SET-010 project settings: pipeline toggles persist through the UI and the API sees them | 0 ms | — | — |
| ✅ passed | — | seeded from the web fixture; the alias URL clones, a branch pushes, a merge lands on main | 1.9 s | — | — |
| ✅ passed | — | custom files and default branch; a teammate's commit moves the head | 1.1 s | — | — |
| ✅ passed | — | auth: /myself answers for email+token and 401s anything else | 30 ms | — | — |
| ✅ passed | — | scopes, statuses and the Acceptance Criteria field lookup | 36 ms | — | — |
| ✅ passed | — | epic search: by title prefix, by key, by bare number, unknown key → 400 | 23 ms | — | — |
| ✅ passed | — | polling query: assigned to me, under the epics, not done, extra clause; rendered fields; paging | 40 ms | — | — |
| ✅ passed | — | comments newest-first with rendered bodies; attachments via a 303 to the media host | 22 ms | — | — |
| ✅ passed | — | write-back: status read, transitions list, POST transition 204, invalid id 400 | 58 ms | — | — |
| ✅ passed | — | failure injection and an outage | 10 ms | — | — |
| ✅ passed | — | auth: connectionData for the PAT; a wrong PAT gets 203 + HTML like the real service | 5 ms | — | — |
| ✅ passed | — | projects, work item types with state categories, type categories | 19 ms | — | — |
| ✅ passed | — | two-step polling WIQL: hierarchy (MustContain / Recursive), then the flat filter; batch with relations | 63 ms | — | — |
| ✅ passed | — | comments (desc, deleted skipped by the runner), attachments, System.State write-back | 35 ms | — | — |
| ✅ passed | `TCLI-007` `TCLI-008` `TCLI-011` | tracker test/list: the runner signs in to the fake Jira and ADO with the seeded tokens; tokens never printed | 1.6 s | — | — |
| ✅ passed | `TCLI-008` | a revoked token: tracker test reports failing with Jira's advice and exits 1 | 698 ms | — | — |
| ✅ passed | — | tracker add cannot target a loopback site: --site http://127.0.0.1:<port> is rewritten to https://127.0.0.1 (no port) | 2.3 s | — | — |
| ✅ passed | `TCLI-009` | tracker remove deletes the entry and its token | 806 ms | — | — |
| ✅ passed | — | auth status reports the signed-in accounts the way ForgeCli.SignedInAccounts parses them | 311 ms | — | — |
| ✅ passed | — | publish: pr list finds nothing → pr create prints the URL → a retry finds the open PR (idempotent) | 388 ms | — | — |
| ✅ passed | — | state: pr view --json state --jq .state follows OPEN → MERGED (ForgeCli.NormalizeState → merged) | 401 ms | — | — |
| ✅ passed | — | account scoping: auth token --user prints that account's token; GH_TOKEN reaches the call record | 298 ms | — | — |
| ✅ passed | — | a configured failure and a signed-out CLI fail like gh does | 193 ms | — | — |
| ⏭️ skipped | `NEG-030` | NEG-030 a project whose slug is exactly 26 characters must still resolve by slug (ULID-length regression) | 0 ms | — | — |
| ⏭️ skipped | — | an unknown project id answers 404, not 403 | 0 ms | — | — |
| ⏭️ skipped | — | the harness API key acts as a Member: admin/me says not admin and admin actions are 403 | 0 ms | — | — |
| ⏭️ skipped | — | API keys cannot mint API keys | 0 ms | — | — |
| ⏭️ skipped | — | health endpoint answers anonymously | 0 ms | — | — |
| ⏭️ skipped | — | API: org/me answers for the harness key and matches the expected workspace @smoke | 0 ms | — | — |
| ⏭️ skipped | `WSP-001` | API: workspaces list answers with exactly one active workspace @smoke | 0 ms | — | — |
| ⏭️ skipped | — | API: runner presence answers, with forge CLI status when online @smoke | 0 ms | — | — |
| ⏭️ skipped | `TSRC-009` | API: tracker connections list answers (possibly empty) @smoke | 0 ms | — | — |
| ⏭️ skipped | — | UI: dashboard loads for the saved session and shows the shell @smoke | 0 ms | — | — |
| ⏭️ skipped | `SET-012` | UI: project settings page renders for the first project (or onboarding when none) @smoke | 0 ms | — | — |
| ⏭️ skipped | `TING-001` `TING-006` `TSRC-004` `TSRC-010` `TWB-001` `TWB-002` `TWB-003` `FRG-001` | TING-001 a Jira ticket becomes a task, is planned, built, published via gh, and its status follows | 0 ms | — | — |
| ⏭️ skipped | — | seed: create the inventory project with a repo and a draft task | 0 ms | — | — |
| ⏭️ skipped | — | / renders (MKT-001) | 0 ms | — | — |
| ⏭️ skipped | — | /pricing renders (MKT-005) | 0 ms | — | — |
| ⏭️ skipped | — | /docs renders (MKT-006) | 0 ms | — | — |
| ⏭️ skipped | — | /blog renders (MKT-009) | 0 ms | — | — |
| ⏭️ skipped | — | /terms renders (MKT-008) | 0 ms | — | — |
| ⏭️ skipped | — | /privacy renders (MKT-008) | 0 ms | — | — |
| ⏭️ skipped | — | /dpa renders (MKT-008) | 0 ms | — | — |
| ⏭️ skipped | — | /dashboard renders (ONB-001) | 0 ms | — | — |
| ⏭️ skipped | — | /inbox renders (NTF-001) | 0 ms | — | — |
| ⏭️ skipped | — | /notifications renders (NTF-003) | 0 ms | — | — |
| ⏭️ skipped | — | /onboarding renders (ONB-002) | 0 ms | — | — |
| ⏭️ skipped | — | /settings renders (SET-001) | 0 ms | — | — |
| ⏭️ skipped | — | /settings/preferences renders (SET-003) | 0 ms | — | — |
| ⏭️ skipped | — | /settings/billing renders (BIL-002) | 0 ms | — | — |
| ⏭️ skipped | — | /settings/billing/credits renders (BIL-004) | 0 ms | — | — |
| ⏭️ skipped | — | /settings/billing/budget renders (BIL-012) | 0 ms | — | — |
| ⏭️ skipped | — | /settings/billing/invoices renders (BIL-014) | 0 ms | — | — |
| ⏭️ skipped | — | /settings/billing/payment renders (BIL-016) | 0 ms | — | — |
| ⏭️ skipped | — | /settings/notifications renders (SET-005) | 0 ms | — | — |
| ⏭️ skipped | — | /settings/runner renders (RUN-002) | 0 ms | — | — |
| ⏭️ skipped | — | /settings/organization renders (SET-006) | 0 ms | — | — |
| ⏭️ skipped | — | /settings/api-keys renders (KEY-001) | 0 ms | — | — |
| ⏭️ skipped | — | /settings/integrations renders (INT-001) | 0 ms | — | — |
| ⏭️ skipped | — | /settings/audit renders (AUD-001) | 0 ms | — | — |
| ⏭️ skipped | — | /p/{slug} renders (NAV-003) | 0 ms | — | — |
| ⏭️ skipped | — | /p/{slug}/board renders (BRD-001) | 0 ms | — | — |
| ⏭️ skipped | — | /p/{slug}/board?view=code renders (BRD-004) | 0 ms | — | — |
| ⏭️ skipped | — | /p/{slug}/tasks renders (TSK-050) | 0 ms | — | — |
| ⏭️ skipped | — | /p/{slug}/tasks/new renders (TSK-001) | 0 ms | — | — |
| ⏭️ skipped | — | /p/{slug}/tasks/{n} renders (TSK-011) | 0 ms | — | — |
| ⏭️ skipped | — | /p/{slug}/tasks/{n}/review renders (PR-001) | 0 ms | — | — |
| ⏭️ skipped | — | /p/{slug}/team renders (TEAM-001) | 0 ms | — | — |
| ⏭️ skipped | — | /p/{slug}/activity renders (ACT-001) | 0 ms | — | — |
| ⏭️ skipped | — | /p/{slug}/skills renders (SKL-001) | 0 ms | — | — |
| ⏭️ skipped | — | /p/{slug}/automation renders (AUTO-001) | 0 ms | — | — |
| ⏭️ skipped | — | /p/{slug}/memory renders (MEM-001) | 0 ms | — | — |
| ⏭️ skipped | — | /p/{slug}/roadmap renders (RDM-001) | 0 ms | — | — |
| ⏭️ skipped | — | /p/{slug}/repos renders (REPO-001) | 0 ms | — | — |
| ⏭️ skipped | — | /p/{slug}/documents renders (DOC-001) | 0 ms | — | — |
| ⏭️ skipped | — | /p/{slug}/design renders (DSN-001) | 0 ms | — | — |
| ⏭️ skipped | — | /p/{slug}/architect renders (ARC-001) | 0 ms | — | — |
| ⏭️ skipped | — | /p/{slug}/members renders (MBR-001) | 0 ms | — | — |
| ⏭️ skipped | — | /p/{slug}/settings renders (SET-010) | 0 ms | — | — |
| ⏭️ skipped | — | /es/settings renders localized (I18N-001, I18N-004) | 0 ms | — | — |
| ⏭️ skipped | — | /pt/settings renders localized (I18N-001, I18N-004) | 0 ms | — | — |
| ⏭️ skipped | — | /fr/settings renders localized (I18N-001, I18N-004) | 0 ms | — | — |
| ⏭️ skipped | — | /de/settings renders localized (I18N-001, I18N-004) | 0 ms | — | — |
| ⏭️ skipped | — | teardown: delete the inventory project | 0 ms | — | — |

## Skipped

- TSRC-001 a project with no ticket source answers 204 — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- TSRC-002 PUT with a connection that is not mine → 400 on trackerConnectionId — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- TSRC-003 validation: poll interval, branch template, epics, subfolder, scope — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- TSRC-005 DELETE without a ticket source is a no-op 204; GET stays 204 — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- TSRC-007 tracker query with an unknown kind → 400 — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- TSRC-008 tracker query on an unknown connection → 404 (not 403) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- TSRC-009 GET /api/trackers/connections lists my connections with metadata only — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- TSRC-006 a query while my runner is offline answers supported:false, not an error — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- WSP-001 GET /api/me/workspaces lists my workspaces with exactly one active — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- WSP-003 switching the active workspace answers 204; an unknown org answers 404 — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- WSP-004 a non-ULID X-Orkka-Workspace answers 400 Invalid workspace. — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- WSP-005 naming a workspace I am not in answers 403 — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- WSP-006 the same call in two workspaces returns disjoint projects — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- WSP-007 an API key ignores X-Orkka-Workspace and stays in its own org — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- WSP-002 creating a workspace makes it mine and active (restores the previous active one) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- HYF-001 browse the runner's file system through the relay — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- HYF-004 link a git folder from this machine: provider local, execution on the runner — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- HYF-005 link a plain (non-git) folder: Orkka initialises the repository in place — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- HYF-006 linking a folder that does not exist answers 4xx with a reason — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- HYF-010 sync-status of a freshly linked folder: no remote, nothing to push — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- HYF-013 connect the folder to an EMPTY remote the developer created and push — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- HYF-014 connecting to a remote that already has commits is refused with a reason — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- HYF-024 a task on a linked folder lands in the working copy on this machine — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- HYB-046 hands-free task → Done → PR published via gh on the developer's account — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- HYB-024 human loop: plan waits in PlanReady, approval resumes, request changes, approve publishes — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- FRG-003 gh signed out: publishing fails with the gh auth login remedy and nothing is created — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- HYB-002 unknown execution preference target is rejected with 400 — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- HYB-008 create a project and add a remote by URL: GitHub detected from the host — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- RURL-002 ssh, scp-style and host-alias remotes are accepted; an alias reads as Other — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- RURL-003 a folder path in the URL field is refused (link folders through the runner) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- RURL-004 provider override and CLI account are stored — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- RURL-005 credentials embedded in an https URL are refused — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- HYB-009 access preflight: the sandboxed runner reads the remote with its own git — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- HYB-010 access preflight names an unreadable remote with a reason — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- HYB-004 execution-target preview: a task runs on the developer's runner — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- REPO-008 the file viewer reads the remote through the runner relay — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- RUN-001 Runner settings page shows the runner online with its machine and providers — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- BRD-001 board renders the nine lanes and the new-task entry point for a fresh project — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- create a task from the form as a draft and see it on the board and the detail page — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- task detail shows the runner execution badge for a UserRunner task — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- SET-010 project settings: pipeline toggles persist through the UI and the API sees them — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- NEG-030 a project whose slug is exactly 26 characters must still resolve by slug (ULID-length regression) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- an unknown project id answers 404, not 403 — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- the harness API key acts as a Member: admin/me says not admin and admin actions are 403 — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- API keys cannot mint API keys — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- health endpoint answers anonymously — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- API: org/me answers for the harness key and matches the expected workspace @smoke — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- API: workspaces list answers with exactly one active workspace @smoke — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- API: runner presence answers, with forge CLI status when online @smoke — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- API: tracker connections list answers (possibly empty) @smoke — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- UI: dashboard loads for the saved session and shows the shell @smoke — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- UI: project settings page renders for the first project (or onboarding when none) @smoke — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- TING-001 a Jira ticket becomes a task, is planned, built, published via gh, and its status follows — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- seed: create the inventory project with a repo and a draft task — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- / renders (MKT-001) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /pricing renders (MKT-005) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /docs renders (MKT-006) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /blog renders (MKT-009) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /terms renders (MKT-008) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /privacy renders (MKT-008) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /dpa renders (MKT-008) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /dashboard renders (ONB-001) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /inbox renders (NTF-001) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /notifications renders (NTF-003) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /onboarding renders (ONB-002) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /settings renders (SET-001) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /settings/preferences renders (SET-003) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /settings/billing renders (BIL-002) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /settings/billing/credits renders (BIL-004) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /settings/billing/budget renders (BIL-012) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /settings/billing/invoices renders (BIL-014) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /settings/billing/payment renders (BIL-016) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /settings/notifications renders (SET-005) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /settings/runner renders (RUN-002) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /settings/organization renders (SET-006) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /settings/api-keys renders (KEY-001) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /settings/integrations renders (INT-001) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /settings/audit renders (AUD-001) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /p/{slug} renders (NAV-003) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /p/{slug}/board renders (BRD-001) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /p/{slug}/board?view=code renders (BRD-004) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /p/{slug}/tasks renders (TSK-050) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /p/{slug}/tasks/new renders (TSK-001) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /p/{slug}/tasks/{n} renders (TSK-011) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /p/{slug}/tasks/{n}/review renders (PR-001) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /p/{slug}/team renders (TEAM-001) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /p/{slug}/activity renders (ACT-001) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /p/{slug}/skills renders (SKL-001) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /p/{slug}/automation renders (AUTO-001) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /p/{slug}/memory renders (MEM-001) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /p/{slug}/roadmap renders (RDM-001) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /p/{slug}/repos renders (REPO-001) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /p/{slug}/documents renders (DOC-001) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /p/{slug}/design renders (DSN-001) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /p/{slug}/architect renders (ARC-001) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /p/{slug}/members renders (MBR-001) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /p/{slug}/settings renders (SET-010) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /es/settings renders localized (I18N-001, I18N-004) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /pt/settings renders localized (I18N-001, I18N-004) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /fr/settings renders localized (I18N-001, I18N-004) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- /de/settings renders localized (I18N-001, I18N-004) — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)
- teardown: delete the inventory project — no deployed Orkka configured (set E2E_BASE_URL / E2E_API_URL in .env)

## Cleanup

- Projects created this run: 0
- Leftovers after teardown: none
