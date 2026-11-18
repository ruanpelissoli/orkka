# Run 20261117-2321-workflow-live

| | |
|---|---|
| Suite | `workflow` (live, step by step — the same API calls as `tests/local/workflow.spec.ts`, driven by hand so each bug could be fixed and the step re-run) |
| Target | sandboxed local stack `http://127.0.0.1:6750` (`ORKKA_HOME=<scratchpad>/e2e-full/home`, `ORKKA_SECRET_STORE=file`) |
| Started | 2026-11-17 23:21 local (02:21Z) |
| Finished | 2026-11-18 00:55 local (03:55Z) |
| Result | every step passed after 11 product fixes (orkka-api ×10, orkka-ui ×1) and 6 harness fixes |
| Real agent runs | **3** pipeline runs on the developer's Claude subscription, all agents on `claude-haiku-4-5`: SHOP-11 (standalone), SHOP-12 (question-routed), Story SHOP-20 (2 sub-tasks). Plus one Story start swept after ~1 min (before any agent ran) and small utility calls (repo router, PM triage, one Discuss turn). Codex unused. |
| Operator | claude · orkka-api pivot/trackers · orkka-ui pivot/trackers · node v24.18.0 · playwright 1.63.0 |

## Steps

| # | Step | Result | Evidence |
|---|---|---|---|
| 0 | `orkka-local start --detach --no-browser` in the sandbox; API key from the install secret; launch-code session | ✅ after fix 1 | `start` returns only once the UI answers (host log: `/favicon.ico` 502 → 200 before "running") |
| 1 | Base folder `shop` (api = alias `https://github.com/e2e-local/shop-api-*.git` → local bare remote; web = no origin) → `POST /api/projects/from-folder` | ✅ after fixes 2, 3 | 2 repos: api `remote`/github, web `local`; both `ready` |
| 2 | `POST /api/trackers/connections` (fake Jira) | ✅ after fix 4 | token only in `home/workspaces/personal/secrets/jira-127-0-0-1`; not in `pg_dump`, logs, `trackers.json`, nor Windows Credential Manager |
| 3 | Ticket source: intake `["To Do"]`; planning → In Progress, inReview unmapped, prOpened → Code Review, merged → Done | ✅ | `PUT …/tracker` echoes the map; the live `statuses` query proposes the same defaults |
| 4 | SHOP-11 standalone: router → api; PM asked nothing; one Discuss question (SSE reply in 7 s); plan approved; dev; Orkka review approved; publish | ✅ after fixes 5, 6, 7 | stub gh `pr create` → `https://github.com/e2e-local/shop-api-mwlijbia/pull/1`; the Jira comment "PO here…" reached the thread as `ExternalComment` |
| 4b | Jira transitions | ✅ after fix 7 | SHOP-11: To Do → In Progress → **Done** (the bug: the PR had only opened); after the fix SHOP-12: In Progress → **Code Review** |
| 5 | SHOP-12 names no repo → repository question (api/web) → answered with Discuss "Apply" (`action:"answered"`) → api; moved to In Progress in the fake Jira right after intake | ✅ | not retired; no spurious "was updated" note |
| 6 | Story SHOP-20 with SHOP-21, SHOP-22 (hands-free approvals) | ✅ after fixes 8, 9 | one group, order 1/2, one branch `feature/SHOP-20-math-helpers-for-the-api-service`; stub gh history `created-draft, edited, ready, edited`; branch log has SHOP-21's commits under SHOP-22's; SHOP-22's Code changes = `neg()` only; Jira SHOP-21/20/22 → Code Review together; story PR merged on the stub → all three → Done |
| 7 | Azure DevOps (short): PAT connection, intake `New`, Picked up → Active, a comment | ✅ after harness fix (fake ADO WIQL) | PAT only in `secrets/ado-e2eorg`, not in Postgres; work item 501 → Draft task routed to api; New → Active; comment in the thread. No agent run (auto-submit off) |
| 8 | Screens (`npm run test:screens`) | ✅ after fix 10 | 10 PNGs under `<scratchpad>/e2e-full/screens/` |

## Bugs found and fixed

| # | Repo | Commit | Bug | Regression test |
|---|---|---|---|---|
| 1 | orkka-api | `327e3a2` | `orkka-local start` took the host's own signed-out 401 on `/` as "UI ready": it returned before Next listened and the launch URL landed on a 502 | `LocalStackRuntimeTests.The_front_door_probe_is_not_fooled_by_the_hosts_own_signed_out_401` |
| 2 | orkka-api | `15f5ee0` | BundledGit overwrote the inherited `GIT_CONFIG_*` pairs (a developer's insteadOf / proxy) with its own `core.longpaths`: mirrors cloned github.com instead of the rewritten remote | `BundledGitConfigTests` |
| 3 | orkka-api | `d012406` | Base-folder discovery used `git remote get-url` (insteadOf-expanded): a GitHub origin rewritten to a path registered as a local repo | `BaseFolderDiscoveryTests.Origin_IsTheConfiguredUrl_NotItsInsteadOfRewrite` |
| 4 | orkka-api | `a4c76d5` | The runner scrubs `*SECRET*` variables at startup, `ORKKA_SECRET_STORE` included: tokens added from the UI went to the Windows Credential Manager despite `ORKKA_SECRET_STORE=file` (the fake token was removed from Credential Manager by hand) | `TrackerTestOptInTests.TheForcedFileStore_SurvivesTheRunnersEnvironmentScrub` |
| 5 | orkka-api | `cf0218a` | The ticket content hash included the status: Orkka's own write-back posted "SHOP-11 was updated in Jira. The ticket now reads…" on started work | `TicketNormalizerTests.Hash_IgnoresCommentsUpdatedAtAndLabelOrder` |
| 6 | orkka-api | `770abb7` | Publishing read the mirror origin with `remote get-url`: "This repository has no remote to open a pull request on" | `RunnerOriginUrlTests` |
| 7 | orkka-api | `4cbb34a` | The Merge/Publish click never stored `PublishedPrUrl`; write-back read "merged, no forge PR" as a folder landing and moved the ticket to Done when the PR had only opened (other paths had a two-save race) | `PublishRecordsForgePrTests` |
| 8 | orkka-api | `613d1c9` | Story members kept the dependency-analysis gate from submission: every member, the first included, waited the full 5-minute window | `StoryMemberSubmissionGateTests` |
| 9 | orkka-api | `8df8c45` | A finished Story member's Code changes diff was the whole shared branch vs main (SHOP-22 showed SHOP-21's `div()`) | `SnapshotDiffStoryBaseTests` |
| 10 | orkka-ui | `b391de3` | `Toggle` had no `shrink-0`: beside a long hint the switch was squeezed and out of line | `__tests__/components/toggle.test.tsx` |
| 11 | orkka-api | `3f0e16e` | (`test:api` TSRC-003) an empty ticket-source scope answered "'scope' must not be empty." instead of the friendly message | `TrackerConfigTests.Validator_AnEmptyScope_SaysWhatToPick` |

## Harness fixes (this repo)

- Fake Jira: `key in (…)` with Jira's whole-query failure on a missing key, `subtasks`, `assignee.accountId`, `components`, issue-type `hierarchyLevel`/`subtask`, the parent's issue type; admin `PATCH /__fake/jira/issues/{key}`, `POST …/comments`, `GET /__fake/transitions`.
- Fake ADO: `[System.WorkItemType] NOT IN (…)` (intake v2); admin item patch / comment.
- Stub gh: `pr create --draft`, `pr ready [--undo]`, `pr edit --body-file`, `isDraft` / `history`; its state survives a stack restart.
- API fixture: `"test": "node --test"` (`node --test test/` fails on Node 24 — the agents had patched it in their PRs).
- The pre-run sweep deleted the live project mid-Story (> 30 min old): `E2E_KEEP_PROJECTS=1` now skips the sweep as well as the teardown.

## Observations (not fixed)

- After a stack restart the ticket source waits for the old runner instance's 5-minute poll lease — intake pauses up to 5 minutes per restart.
- A Story member's live (in-run) working-tree diff still compares against the default branch; only the finished diff was fixed (needs `BaseSha` on `GetWorkingTreeDiffRequest` through runner, relay and endpoint).
- PR bodies mix CRLF and LF on Windows (renders fine).
- NEG-030 (26-character slug) still fails as expected.

## Cleanup

- Projects `e2e-shop` (x2, the first was swept mid-run) and `e2e-shop-ado`; deleted by the later suites’ teardown.
- `orkka-local stop`; no listener left on 6750/6752/6753.

## Follow-up (2026-11-18)
- Intake pause after a restart: fixed in orkka-api `6d1440b9`. The owner's active runner connection now takes over a poll lease held by the instance it replaced.
- A Story sub-task's live diff: fixed in orkka-api `cc570938`. The working-tree diff takes the member's review base (`GetWorkingTreeDiffRequest.BaseSha`).
