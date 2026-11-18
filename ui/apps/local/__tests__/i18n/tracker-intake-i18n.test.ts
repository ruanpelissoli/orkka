import { describe, it, expect } from "vitest";
import fs from "node:fs";
import path from "node:path";

/**
 * Settings → Trackers (add / re-test / remove) and the project's ticket source (intake,
 * routing, the six-step status sync) ship in five locales at once. The component tests can't
 * catch a missing translation (the intl mock echoes keys), so this walks the real
 * catalogs.
 */
const MESSAGES_DIR = path.resolve(__dirname, "../../../../packages/i18n/messages");
const LOCALES = ["en", "es", "pt", "fr", "de"] as const;

const catalogs = Object.fromEntries(
  LOCALES.map((locale) => [
    locale,
    JSON.parse(
      fs.readFileSync(path.join(MESSAGES_DIR, locale, "dashboard.json"), "utf8")
    ) as Record<string, unknown>,
  ])
);

function leaves(block: unknown, prefix = ""): string[] {
  if (!block || typeof block !== "object") return [prefix];
  return Object.entries(block as Record<string, unknown>).flatMap(([key, value]) =>
    leaves(value, prefix ? `${prefix}.${key}` : key)
  );
}

const BLOCKS = ["trackers", "ticketSource", "ticketSourceStatus"] as const;
const STEPS = ["pickedUp", "planning", "developing", "inReview", "prOpened", "merged"];

describe("tracker intake copy", () => {
  it.each(BLOCKS)("has the same %s keys in every locale", (block) => {
    const reference = leaves(catalogs.en[block]).sort();
    for (const locale of LOCALES) {
      expect(leaves(catalogs[locale][block]).sort(), locale).toEqual(reference);
    }
  });

  it.each(LOCALES)("names and explains all six status-sync steps in %s", (locale) => {
    const source = catalogs[locale].ticketSource as Record<string, Record<string, string>>;
    expect(Object.keys(source.statusStep)).toEqual(STEPS);
    expect(Object.keys(source.statusStepHint)).toEqual(STEPS);
    for (const step of STEPS) {
      expect(source.statusStep[step]?.trim(), `${locale} ${step}`).toBeTruthy();
      expect(source.statusStepHint[step]?.trim(), `${locale} ${step} hint`).toBeTruthy();
    }
  });

  it.each(LOCALES)("tells %s users to start or update the runner, not the old connect command", (locale) => {
    const add = (catalogs[locale].trackers as Record<string, Record<string, string>>).add;
    expect(add.runnerUnsupported).toContain("./orkka start");
    expect(add.runnerUnsupported).toContain("git pull");
    const source = catalogs[locale].ticketSource as Record<string, string>;
    expect(source.runnerOffline).toContain("./orkka start");
    expect(JSON.stringify(catalogs[locale].ticketSourceStatus)).not.toContain("./orkka connect");
  });

  it.each(LOCALES)("never names the retired `orkka-local` command in %s", (locale) => {
    const dir = path.join(MESSAGES_DIR, locale);
    for (const file of fs.readdirSync(dir).filter((name) => name.endsWith(".json"))) {
      const text = fs.readFileSync(path.join(dir, file), "utf8");
      // The orkka-local://pull/N URL scheme is data, not a command.
      expect(text.replace(/orkka-local:\/\//g, ""), `${locale}/${file}`).not.toContain("orkka-local");
      expect(text, `${locale}/${file}`).not.toContain("./orkka connect");
    }
  });

  it.each(LOCALES)("states the Azure DevOps scopes in %s", (locale) => {
    const add = (catalogs[locale].trackers as Record<string, Record<string, string>>).add;
    expect(add.adoScopes).toContain("Work Items (Read & write)");
    expect(add.adoScopes).toContain("Project and Team (Read)");
  });
});
