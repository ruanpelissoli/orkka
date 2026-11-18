import fs from "node:fs";
import path from "node:path";
import { test, expect, caseIds, type Page } from "../../src/fixtures/test.js";
import { cfg, env, needsEnvironment } from "../../config/index.js";
import type { ProjectResponse } from "../../src/api/types.js";

/**
 * The key screens of the local product, captured through the host's front door with the
 * launch-code session: dashboard, create-from-folder preview, board (with a Story chip when the
 * workflow spec left one), task detail with the Story panel and the Discuss tab, Settings →
 * Trackers, the ticket source card (status sync) and the Machine page.
 *
 * It reads whatever the workflow left behind (the first `e2e-*` project) and creates nothing
 * except the folder preview (which only discovers). Screenshots go to
 * `<E2E_LOCAL_SANDBOX>/screens/` (outside the repo) and are attached to the run report.
 * `E2E_SCREENS_FOLDER` = the base folder the create-from-folder preview opens (default
 * `<sandbox>/shop`); `E2E_SCREENS_PROJECT` = the project's slug (default: the first `e2e-*`). @screens
 */
const OUT = path.join(env.E2E_LOCAL_SANDBOX, "screens");

async function shot(page: Page, name: string) {
  fs.mkdirSync(OUT, { recursive: true });
  await page.waitForLoadState("networkidle").catch(() => {});
  await page.waitForTimeout(800);
  const file = path.join(OUT, `${name}.png`);
  await page.screenshot({ path: file, fullPage: true });
  await test.info().attach(name, { path: file, contentType: "image/png" });
  return file;
}

/** Fails the screen on a server error, a console error or a raw i18n key. */
function guard(page: Page) {
  const problems: string[] = [];
  page.on("console", (m) => {
    if (m.type() === "error" && !/favicon|Download the React DevTools/.test(m.text())) problems.push(`console: ${m.text().slice(0, 200)}`);
  });
  page.on("response", (r) => {
    if (r.status() >= 500) problems.push(`${r.status()} ${r.request().method()} ${r.url().replace(/^https?:\/\/[^/]+/, "")}`);
  });
  return problems;
}

async function noRawKeys(page: Page) {
  const text = await page.locator("main").innerText().catch(() => "");
  expect(text, "a raw i18n key on the page").not.toMatch(/\b(dashboard|common|settings)\.[a-z]+[A-Z]\w*\.\w+/);
}

test.describe("key screens @screens", () => {
  test.skip(Boolean(needsEnvironment()), needsEnvironment() ?? "");
  test.describe.configure({ mode: "serial" });
  let project: ProjectResponse | undefined;

  test.beforeAll(async () => {
    const { createApi } = await import("../../src/auth/api-key.js");
    // The first-visit product tour would cover every project page.
    await createApi().org.updateMyPreferences({ onboardingTourSeen: true });
    const all = await createApi().projects.list();
    const wanted = process.env.E2E_SCREENS_PROJECT;
    project = wanted ? all.find((p) => p.slug === wanted || p.name === wanted) : all.find((p) => p.name.startsWith(cfg.projectPrefix));
  });

  test("dashboard", caseIds("LWF-009"), async ({ page }) => {
    const problems = guard(page);
    await page.goto("/en/dashboard");
    await expect(page.getByRole("navigation").first()).toBeVisible();
    await shot(page, "01-dashboard");
    await noRawKeys(page);
    expect(problems).toEqual([]);
  });

  test("create from folder: the preview lists both repositories with their location", async ({ page }) => {
    const problems = guard(page);
    const folder = process.env.E2E_SCREENS_FOLDER ?? path.join(env.E2E_LOCAL_SANDBOX, "shop");
    // The picker starts at the machine's roots; open it straight at the base folder's parent.
    await page.route("**/api/runners/fs/browse", async (route) => {
      const body = JSON.parse(route.request().postData() ?? "{}") as { path?: string | null };
      if (!body.path) return route.continue({ postData: JSON.stringify({ ...body, path: path.dirname(folder) }) });
      return route.continue();
    });
    await page.goto("/en/projects/new");
    await page.getByRole("button", { name: /choose folder/i }).click();
    await page.getByText(path.basename(folder), { exact: true }).first().click();
    await page.getByRole("button", { name: new RegExp(`^use ${path.basename(folder)}$`, "i") }).click();
    await expect(page.getByText(/remote at github\.com|github\.com/i).first()).toBeVisible({ timeout: 60_000 });
    await expect(page.getByText(/local folder/i).first()).toBeVisible();
    await shot(page, "02-create-from-folder-preview");
    await noRawKeys(page);
    expect(problems).toEqual([]);
  });

  test("board (with the Story chip when a Story is on it)", async ({ page }) => {
    test.skip(!project, "no e2e project — run the workflow spec first");
    const problems = guard(page);
    await page.goto(`/en/p/${project!.slug}/board`);
    await expect(page.getByText(/SHOP-|ES-/).first()).toBeVisible({ timeout: 30_000 });
    await shot(page, "03-board");
    // The finished Story sits in the Done lane, off to the right.
    const card = page.getByText(/SHOP-2\d/).first();
    if (await card.count()) {
      await card.scrollIntoViewIfNeeded();
      await shot(page, "03b-board-story-chip");
    }
    await noRawKeys(page);
    expect(problems).toEqual([]);
  });

  test("board in Portuguese: the top-bar breadcrumb follows the locale", async ({ page }) => {
    test.skip(!project, "no e2e project — run the workflow spec first");
    const problems = guard(page);
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto(`/pt/p/${project!.slug}/board`);
    await expect(page.getByText(/SHOP-|ES-/).first()).toBeVisible({ timeout: 30_000 });
    // The crumb used to print the raw URL segment ("Board") on every locale.
    const crumbs = page.locator("header nav");
    await expect(crumbs.getByText("Quadro", { exact: true })).toBeVisible();
    await expect(crumbs.getByText("Board", { exact: true })).toHaveCount(0);
    // So did the lane headers ("DRAFTING" on a Portuguese board).
    await expect(page.getByText("Rascunho", { exact: true }).first()).toBeVisible();
    await expect(page.getByText("Drafting", { exact: true })).toHaveCount(0);
    await shot(page, "03c-board-pt-breadcrumb");
    await noRawKeys(page);
    expect(problems).toEqual([]);
  });

  test("task detail with the Story panel, then the Discuss tab", async ({ page, api }) => {
    test.skip(!project, "no e2e project — run the workflow spec first");
    const tasks = await api.tasks.list(project!.id);
    const list = Array.isArray(tasks) ? tasks : tasks.items;
    const member = list.find((t) => (t as unknown as { groupId?: string }).groupId) ?? list[0];
    const problems = guard(page);
    await page.goto(`/en/p/${project!.slug}/tasks/${member.number}`);
    await expect(page.getByText(member.title.slice(0, 20)).first()).toBeVisible({ timeout: 30_000 });
    await shot(page, "04-task-detail-story");
    const discussTab = page.getByRole("group", { name: /conversation|discuss/i }).getByText("Discuss", { exact: true }).or(page.getByText("Discuss", { exact: true })).first();
    await discussTab.scrollIntoViewIfNeeded();
    await discussTab.click();
    await page.waitForTimeout(1500);
    await shot(page, "05-task-discuss");
    await noRawKeys(page);
    expect(problems).toEqual([]);
  });

  test("Settings → Trackers", async ({ page }) => {
    const problems = guard(page);
    await page.goto("/en/settings/trackers");
    await expect(page.getByText(/jira/i).first()).toBeVisible({ timeout: 30_000 });
    await shot(page, "06-settings-trackers");
    await noRawKeys(page);
    expect(problems).toEqual([]);
  });

  test("project settings: the ticket source card with status sync", async ({ page }) => {
    test.skip(!project, "no e2e project — run the workflow spec first");
    const problems = guard(page);
    await page.goto(`/en/p/${project!.slug}/settings`);
    const card = page.getByText(/ticket source/i).first();
    await expect(card).toBeVisible({ timeout: 30_000 });
    await card.scrollIntoViewIfNeeded();
    await shot(page, "07-ticket-source");
    const sync = page.getByText(/PR opened/i).first();
    await sync.scrollIntoViewIfNeeded();
    await shot(page, "07b-ticket-source-status-sync");
    await noRawKeys(page);
    expect(problems).toEqual([]);
  });

  test("Machine page", async ({ page }) => {
    const problems = guard(page);
    await page.goto("/en/settings/machine");
    await expect(page.getByText(/claude/i).first()).toBeVisible({ timeout: 30_000 });
    await shot(page, "08-machine");
    await noRawKeys(page);
    expect(problems).toEqual([]);
  });
});
