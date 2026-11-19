/**
 * Every page of the local UI (orkka-ui apps/local) the UI-inventory walk visits through the host's
 * front door, with what must be visible for the page to count as rendered. `{slug}`/`{n}` are
 * substituted with the seeded project and its first task. Paths carry the locale (`/en/…`); the
 * cloud-only pages (billing, members, integrations, admin, marketing) were deleted in 2026-11.
 */
export interface RouteCheck {
  path: string;
  /** Regex matched against the page body. */
  expectText: RegExp;
  caseIds: string[];
  needsProject?: boolean;
  needsTask?: boolean;
}

export const WEB_ROUTES: RouteCheck[] = [
  // Dashboard-level
  { path: "/en/dashboard", expectText: /./, caseIds: ["ONB-001"] },
  // The workspace board (BRD-039…047, planned) — here only "it renders".
  { path: "/en/board", expectText: /workspace board|no projects yet/i, caseIds: [] },
  { path: "/en/inbox", expectText: /inbox/i, caseIds: ["NTF-001"] },
  { path: "/en/notifications", expectText: /notifications/i, caseIds: ["NTF-003"] },
  { path: "/en/projects", expectText: /project/i, caseIds: [] },
  { path: "/en/projects/new", expectText: /new project/i, caseIds: ["LWF-002"] },
  // Settings
  { path: "/en/settings", expectText: /settings/i, caseIds: ["SET-001"] },
  { path: "/en/settings/workspace", expectText: /workspace/i, caseIds: ["LWF-013"] },
  { path: "/en/settings/machine", expectText: /runner status|machine/i, caseIds: ["LWF-009"] },
  { path: "/en/settings/trackers", expectText: /ticket trackers/i, caseIds: ["LWF-009"] },
  { path: "/en/settings/usage", expectText: /usage/i, caseIds: ["LWF-012"] },
  { path: "/en/settings/api-keys", expectText: /keys/i, caseIds: ["KEY-001"] },
  { path: "/en/settings/preferences", expectText: /preferences|technical/i, caseIds: ["SET-003"] },
  { path: "/en/settings/notifications", expectText: /notifications/i, caseIds: ["SET-005"] },
  // Project pages
  { path: "/en/p/{slug}", expectText: /./, caseIds: [], needsProject: true },
  { path: "/en/p/{slug}/board", expectText: /board/i, caseIds: ["BRD-001"], needsProject: true },
  { path: "/en/p/{slug}/board?view=code", expectText: /code|board/i, caseIds: ["BRD-004"], needsProject: true },
  { path: "/en/p/{slug}/tasks", expectText: /tasks/i, caseIds: ["TSK-050"], needsProject: true },
  { path: "/en/p/{slug}/tasks/new", expectText: /task/i, caseIds: ["TSK-001"], needsProject: true },
  { path: "/en/p/{slug}/tasks/{n}", expectText: /./, caseIds: ["TSK-011"], needsProject: true, needsTask: true },
  { path: "/en/p/{slug}/tasks/{n}/review", expectText: /review|pull request|no pull/i, caseIds: ["PR-001"], needsProject: true, needsTask: true },
  { path: "/en/p/{slug}/team", expectText: /team/i, caseIds: ["TEAM-001"], needsProject: true },
  { path: "/en/p/{slug}/activity", expectText: /activity/i, caseIds: ["ACT-001"], needsProject: true },
  { path: "/en/p/{slug}/skills", expectText: /skills/i, caseIds: ["SKL-001"], needsProject: true },
  { path: "/en/p/{slug}/automation", expectText: /automation/i, caseIds: ["AUTO-001"], needsProject: true },
  { path: "/en/p/{slug}/memory", expectText: /memory/i, caseIds: ["MEM-001"], needsProject: true },
  { path: "/en/p/{slug}/roadmap", expectText: /roadmap/i, caseIds: ["RDM-001"], needsProject: true },
  { path: "/en/p/{slug}/repos", expectText: /repositor/i, caseIds: ["REPO-001"], needsProject: true },
  { path: "/en/p/{slug}/documents", expectText: /documents/i, caseIds: ["DOC-001"], needsProject: true },
  { path: "/en/p/{slug}/design", expectText: /design/i, caseIds: ["DSN-001"], needsProject: true },
  { path: "/en/p/{slug}/architect", expectText: /diagrams|architect/i, caseIds: ["ARC-001"], needsProject: true },
  { path: "/en/p/{slug}/settings", expectText: /settings|general/i, caseIds: ["SET-010"], needsProject: true },
];

export const LOCALES = ["es", "pt", "fr", "de"] as const;
