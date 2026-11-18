import fs from "node:fs";
import { chromium, type Browser, type BrowserContext, type Page } from "@playwright/test";
import { cfg, paths } from "../../config/index.js";
import { fileAgeMs } from "../util/fs.js";

export function storageStateExists(): boolean {
  return fs.existsSync(paths.storageState);
}

export function storageStateAgeHours(): number | undefined {
  const ms = fileAgeMs(paths.storageState);
  return ms === undefined ? undefined : ms / 3_600_000;
}

/**
 * Open a headless context from the saved local session (the cookie a launch code set) and land
 * on the dashboard. `ok: false` when the host answered its "Open Orkka from your terminal" page
 * (no session — the stack was restarted, sessions live in memory) — never blocks.
 */
export async function openSignedInPage(browser?: Browser): Promise<{ ok: boolean; browser: Browser; context: BrowserContext; page: Page; landedOn: string }> {
  const b = browser ?? (await chromium.launch({ headless: true }));
  const context = await b.newContext({ storageState: storageStateExists() ? paths.storageState : undefined, locale: "en-US" });
  const page = await context.newPage();
  const res = await page.goto(new URL("/en/dashboard", cfg.webUrl).toString(), { waitUntil: "domcontentloaded" }).catch(() => null);
  return { ok: Boolean(res && res.status() < 400), browser: b, context, page, landedOn: page.url() };
}
