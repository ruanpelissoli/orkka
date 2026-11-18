import { describe, it, expect } from "vitest";
import fs from "node:fs";
import path from "node:path";
import { laneLabel } from "@/components/board/lane-label";
import { normalizeBoardLaneKey } from "@/lib/constants";

/**
 * The board's lane headers printed the board endpoint's English labels ("DRAFTING", "READY")
 * on every locale (R5 e2e run: a Portuguese board). Every lane the endpoint can return now
 * resolves to the reader's language from the real catalogs.
 */
const MESSAGES_DIR = path.resolve(__dirname, "../../../../packages/i18n/messages");
const LOCALES = ["en", "es", "pt", "fr", "de"] as const;
// GetBoardEndpoint.BuildLanes, Testing included (it appears when the project tests).
const BOARD_KEYS = ["drafting", "ready", "planning", "plan_ready", "pending_questions", "developing", "testing", "code_review", "done"];

function translator(locale: string, namespace: string) {
  const catalog = JSON.parse(fs.readFileSync(path.join(MESSAGES_DIR, locale, `${namespace}.json`), "utf8"));
  const lookup = (key: string) =>
    key.split(".").reduce<unknown>((node, part) => (node && typeof node === "object" ? (node as Record<string, unknown>)[part] : undefined), catalog);
  const t = (key: string) => {
    const v = lookup(key);
    if (typeof v !== "string") throw new Error(`${locale}/${namespace}: ${key} missing`);
    return v;
  };
  t.has = (key: string) => typeof lookup(key) === "string";
  return t;
}

describe("laneLabel", () => {
  it.each(LOCALES)("names every board lane from the %s catalogs, never the server's English fallback", (locale) => {
    const tCommon = translator(locale, "common");
    const tDash = translator(locale, "dashboard");
    for (const key of BOARD_KEYS) {
      expect(laneLabel(normalizeBoardLaneKey(key), tCommon, tDash, "FALLBACK"), key).not.toBe("FALLBACK");
    }
  });

  it("is Portuguese on a Portuguese board", () => {
    const tCommon = translator("pt", "common");
    const tDash = translator("pt", "dashboard");
    expect(["drafting", "ready", "testing", "done"].map((k) => laneLabel(normalizeBoardLaneKey(k), tCommon, tDash, "x"))).toEqual([
      "Rascunho",
      "Pronto",
      "Testes",
      "Concluído",
    ]);
  });

  it("falls back to the server label for a lane no catalog knows", () => {
    const t = Object.assign((k: string) => k, { has: () => false });
    expect(laneLabel("Custom", t, t, "My lane")).toBe("My lane");
    expect(laneLabel("Custom", t, t)).toBe("Custom");
  });
});
