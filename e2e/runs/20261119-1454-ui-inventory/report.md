# Run 20261119-1454-ui-inventory

| | |
|---|---|
| Suite | `ui-inventory` |
| Target | http://127.0.0.1:6760 |
| Started | 2026-11-19T17:54:16.555Z |
| Duration | 1m 4s |
| Result | 40 passed · 0 failed · 0 timed out · 0 skipped |
| Operator | claude · harness uncommitted · node v24.18.0 · playwright 1.63.0 |

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ✅ passed | — | seed: create the inventory project with a repo and a draft task | 732 ms | — | e2e-inventory-20261119-1454-2sjw |
| ✅ passed | `ONB-001` | /en/dashboard renders (ONB-001) | 1.5 s | — | — |
| ✅ passed | — | /en/board renders () | 1.5 s | — | — |
| ✅ passed | `NTF-001` | /en/inbox renders (NTF-001) | 1.3 s | — | — |
| ✅ passed | `NTF-003` | /en/notifications renders (NTF-003) | 1.3 s | — | — |
| ✅ passed | — | /en/projects renders () | 1.4 s | — | — |
| ✅ passed | `LWF-002` | /en/projects/new renders (LWF-002) | 1.1 s | — | — |
| ✅ passed | `SET-001` | /en/settings renders (SET-001) | 1.5 s | — | — |
| ✅ passed | `LWF-013` | /en/settings/workspace renders (LWF-013) | 1.4 s | — | — |
| ✅ passed | `LWF-009` | /en/settings/machine renders (LWF-009) | 1.6 s | — | — |
| ✅ passed | `LWF-009` | /en/settings/trackers renders (LWF-009) | 1.6 s | — | — |
| ✅ passed | `LWF-012` | /en/settings/usage renders (LWF-012) | 1.5 s | — | — |
| ✅ passed | `KEY-001` | /en/settings/api-keys renders (KEY-001) | 1.5 s | — | — |
| ✅ passed | `SET-003` | /en/settings/preferences renders (SET-003) | 1.2 s | — | — |
| ✅ passed | `SET-005` | /en/settings/notifications renders (SET-005) | 1.2 s | — | — |
| ✅ passed | — | /en/p/{slug} renders () | 1.5 s | — | — |
| ✅ passed | `BRD-001` | /en/p/{slug}/board renders (BRD-001) | 1.8 s | — | — |
| ✅ passed | `BRD-004` | /en/p/{slug}/board?view=code renders (BRD-004) | 1.7 s | — | — |
| ✅ passed | `TSK-050` | /en/p/{slug}/tasks renders (TSK-050) | 1.5 s | — | — |
| ✅ passed | `TSK-001` | /en/p/{slug}/tasks/new renders (TSK-001) | 1.8 s | — | — |
| ✅ passed | `TSK-011` | /en/p/{slug}/tasks/{n} renders (TSK-011) | 2.0 s | — | — |
| ✅ passed | `PR-001` | /en/p/{slug}/tasks/{n}/review renders (PR-001) | 1.5 s | — | — |
| ✅ passed | `TEAM-001` | /en/p/{slug}/team renders (TEAM-001) | 1.5 s | — | — |
| ✅ passed | `ACT-001` | /en/p/{slug}/activity renders (ACT-001) | 1.7 s | — | — |
| ✅ passed | `SKL-001` | /en/p/{slug}/skills renders (SKL-001) | 1.7 s | — | — |
| ✅ passed | `AUTO-001` | /en/p/{slug}/automation renders (AUTO-001) | 1.5 s | — | — |
| ✅ passed | `MEM-001` | /en/p/{slug}/memory renders (MEM-001) | 1.7 s | — | — |
| ✅ passed | `RDM-001` | /en/p/{slug}/roadmap renders (RDM-001) | 1.6 s | — | — |
| ✅ passed | `REPO-001` | /en/p/{slug}/repos renders (REPO-001) | 2.5 s | — | — |
| ✅ passed | `DOC-001` | /en/p/{slug}/documents renders (DOC-001) | 1.6 s | — | — |
| ✅ passed | `DSN-001` | /en/p/{slug}/design renders (DSN-001) | 1.9 s | — | — |
| ✅ passed | `ARC-001` | /en/p/{slug}/architect renders (ARC-001) | 1.9 s | — | — |
| ✅ passed | `SET-010` | /en/p/{slug}/settings renders (SET-010) | 3.6 s | — | — |
| ✅ passed | `PSU-004` | /en/p/{slug}/setup renders (PSU-004) | 1.8 s | — | — |
| ✅ passed | `PSU-012` | /en/p/{slug}/setup?step=review renders (PSU-012) | 1.8 s | — | — |
| ✅ passed | `I18N-001` `I18N-004` | /es/settings renders localized (I18N-001, I18N-004) | 1.1 s | — | — |
| ✅ passed | `I18N-001` `I18N-004` | /pt/settings renders localized (I18N-001, I18N-004) | 1.1 s | — | — |
| ✅ passed | `I18N-001` `I18N-004` | /fr/settings renders localized (I18N-001, I18N-004) | 1.1 s | — | — |
| ✅ passed | `I18N-001` `I18N-004` | /de/settings renders localized (I18N-001, I18N-004) | 1.2 s | — | — |
| ✅ passed | — | teardown: delete the inventory project | 43 ms | — | — |

## Cleanup

- Projects created this run: 1
  - `e2e-inventory-20261119-1454-2sjw` (01M7TF3VRY6BD5784RHVCGK0Y8)
- Leftovers after teardown: none
