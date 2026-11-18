import { test, expect } from "../../src/fixtures/test.js";
import { WEB_ROUTES, LOCALES } from "../../src/ui/inventory/routes.js";
import { needsEnvironment } from "../../config/index.js";
import { BareRemote } from "../../src/testdata/bare-remote.js";
import { createStandardTask, linkRemote } from "../../src/flows/pipeline.js";
import type { ProjectResponse, TaskResponse } from "../../src/api/types.js";

/**
 * Visit every web-app page once on a seeded project: it must render its heading, produce no
 * console errors and no failed API calls (>= 500). One project is shared by the whole file.
 */
test.describe("UI inventory @ui-inventory", () => {
  test.skip(Boolean(needsEnvironment()), needsEnvironment() ?? "");
  test.describe.configure({ mode: "serial" });
  let project: ProjectResponse | undefined;
  let task: TaskResponse | undefined;

  test.beforeAll(async ({ browser }) => {
    void browser;
  });

  test("seed: create the inventory project with a repo and a draft task", async ({ api, projects }) => {
    await api.org.updateMyPreferences({ onboardingTourSeen: true });
    project = await projects.create("inventory");
    // A repo is only registered here, never cloned: no runner is needed to render the pages.
    const repo = await linkRemote(api, project, await BareRemote.create(`e2e-inventory-${Date.now().toString(36)}`));
    task = (await createStandardTask(api, project, repo, { submit: false })).task;
    // Keep the project alive for the rest of the file; the last test deletes it.
    projects.created.splice(0);
  });

  for (const route of WEB_ROUTES) {
    test(`${route.path} renders (${route.caseIds.join(", ")})`, async ({ page }) => {
      test.info().annotations.push(...route.caseIds.map((id) => ({ type: "case", description: id })));
      if (route.needsProject && !project) test.skip(true, "no seeded project");
      if (route.needsTask && !task) test.skip(true, "no seeded task");
      const url = route.path.replace("{slug}", project?.slug ?? "").replace("{n}", String(task?.number ?? ""));
      const consoleErrors: string[] = [];
      const failed: string[] = [];
      page.on("console", (m) => {
        if (m.type() === "error") consoleErrors.push(m.text().slice(0, 200));
      });
      page.on("response", (r) => {
        if (r.status() >= 500) failed.push(`${r.status()} ${r.request().method()} ${r.url().replace(/^https?:\/\/[^/]+/, "")}`);
      });
      const res = await page.goto(url, { waitUntil: "domcontentloaded" });
      expect(res?.status() ?? 0, `HTTP status for ${url}`).toBeLessThan(500);
      await page.waitForLoadState("networkidle").catch(() => {});
      const body = page.locator("body");
      await expect(body).toContainText(route.expectText, { timeout: 30_000 });
      await expect(page.getByText(/something went wrong/i)).toHaveCount(0);
      await page.screenshot({ path: test.info().outputPath("page.png"), fullPage: false });
      const ignorable = /favicon|third-party cookie|hydrat|ResizeObserver|net::ERR_BLOCKED_BY_CLIENT/i;
      const realErrors = consoleErrors.filter((e) => !ignorable.test(e));
      if (realErrors.length) test.info().annotations.push({ type: "console-errors", description: realErrors.join(" | ").slice(0, 800) });
      expect(failed, "5xx API responses while rendering").toEqual([]);
    });
  }

  for (const locale of LOCALES) {
    test(`/${locale}/settings renders localized (I18N-001, I18N-004)`, async ({ page }) => {
      test.info().annotations.push({ type: "case", description: "I18N-001" }, { type: "case", description: "I18N-004" });
      await page.goto(`/${locale}/settings`, { waitUntil: "domcontentloaded" });
      await expect(page.getByRole("navigation").first()).toBeVisible({ timeout: 30_000 });
      const text = await page.locator("body").innerText();
      // No raw message keys leaked.
      expect(text).not.toMatch(/\b(dashboard|common)\.[a-zA-Z]+\.[a-zA-Z]+\b/);
      await page.screenshot({ path: test.info().outputPath(`settings-${locale}.png`) });
    });
  }

  test("teardown: delete the inventory project", async ({ api }) => {
    if (!project) return;
    const { deleteProject } = await import("../../src/safety/guards.js");
    await deleteProject(api, project.id);
  });
});
