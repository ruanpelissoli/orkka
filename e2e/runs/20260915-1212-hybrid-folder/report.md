# Run 20260915-1212-hybrid-folder

| | |
|---|---|
| Suite | `hybrid-folder` |
| Target | prod |
| Started | 2026-09-15T15:12:37.791Z |
| Duration | 13.3 s |
| Result | 0 passed · 0 failed · 0 timed out · 9 skipped |
| Spend | $0.00 of $5.00 cap (credits 2814 → 2814) |
| Operator | claude · harness uncommitted · node v24.18.0 · playwright 1.63.0 |

## Notes
- Harness API key acts as a Member with all 18 permissions (by design); Owner-only endpoints (org rename/settings, role changes, admin) use the Clerk session.
- Org plan has no team features (not Enterprise); Members cases will be skipped.

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ⏭️ skipped | `HYF-001` | HYF-001 browse the runner's file system through the relay | 1.4 s | $0.000 | — |
| ⏭️ skipped | `HYF-002` `HYF-003` `HYF-010` | HYF-002 link a git folder from this machine: provider local, execution pinned to the runner | 610 ms | $0.000 | — |
| ⏭️ skipped | `HYF-004` | HYF-004 link a plain (non-git) folder: Orkka initialises the repository in place | 628 ms | $0.000 | — |
| ⏭️ skipped | `HYF-005` | HYF-005 linking a folder that does not exist answers 4xx with a reason | 631 ms | $0.000 | — |
| ⏭️ skipped | `HYF-006` | HYF-006 sync-status of a freshly linked folder: no remote, nothing to push | 634 ms | $0.000 | — |
| ⏭️ skipped | `HYF-007` `HYF-008` | HYF-007 'Let Orkka host it' publishes the folder to a private repo under orkka-repos | 615 ms | $0.000 | — |
| ⏭️ skipped | `HYF-009` `HYF-011` | HYF-009 connect an EMPTY GitHub repo the user created and push; a second push says up to date | 613 ms | $0.000 | — |
| ⏭️ skipped | `HYF-012` | HYF-012 attach-remote to a NON-empty repo is refused with a clear reason | 618 ms | $0.000 | — |
| ⏭️ skipped | `HYF-020` `HYF-021` `HYF-022` | HYF-020 task on a linked folder lands in the working copy on this machine ($0) | 624 ms | $0.000 | — |

## Skipped

- HYF-001 browse the runner's file system through the relay — the online runner is LAPTOP-B, but the fixture folders live on LAPTOP-A — run this suite where the runner runs
- HYF-002 link a git folder from this machine: provider local, execution pinned to the runner — the online runner is LAPTOP-B, but the fixture folders live on LAPTOP-A — run this suite where the runner runs
- HYF-004 link a plain (non-git) folder: Orkka initialises the repository in place — the online runner is LAPTOP-B, but the fixture folders live on LAPTOP-A — run this suite where the runner runs
- HYF-005 linking a folder that does not exist answers 4xx with a reason — the online runner is LAPTOP-B, but the fixture folders live on LAPTOP-A — run this suite where the runner runs
- HYF-006 sync-status of a freshly linked folder: no remote, nothing to push — the online runner is LAPTOP-B, but the fixture folders live on LAPTOP-A — run this suite where the runner runs
- HYF-007 'Let Orkka host it' publishes the folder to a private repo under orkka-repos — the online runner is LAPTOP-B, but the fixture folders live on LAPTOP-A — run this suite where the runner runs
- HYF-009 connect an EMPTY GitHub repo the user created and push; a second push says up to date — the online runner is LAPTOP-B, but the fixture folders live on LAPTOP-A — run this suite where the runner runs
- HYF-012 attach-remote to a NON-empty repo is refused with a clear reason — the online runner is LAPTOP-B, but the fixture folders live on LAPTOP-A — run this suite where the runner runs
- HYF-020 task on a linked folder lands in the working copy on this machine ($0) — the online runner is LAPTOP-B, but the fixture folders live on LAPTOP-A — run this suite where the runner runs

## Cleanup

- Projects created this run: 0
- Leftovers after teardown: none
