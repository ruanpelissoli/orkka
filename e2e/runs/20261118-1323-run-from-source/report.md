# Run 20261118-1323-run-from-source

| | |
|---|---|
| Phase | R5 (final): e2e harness retargeted at the one-process `./orkka`, every suite re-run, one real Story on Haiku, loose ends a–f |
| Target | sandboxed Orkka `http://127.0.0.1:6792` — `dotnet src/Orkka/bin/Release/net10.0/orkka.dll start --no-browser --port 6792`, `ORKKA_HOME=<scratchpad>/e2e-rfs/home`, `ORKKA_SECRET_STORE=file` |
| Branch | `r5-e2e` from `9047232b` · 2026-11-18 13:23 → 14:40 local |
| Result | all suites green on the final build; 5 product bugs fixed, 4 harness bugs fixed |
| Real agent runs | Story SHOP-20 (2 sub-tasks) to completion twice + one retry stopped by product bug 3 before any pipeline agent; all `claude-haiku-4-5`; ≈ $1.85 list-price equivalent |

## Stack
`stack:start` spawns `orkka.dll start --no-browser --port <port>` detached (console → `<sandbox>/orkka-start.out`), waits for `/health` `service: orkka`, mints an `orkka_key_` key (install secret) and a session from `open --print`; `stack:stop` = `orkka.dll stop` + kill tree. Postgres, copied builds, `--detach`, `E2E_HOST_BUILD`/`E2E_UI_STANDALONE`/`E2E_RUNNER_EXE`/`E2E_LOCAL_RUNTIME_SEED` gone; secret scan reads `orkka.db` + WAL; CLI specs run `dotnet orkka.dll`. Healthy in ~3 s.

## Suites
| Suite | Result | Report |
|---|---|---|
| offline | 25/25 | 20261118-1414-offline |
| smoke | 14/14 (NEG-030 expected-fail) | 20261118-1433-smoke |
| api | 14/15, TSRC-006 skipped (needs runner offline) | 20261118-1434-api |
| ui-inventory | 37/37 | 20261118-1433-ui-inventory |
| workflow, no agents | 4/4 (+3 agent steps skipped) | 20261118-1324-workflow |
| workflow, Story (real agents) | 5/5 | 20261118-1414-workflow |
| screens | 8/8 (new: board in Portuguese) | 20261118-1432-screens |
| sync-check | OK — 708 cases, 226 routes, 39 pages, 101 namespaces, 12 verbs, 20 hub events | — |

## The Story
| Run | Outcome |
|---|---|
| 1 (1329) | Both members Done; one draft PR (`created-draft → edited → ready → edited`); PM asked about `div(a,0)` (answered); stopped mid-Developing on purpose (loose end a), re-queued on restart. Spec failed on a harness assertion (`div` as diff context); Jira moves unchecked. |
| retry (1343) | Tickets reached intake, no task for 15 min — product bug 3 (hub deadlock): dumped, reproduced twice intake-only (~$0.01 each), fixed. |
| final (1414) | Green: intake 34 s on a fresh start; both members Done in 5.6 min; draft → ready; SHOP-22's review adds only `neg`; SHOP-20/21/22 → Code Review; ADO step green. |

## Bugs fixed
| # | Commit | Bug | Test |
|---|---|---|---|
| 1 | 7178c4f2 | `./orkka stop`/Ctrl+C left claude.exe + its MCP servers orphaned (no run cancellation on stop, fire-and-forget teardown, SDK plain `Kill()`). Session drain + `AgentProcesses` kill-tree + SDK patch 5 | AgentProcessesTests, ClaudeAgentSdkCloseKillsTreeTests, RunnerRunRegistryTests.CancelAllAsync_* |
| 2 | a196760c | Deleted project's ticket source kept polling (no Trackers `ProjectDeleted` handler) | ProjectTrackerEndpointTests.DeletingTheProject_RemovesItsTicketSource_SoNothingPollsIt |
| 3 | f6d0a040 | Runner hub default 1 invocation/client: `RotateToken` during `ReportTickets`→routing parked the read loop; utility completion unread for 900 s. Now 8; Hello serialized | RunnerHubClientResultDeadlockTests |
| 4 | 45deb798 | Breadcrumb untranslated; task keys mangled | breadcrumbs.test.ts + screen 03c |
| 5 | d978eac5 | Board lane headers English on every locale | board-lane-label.test.ts + screen 03c |

Also 2170591a (favicon), 75a58ad9 (launcher scripts).

## Loose ends
- a. Leaked → fixed → re-verified: stop 2 s, Ctrl+C 1 s, "Stopped 1 run(s) in flight", 0/7 survivors; parked task re-queued after restart. (Ctrl+C sent into Orkka's own console; Orkka launched via `Win32_Process.Create` because tool-launched processes ignore Ctrl+C.)
- b. Transitional branch removed from `orkka`/`orkka.cmd`; `ORKKA_SKIP_UI_BUILD` kept (documented).
- c. `orkka-favicon.svg` (distinct 32 px icon).
- d. Fixed with existing keys; lane headers fixed in the same pass.
- e. PATH (user+machine), HKCU/HKLM Run/RunOnce, services, scheduled tasks, Credential Manager `orkka` entries — before / running / after a fresh `orkka.cmd start` + `stop`: 542 lines each, identical; only listener 127.0.0.1:6791.
- f. Remaining `orkka-local` hits deliberate (HMAC label, LegacyInstall marker + its docs, copy-guard test, inventories under an R4 banner); stack-term hits are customer stacks/env scrub/examples/history; 2 stale fixed.

## Not verified
Linux/macOS; NEG-030 still open; doubled console warnings and abandoned-request 500 logs (cosmetic).

## Final state
Build 0 errors · unit 3252/3252 · integration 429/429 · ui tsc/eslint clean, vitest 888/888 · e2e tsc clean · sandbox stopped, no listeners or leftover processes.
