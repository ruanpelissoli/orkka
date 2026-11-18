# Run 20261118-0102-ui-inventory

| | |
|---|---|
| Suite | `ui-inventory` |
| Target | http://127.0.0.1:6750 |
| Started | 2026-11-18T04:02:57.663Z |
| Duration | 50.8 s |
| Result | 37 passed · 0 failed · 0 timed out · 0 skipped |
| Operator | claude · harness 7c790e44baab · node v24.18.0 · playwright 1.63.0 |

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ✅ passed | — | seed: create the inventory project with a repo and a draft task | 628 ms | — | e2e-inventory-20261118-0102-89un |
| ✅ passed | `ONB-001` | /en/dashboard renders (ONB-001) | 2.0 s | — | — |
| ✅ passed | `NTF-001` | /en/inbox renders (NTF-001) | 1.2 s | — | — |
| ✅ passed | `NTF-003` | /en/notifications renders (NTF-003) | 1.2 s | — | — |
| ✅ passed | — | /en/projects renders () | 1.8 s | — | — |
| ✅ passed | `LWF-002` | /en/projects/new renders (LWF-002) | 1.3 s | — | — |
| ✅ passed | `SET-001` | /en/settings renders (SET-001) | 1.8 s | — | — |
| ✅ passed | `LWF-013` | /en/settings/workspace renders (LWF-013) | 1.3 s | — | — |
| ✅ passed | `LWF-009` | /en/settings/machine renders (LWF-009) | 1.4 s | — | — |
| ✅ passed | `LWF-009` | /en/settings/trackers renders (LWF-009) | 1.3 s | — | — |
| ✅ passed | `LWF-012` | /en/settings/usage renders (LWF-012) | 1.3 s | — | — |
| ✅ passed | `KEY-001` | /en/settings/api-keys renders (KEY-001) | 1.2 s | — | — |
| ✅ passed | `SET-003` | /en/settings/preferences renders (SET-003) | 1.4 s | — | — |
| ✅ passed | `SET-005` | /en/settings/notifications renders (SET-005) | 1.2 s | — | — |
| ✅ passed | — | /en/p/{slug} renders () | 1.3 s | — | — |
| ✅ passed | `BRD-001` | /en/p/{slug}/board renders (BRD-001) | 1.7 s | — | — |
| ✅ passed | `BRD-004` | /en/p/{slug}/board?view=code renders (BRD-004) | 1.6 s | — | — |
| ✅ passed | `TSK-050` | /en/p/{slug}/tasks renders (TSK-050) | 1.4 s | — | — |
| ✅ passed | `TSK-001` | /en/p/{slug}/tasks/new renders (TSK-001) | 1.6 s | — | — |
| ✅ passed | `TSK-011` | /en/p/{slug}/tasks/{n} renders (TSK-011) | 1.6 s | — | — |
| ✅ passed | `PR-001` | /en/p/{slug}/tasks/{n}/review renders (PR-001) | 1.3 s | — | — |
| ✅ passed | `TEAM-001` | /en/p/{slug}/team renders (TEAM-001) | 1.4 s | — | — |
| ✅ passed | `ACT-001` | /en/p/{slug}/activity renders (ACT-001) | 1.4 s | — | — |
| ✅ passed | `SKL-001` | /en/p/{slug}/skills renders (SKL-001) | 1.3 s | — | — |
| ✅ passed | `AUTO-001` | /en/p/{slug}/automation renders (AUTO-001) | 1.4 s | — | — |
| ✅ passed | `MEM-001` | /en/p/{slug}/memory renders (MEM-001) | 1.4 s | — | — |
| ✅ passed | `RDM-001` | /en/p/{slug}/roadmap renders (RDM-001) | 1.4 s | — | — |
| ✅ passed | `REPO-001` | /en/p/{slug}/repos renders (REPO-001) | 1.8 s | — | — |
| ✅ passed | `DOC-001` | /en/p/{slug}/documents renders (DOC-001) | 1.4 s | — | — |
| ✅ passed | `DSN-001` | /en/p/{slug}/design renders (DSN-001) | 1.5 s | — | — |
| ✅ passed | `ARC-001` | /en/p/{slug}/architect renders (ARC-001) | 1.3 s | — | — |
| ✅ passed | `SET-010` | /en/p/{slug}/settings renders (SET-010) | 1.4 s | — | — |
| ✅ passed | `I18N-001` `I18N-004` | /es/settings renders localized (I18N-001, I18N-004) | 842 ms | — | — |
| ✅ passed | `I18N-001` `I18N-004` | /pt/settings renders localized (I18N-001, I18N-004) | 885 ms | — | — |
| ✅ passed | `I18N-001` `I18N-004` | /fr/settings renders localized (I18N-001, I18N-004) | 995 ms | — | — |
| ✅ passed | `I18N-001` `I18N-004` | /de/settings renders localized (I18N-001, I18N-004) | 831 ms | — | — |
| ✅ passed | — | teardown: delete the inventory project | 49 ms | — | — |

## Cleanup

- Projects created this run: 1
  - `e2e-inventory-20261118-0102-89un` (01M7PD4YEETWMFC05J8SH5T5WA)
- Leftovers after teardown: none
