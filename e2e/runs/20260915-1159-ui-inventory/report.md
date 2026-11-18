# Run 20260915-1159-ui-inventory

| | |
|---|---|
| Suite | `ui-inventory` |
| Target | prod |
| Started | 2026-09-15T14:59:39.985Z |
| Duration | 4m 19s |
| Result | 49 passed · 0 failed · 0 timed out · 0 skipped |
| Spend | $0.00 of $5.00 cap (credits 3294 → 3294) |
| Operator | claude · harness uncommitted · node v24.18.0 · playwright 1.63.0 |

## Notes
- Credentials are not an Owner (role=Member); permission-gated cases may fail.
- Org plan has no team features (not Enterprise); Members cases will be skipped.

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ✅ passed | — | seed: create the inventory project with a repo and a draft task | 1.6 s | $0.000 | e2e-inventory-20260915-1159-s4rv<br>e2e-inventory-20260915-1159-s4rv |
| ✅ passed | `MKT-001` | / renders (MKT-001) | 7.0 s | $0.000 | — |
| ✅ passed | `MKT-005` | /pricing renders (MKT-005) | 4.6 s | $0.000 | — |
| ✅ passed | `MKT-006` | /docs renders (MKT-006) | 3.9 s | $0.000 | — |
| ✅ passed | `MKT-007` | /local renders (MKT-007) | 3.7 s | $0.000 | — |
| ✅ passed | `MKT-008` | /terms renders (MKT-008) | 4.1 s | $0.000 | — |
| ✅ passed | `MKT-008` | /privacy renders (MKT-008) | 4.7 s | $0.000 | — |
| ✅ passed | `MKT-008` | /dpa renders (MKT-008) | 3.4 s | $0.000 | — |
| ✅ passed | `ONB-001` | /dashboard renders (ONB-001) | 6.6 s | $0.000 | — |
| ✅ passed | `NTF-001` | /inbox renders (NTF-001) | 5.0 s | $0.000 | — |
| ✅ passed | `NTF-003` | /notifications renders (NTF-003) | 6.6 s | $0.000 | — |
| ✅ passed | `ONB-002` | /onboarding renders (ONB-002) | 5.0 s | $0.000 | — |
| ✅ passed | `SET-001` | /settings renders (SET-001) | 4.9 s | $0.000 | — |
| ✅ passed | `SET-003` | /settings/preferences renders (SET-003) | 5.0 s | $0.000 | — |
| ✅ passed | `BIL-002` | /settings/billing renders (BIL-002) | 5.4 s | $0.000 | — |
| ✅ passed | `BIL-004` | /settings/billing/credits renders (BIL-004) | 5.5 s | $0.000 | — |
| ✅ passed | `BIL-012` | /settings/billing/budget renders (BIL-012) | 5.0 s | $0.000 | — |
| ✅ passed | `BIL-014` | /settings/billing/invoices renders (BIL-014) | 5.5 s | $0.000 | — |
| ✅ passed | `BIL-016` | /settings/billing/payment renders (BIL-016) | 7.6 s | $0.000 | — |
| ✅ passed | `SET-005` | /settings/notifications renders (SET-005) | 5.2 s | $0.000 | — |
| ✅ passed | `RUN-002` | /settings/runner renders (RUN-002) | 5.0 s | $0.000 | — |
| ✅ passed | `SET-006` | /settings/organization renders (SET-006) | 5.7 s | $0.000 | — |
| ✅ passed | `KEY-001` | /settings/api-keys renders (KEY-001) | 4.9 s | $0.000 | — |
| ✅ passed | `INT-001` | /settings/integrations renders (INT-001) | 4.9 s | $0.000 | — |
| ✅ passed | `AUD-001` | /settings/audit renders (AUD-001) | 5.2 s | $0.000 | — |
| ✅ passed | `NAV-003` | /p/{slug} renders (NAV-003) | 6.1 s | $0.000 | — |
| ✅ passed | `BRD-001` | /p/{slug}/board renders (BRD-001) | 6.1 s | $0.000 | — |
| ✅ passed | `BRD-004` | /p/{slug}/board?view=code renders (BRD-004) | 6.7 s | $0.000 | — |
| ✅ passed | `TSK-050` | /p/{slug}/tasks renders (TSK-050) | 5.8 s | $0.000 | — |
| ✅ passed | `TSK-001` | /p/{slug}/tasks/new renders (TSK-001) | 5.9 s | $0.000 | — |
| ✅ passed | `TSK-011` | /p/{slug}/tasks/{n} renders (TSK-011) | 6.1 s | $0.000 | — |
| ✅ passed | `PR-001` | /p/{slug}/tasks/{n}/review renders (PR-001) | 6.5 s | $0.000 | — |
| ✅ passed | `TEAM-001` | /p/{slug}/team renders (TEAM-001) | 6.1 s | $0.000 | — |
| ✅ passed | `ACT-001` | /p/{slug}/activity renders (ACT-001) | 5.9 s | $0.000 | — |
| ✅ passed | `SKL-001` | /p/{slug}/skills renders (SKL-001) | 6.1 s | $0.000 | — |
| ✅ passed | `AUTO-001` | /p/{slug}/automation renders (AUTO-001) | 6.1 s | $0.000 | — |
| ✅ passed | `MEM-001` | /p/{slug}/memory renders (MEM-001) | 6.4 s | $0.000 | — |
| ✅ passed | `RDM-001` | /p/{slug}/roadmap renders (RDM-001) | 5.9 s | $0.000 | — |
| ✅ passed | `REPO-001` | /p/{slug}/repos renders (REPO-001) | 7.0 s | $0.000 | — |
| ✅ passed | `DOC-001` | /p/{slug}/documents renders (DOC-001) | 6.1 s | $0.000 | — |
| ✅ passed | `DSN-001` | /p/{slug}/design renders (DSN-001) | 5.7 s | $0.000 | — |
| ✅ passed | `ARC-001` | /p/{slug}/architect renders (ARC-001) | 5.1 s | $0.000 | — |
| ✅ passed | `MBR-001` | /p/{slug}/members renders (MBR-001) | 5.9 s | $0.000 | — |
| ✅ passed | `SET-010` | /p/{slug}/settings renders (SET-010) | 6.1 s | $0.000 | — |
| ✅ passed | `I18N-001` `I18N-004` | /es/settings renders localized (I18N-001, I18N-004) | 2.6 s | $0.000 | — |
| ✅ passed | `I18N-001` `I18N-004` | /pt/settings renders localized (I18N-001, I18N-004) | 2.2 s | $0.000 | — |
| ✅ passed | `I18N-001` `I18N-004` | /fr/settings renders localized (I18N-001, I18N-004) | 2.2 s | $0.000 | — |
| ✅ passed | `I18N-001` `I18N-004` | /de/settings renders localized (I18N-001, I18N-004) | 2.1 s | $0.000 | — |
| ✅ passed | — | teardown: delete the inventory project | 1.1 s | $0.000 | — |

## Cleanup

- Projects created this run: 1
  - Unmanaged `e2e-inventory-20260915-1159-s4rv` (01M2JS9FJ5ZZ2A41NN7XJX87K9)
- Leftovers after teardown: none
