import { describe, it, expect } from "vitest";
import fs from "node:fs";
import path from "node:path";

/**
 * Add-by-URL, the forge-CLI runner section and the publish wording ship in five locales at
 * once. The component tests can't catch a missing translation (the intl mock echoes
 * keys), so this walks the real catalogs: every new block has the same key tree in every
 * locale, and the copy that served the removed GitHub App flows is gone everywhere.
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

function lookup(block: unknown, dotted: string): unknown {
  return dotted.split(".").reduce<unknown>((node, part) => {
    if (node && typeof node === "object") return (node as Record<string, unknown>)[part];
    return undefined;
  }, block);
}

/** Every leaf path under a block, e.g. "kind.https". */
function leaves(block: unknown, prefix = ""): string[] {
  if (!block || typeof block !== "object") return [prefix];
  return Object.entries(block as Record<string, unknown>).flatMap(([key, value]) =>
    leaves(value, prefix ? `${prefix}.${key}` : key)
  );
}

const BLOCKS = [
  "forges",
  "addRepoByUrl",
  "repoAccessCheck",
  "forgePr",
  "runnerPage.forgeClis",
  "trackers",
  "ticketSource",
  "ticketSourceStatus",
  "externalTicket",
  "workspaces",
  // Base-folder projects (create from a folder, Repos page rescan).
  "folderPicker",
  "fromFolder",
  "baseFolder",
];

const REMOVED = [
  "repoMisc.githubConnect",
  "repoMisc.prList.openOnGitHub",
  "onboarding.installGitHubApp",
  "onboarding.connectGitHub",
  "onboardingMisc.installUrlFailed",
  "milestonePage.ship",
  "automationPage.triggersTitle",
  "automationPage.issueTrigger",
  "automationPage.ciTrigger",
  "branchList.create",
  "taskDetailEditable.openOnGitHub",
  // Local-only: the cloud surfaces (onboarding wizard, billing, members, invites) are gone.
  "onboardingWizard",
  "billingPage",
  "membersPage",
  "invitePage",
  "runnerPage.gettingStarted",
  "runnerPage.tokens.generate",
  // The manual create-project form gave way to creating a project from a folder.
  "onboarding",
  "onboardingMisc",
  "folderPicker.addCount",
  "folderPicker.createAndAdd",
];

describe("repo / forge catalogs", () => {
  for (const block of BLOCKS) {
    for (const locale of LOCALES) {
      it(`has the full ${block} key tree in ${locale}`, () => {
        const en = lookup(catalogs.en, block);
        const other = lookup(catalogs[locale], block);
        expect(other, `${locale} is missing ${block}`).toBeDefined();
        expect(leaves(other).sort()).toEqual(leaves(en).sort());
        for (const leaf of leaves(other)) {
          const value = lookup(other, leaf);
          expect(typeof value, `${locale}.${block}.${leaf}`).toBe("string");
          expect((value as string).trim(), `${locale}.${block}.${leaf} is empty`).toBeTruthy();
        }
      });
    }
  }

  it("names every forge the add-repo select offers", () => {
    for (const locale of LOCALES) {
      expect(Object.keys(lookup(catalogs[locale], "forges") as object).sort()).toEqual(
        ["azuredevops", "bitbucket", "github", "gitlab", "other"]
      );
    }
  });

  it("has a status label and remedy copy for each forge CLI", () => {
    const forgeClis = lookup(catalogs.en, "runnerPage.forgeClis") as Record<string, unknown>;
    expect(Object.keys(forgeClis.cli as object).sort()).toEqual(["az", "gh", "glab"]);
    expect(Object.keys(forgeClis.state as object).sort()).toEqual(
      ["notInstalled", "notSignedIn", "ready"]
    );
  });

  for (const key of REMOVED) {
    it(`no longer carries ${key} in any locale`, () => {
      for (const locale of LOCALES) {
        expect(lookup(catalogs[locale], key), `${locale}.${key}`).toBeUndefined();
      }
    });
  }
});
