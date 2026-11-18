import { test, expect, caseIds } from "../../src/fixtures/test.js";
import { cfg, needsEnvironment } from "../../config/index.js";

test.describe("smoke @smoke", () => {
  test.skip(Boolean(needsEnvironment()), needsEnvironment() ?? "");

  test("API: org/me answers for the harness key and matches the expected workspace @smoke", async ({ api }) => {
    const me = await api.org.me();
    expect(me.orgId).toBeTruthy();
    expect(me.permissions.length).toBeGreaterThan(0);
    test.info().annotations.push({ type: "org", description: `${me.orgName} role=${me.role} perms=${me.permissions.length}` });
  });

  test("API: workspaces list answers with exactly one active workspace @smoke", caseIds("WSP-001"), async ({ api }) => {
    const list = await api.workspaces.list();
    expect(list.length).toBeGreaterThan(0);
    expect(list.filter((w) => w.isActive)).toHaveLength(1);
  });

  test("API: runner presence answers, with forge CLI status when online @smoke", async ({ api }) => {
    const r = await api.me.runner();
    expect(typeof r.online).toBe("boolean");
    if (r.online) expect(Array.isArray(r.forgeClis), "a pivot-era runner reports gh/az/glab").toBe(true);
    test.info().annotations.push({ type: "runner-state", description: `online=${r.online} machine=${r.machineName} v=${r.daemonVersion} caps=${r.capabilities?.length ?? "n/a"} forge=${(r.forgeClis ?? []).map((c) => `${c.cli}:${c.authenticated}`).join(",")}` });
  });

  test("API: tracker connections list answers (possibly empty) @smoke", caseIds("TSRC-009"), async ({ api }) => {
    const list = await api.trackers.connections();
    expect(Array.isArray(list)).toBe(true);
  });

  test("UI: the launch-code session opens the dashboard through the host's front door @smoke", caseIds("LWF-001"), async ({ page }) => {
    const res = await page.goto("/en/dashboard");
    expect(res?.status() ?? 0).toBeLessThan(400);
    await expect(page.getByRole("navigation").first()).toBeVisible({ timeout: 30_000 });
    await expect(page.getByText("Open Orkka from your terminal")).toHaveCount(0);
  });

  test("UI: a browser without the session gets the signed-out page, not the app @smoke", caseIds("LWF-001"), async ({ browser }) => {
    const context = await browser.newContext({ storageState: undefined });
    const page = await context.newPage();
    const res = await page.goto(new URL("/en/dashboard", cfg.webUrl).toString());
    expect(res?.status()).toBe(401);
    await expect(page.getByText("Open Orkka from your terminal")).toBeVisible();
    await context.close();
  });

  test("UI: project settings page renders for the first project (or onboarding when none) @smoke", caseIds("SET-012"), async ({ page, api }) => {
    const projects = await api.projects.list();
    if (projects.length === 0) {
      await page.goto("/en/dashboard");
      await expect(page.getByRole("heading").first()).toBeVisible();
      return;
    }
    await page.goto(`/en/p/${projects[0].slug}/settings`);
    await expect(page.getByRole("heading").first()).toBeVisible({ timeout: 30_000 });
  });
});
