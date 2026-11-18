import { describe, it, expect } from "vitest";
import fs from "node:fs";
import path from "node:path";

/**
 * The runner-readiness copy ships in five locales at once. The component tests can't
 * catch a missing translation — the intl mock echoes keys — so this walks the real
 * catalogs: every key the components ask for exists in en, and each touched block has the
 * same shape in every locale.
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

/** Every key the components reach for, by fully-qualified path. Kept explicit rather than
 * scraped: the note picks keys through ternaries, which a regex over `t("…")` would miss. */
const USED_KEYS = [
  // ModelBillingNote
  "modelBilling.runner",
  "modelBilling.runnerStale",
  "modelBilling.runnerUnknown",
  "modelBilling.runnerWaitingSignIn",
  "modelBilling.unavailableRunnerOnly",
  "modelBilling.copilotPremiumNote",
  "modelBilling.settingsLink",
  // TaskCreateForm / AgentConfigTab option labels
  "modelBilling.optionRunner",
  "taskCreateForm.modelNotConnected",
  // ProviderMismatchBanner (hosted)
  "providerMismatch.hostedWarning",
  "providerMismatch.reviewTeam",
  "providerMismatch.switchTeam",
  "providerMismatch.switched",
  "providerMismatch.switchedQa",
  "providerMismatch.switchFailed",
  // RunnerClient
  "runnerPage.status.readySummary",
  "runnerPage.status.readyNone",
  "runnerPage.status.copilotNote",
];

const PARITY_BLOCKS = ["modelBilling", "providerMismatch", "runnerPage.status"];

/** Cloud-execution copy removed with the runner-only pivot — must not creep back in. */
const REMOVED = [
  "executionBadge",
  "laneRunTarget",
  "runnerPage.preference",
  "taskCard.cloudReason",
  "taskCard.runnerReason",
  "taskCard.inCloud",
  "modelBilling.optionCloud",
  "modelBilling.cloudNotSignedIn",
  "modelBilling.cloudPreference",
  "orgPolicy.hybridLabel",
  "taskActions.runInCloud",
  // Credits and other cloud-product copy removed with the local-only pivot.
  "agentCard.credits",
  "agentConfig.totalCredits",
  "pipelineSettings.spendLimitCreditsLabel",
  "pipelineSettings.spendLimitCreditsDescription",
  "forecast.credits",
  "forecast.creditsRange",
  "workspaces.suspended",
  "runnerPage.status.updateAvailable",
  "permissions.MembersManage",
  "notifications.types.WebhookDisabled",
];

describe("model billing catalogs", () => {
  it("covers every key the components use in en", () => {
    const missing = USED_KEYS.filter((key) => typeof lookup(catalogs.en, key) !== "string");
    expect(missing).toEqual([]);
  });

  it("never uses the bare word subscription — that is the Stripe namespace's word", () => {
    const en = catalogs.en.modelBilling as Record<string, string>;
    for (const [key, value] of Object.entries(en)) {
      if (/subscription/i.test(value)) {
        expect(value, `modelBilling.${key}`).toMatch(/your (\{provider\}|own AI) subscription/);
      }
    }
  });

  it("no longer carries the cloud-execution keys in any locale", () => {
    for (const locale of LOCALES) {
      const present = REMOVED.filter((key) => lookup(catalogs[locale], key) !== undefined);
      expect(present, locale).toEqual([]);
    }
  });

  for (const block of PARITY_BLOCKS) {
    for (const locale of LOCALES) {
      it(`has the full ${block} key set in ${locale}`, () => {
        const en = lookup(catalogs.en, block) as Record<string, unknown>;
        const other = lookup(catalogs[locale], block) as Record<string, unknown>;
        expect(other, `${locale} is missing ${block}`).toBeDefined();
        expect(Object.keys(other).sort()).toEqual(Object.keys(en).sort());
        for (const [key, value] of Object.entries(other)) {
          expect(typeof value, `${locale}.${block}.${key} is not a string`).toBe("string");
          expect((value as string).trim(), `${locale}.${block}.${key} is empty`).toBeTruthy();
        }
      });
    }
  }
});
