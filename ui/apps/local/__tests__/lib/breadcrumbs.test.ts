import { describe, it, expect } from "vitest";
import fs from "node:fs";
import path from "node:path";
import { buildCrumbs, type Crumb } from "@/lib/breadcrumbs";

/**
 * The top bar's breadcrumb used to print raw URL segments ("Board" on a Portuguese page). Every
 * section crumb now names a key the rest of the UI already translates; this checks the trail and
 * that each key it can produce resolves in all five real catalogs.
 */
const MESSAGES_DIR = path.resolve(__dirname, "../../../../packages/i18n/messages");
const LOCALES = ["en", "es", "pt", "fr", "de"] as const;

function translate(locale: string, crumb: Crumb): string {
  if (!crumb.labelKey) return crumb.text;
  const catalog = JSON.parse(fs.readFileSync(path.join(MESSAGES_DIR, locale, `${crumb.labelKey.namespace}.json`), "utf8"));
  const value = crumb.labelKey.key.split(".").reduce<unknown>((node, part) => (node && typeof node === "object" ? (node as Record<string, unknown>)[part] : undefined), catalog);
  if (typeof value !== "string" || !value.trim()) throw new Error(`${locale}: ${crumb.labelKey.namespace}.${crumb.labelKey.key} is missing`);
  return value;
}

const PROJECT = "01M7QQKAZF8K9QSSH38K4K181S";

/** Every page the router knows (src/routes.tsx), with its ids filled in. */
const ALL_PATHS = [
  ...["", "/team", "/activity", "/skills", "/automation", "/memory", "/roadmap", "/roadmap/01MILESTONE", "/board", "/tasks", "/tasks/new", "/tasks/E2E-1", "/tasks/E2E-1/merge", "/tasks/E2E-1/review", "/repos", "/documents", "/design", "/architect", "/settings"].map((p) => `/p/${PROJECT}${p}`),
  ...["/settings/api-keys", "/settings/machine", "/settings/notifications", "/settings/preferences", "/settings/trackers", "/settings/usage", "/settings/workspace"],
  "/dashboard",
  "/inbox",
  "/notifications",
  "/projects",
  "/projects/new",
];

describe("buildCrumbs", () => {
  it("names the project by id, translates the section, keeps the task key", () => {
    const crumbs = buildCrumbs(`/p/${PROJECT}/tasks/E2E-1/review`, PROJECT);
    expect(crumbs.map((c) => c.href)).toEqual([`/p/${PROJECT}`, `/p/${PROJECT}/tasks`, `/p/${PROJECT}/tasks/E2E-1`, `/p/${PROJECT}/tasks/E2E-1/review`]);
    expect(crumbs[0].isProject).toBe(true);
    expect(crumbs.map((c) => translate("pt", c))).toEqual([PROJECT, "Tarefas", "E2E-1", "Revisão"]);
    expect(crumbs.map((c) => c.isCurrent)).toEqual([false, false, false, true]);
  });

  it("shows the board in the page's language (the reported bug)", () => {
    const [, board] = buildCrumbs(`/p/${PROJECT}/board`, PROJECT);
    expect(translate("pt", board)).toBe("Quadro");
    expect(translate("de", board)).toBe("Board");
    expect(translate("fr", board)).toBe("Tableau");
  });

  it("labels the settings pages with the settings nav", () => {
    const crumbs = buildCrumbs("/settings/api-keys");
    expect(crumbs.map((c) => translate("en", c))).toEqual(["Settings", "MCP keys"]);
    expect(crumbs.map((c) => translate("pt", c))).toEqual(["Configurações", "Chaves MCP"]);
  });

  it("does not treat an id that happens to spell a section as one", () => {
    // A milestone id under roadmap stays as typed; "board" only counts directly under a project.
    expect(buildCrumbs(`/p/${PROJECT}/roadmap/board`, PROJECT)[2].labelKey).toBeNull();
  });

  it.each(LOCALES)("every section of every route resolves in %s", (locale) => {
    for (const p of ALL_PATHS) {
      for (const crumb of buildCrumbs(p, PROJECT)) {
        if (crumb.isProject) continue;
        const isId = /^(E2E-1|01MILESTONE)$/.test(crumb.text);
        if (isId) expect(crumb.labelKey, `${p}: ${crumb.text}`).toBeNull();
        else expect(crumb.labelKey, `${p}: "${crumb.text}" has no translated label`).not.toBeNull();
        expect(() => translate(locale, crumb)).not.toThrow();
      }
    }
  });
});
