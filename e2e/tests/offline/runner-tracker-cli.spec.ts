import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execa } from "execa";
import { test, expect, caseIds } from "../../src/fixtures/test.js";
import { env } from "../../config/index.js";
import { readTrackersFile, removeSeededConnection, seedTrackerConnection, type SeededConnection } from "../../src/runner/tracker-seed.js";
import { orkkaDllIfBuilt, RUNNER_TEST_OPT_INS } from "../../src/runner/process.js";
import { rand } from "../../src/util/fs.js";

/**
 * The REAL runner's tracker CLI against the fake tracker, in a throwaway ORKKA_HOME and a
 * throwaway workspace profile — proves the fake answers JiraCloudClient / AzureDevOpsClient
 * exactly as the runner parses it, and that the runner's test opt-ins (loopback site, file
 * secret store) make `tracker add` work against it — and only with them. Needs an
 * `./orkka` that has `tracker` and honours ORKKA_HOME (a current build of
 * orkka-api/src/Orkka.LocalRunner); skips otherwise. No Orkka deployment needed. @offline
 */
const BUILT = env.E2E_ORKKA_DLL;

test.describe("./orkka tracker CLI against the fake tracker @offline", () => {
  test.describe.configure({ mode: "serial" });
  let exe = "";
  let home = "";
  const workspace = `e2e-cli-${rand(6).toLowerCase()}`;
  const seeded: SeededConnection[] = [];

  test.beforeAll(async () => {
    exe = orkkaDllIfBuilt() ?? "";
    home = fs.mkdtempSync(path.join(os.tmpdir(), "orkka-e2e-home-"));
  });

  test.afterAll(async () => {
    for (const c of seeded) await removeSeededConnection(c).catch(() => {});
    if (home) fs.rmSync(home, { recursive: true, force: true });
  });

  const run = (args: string[], input = "", optIns = true) =>
    execa("dotnet", [exe, ...args, "--workspace", workspace], {
      env: { ...process.env, ORKKA_HOME: home, ORKKA_NO_UPDATE_CHECK: "1", ...(optIns ? RUNNER_TEST_OPT_INS : {}) },
      input, reject: false, timeout: 90_000, cwd: os.homedir(),
    });

  test("tracker test/list: the runner signs in to the fake Jira and ADO with the seeded tokens; tokens never printed", caseIds("TCLI-007", "TCLI-008", "TCLI-011"), async ({ fakeTracker }) => {
    test.skip(!exe, `no ./orkka with tracker support (no build at ${BUILT})`);
    const jira = await seedTrackerConnection({ id: "jira-fake", kind: "jira", siteUrl: fakeTracker.jiraSiteUrl, email: fakeTracker.jira.email, secret: fakeTracker.jira.token, workspace, home });
    const ado = await seedTrackerConnection({ id: "ado-fake", kind: "ado", siteUrl: fakeTracker.adoSiteUrl, secret: fakeTracker.ado.pat, workspace, home });
    seeded.push(jira, ado);

    const test1 = await run(["tracker", "test", "jira-fake"]);
    expect(test1.exitCode, test1.stdout + test1.stderr).toBe(0);
    expect(test1.stdout).toMatch(new RegExp(`jira-fake: ok [—-] signed in as ${fakeTracker.jira.displayName}`));
    const test2 = await run(["tracker", "test", "ado-fake"]);
    expect(test2.exitCode, test2.stdout + test2.stderr).toBe(0);
    expect(test2.stdout).toMatch(new RegExp(`ado-fake: ok [—-] signed in as ${fakeTracker.ado.displayName}`));

    // The runner really called the fake with the right Basic credentials.
    expect(fakeTracker.requestsTo("jira", { path: /\/rest\/api\/3\/myself$/ }).map((r) => r.authOk)).toEqual([true]);
    expect(fakeTracker.requestsTo("ado", { path: /\/_apis\/connectionData$/ }).map((r) => r.authOk)).toEqual([true]);

    // The health check was recorded in trackers.json; the token is nowhere in it or in the output.
    const entries = readTrackersFile(workspace, home);
    expect(entries.find((e) => e.id === "jira-fake")).toMatchObject({ healthy: true, displayName: fakeTracker.jira.displayName, siteUrl: fakeTracker.jiraSiteUrl });
    const list = await run(["tracker", "list"]);
    expect(list.stdout).toMatch(/jira-fake\s+jira\s+http:\/\/127\.0\.0\.1:\d+\s+E2E Developer\s+ok/);
    for (const text of [list.stdout, list.stderr, test1.stdout, test2.stdout, fs.readFileSync(path.join(home, "workspaces", workspace, "trackers.json"), "utf8")]) {
      expect(text).not.toContain(fakeTracker.jira.token);
      expect(text).not.toContain(fakeTracker.ado.pat);
    }
  });

  test("a revoked token: tracker test reports failing with Jira's advice and exits 1", caseIds("TCLI-008"), async ({ fakeTracker }) => {
    test.skip(!exe, "no ./orkka with tracker support");
    seeded.push(await seedTrackerConnection({ id: "jira-revoked", kind: "jira", siteUrl: fakeTracker.jiraSiteUrl, email: fakeTracker.jira.email, secret: "not-the-token", workspace, home }));
    const r = await run(["tracker", "test", "jira-revoked"]);
    expect(r.exitCode).toBe(1);
    expect(r.stdout).toMatch(/jira-revoked: failing [—-] Jira rejected the email \+ API token/);
    expect(r.stdout).not.toContain("not-the-token");
  });

  test("tracker add reaches a loopback site only with the runner's test opt-ins", caseIds("TCLI-001"), async ({ fakeTracker }) => {
    test.skip(!exe, "no ./orkka with tracker support");
    // Without them a loopback site is normalized like any other: https, no port — unreachable.
    const plain = await run(["tracker", "add", "jira", "--site", fakeTracker.jiraSiteUrl, "--email", fakeTracker.jira.email], `${fakeTracker.jira.token}\n`, false);
    expect(plain.exitCode).not.toBe(0);
    expect(plain.stdout + plain.stderr).toContain("Checking the token against https://127.0.0.1 ...");
    expect(plain.stdout + plain.stderr).toMatch(/Nothing was saved/);
    expect(fakeTracker.requestsTo("jira")).toHaveLength(0);

    // With them: the site is kept as typed, the token is checked against the fake and lands in
    // the sandbox's file store — never the OS keychain.
    const jira = await run(["tracker", "add", "jira", "--site", fakeTracker.jiraSiteUrl, "--email", fakeTracker.jira.email], `${fakeTracker.jira.token}\n`);
    expect(jira.exitCode, jira.stdout + jira.stderr).toBe(0);
    const added = readTrackersFile(workspace, home).find((e) => e.kind === "jira" && e.siteUrl === fakeTracker.jiraSiteUrl && e.healthy);
    expect(added).toBeTruthy();
    seeded.push({ id: added!.id, kind: "jira", siteUrl: added!.siteUrl, workspace, home });
    expect(fs.readFileSync(path.join(home, "workspaces", workspace, "secrets", added!.id), "utf8").trim()).toBe(fakeTracker.jira.token);
    expect(jira.stdout + jira.stderr).not.toContain(fakeTracker.jira.token);

    const ado = await run(["tracker", "add", "ado", "--org", fakeTracker.adoSiteUrl], `${fakeTracker.ado.pat}\n`);
    expect(ado.exitCode, ado.stdout + ado.stderr).toBe(0);
    const addedAdo = readTrackersFile(workspace, home).find((e) => e.kind === "ado" && e.siteUrl === fakeTracker.adoSiteUrl && e.healthy);
    expect(addedAdo).toBeTruthy();
    seeded.push({ id: addedAdo!.id, kind: "ado", siteUrl: addedAdo!.siteUrl, workspace, home });
  });

  test("tracker remove deletes the entry and its token", caseIds("TCLI-009"), async ({ fakeTracker }) => {
    test.skip(!exe, "no ./orkka with tracker support");
    await seedTrackerConnection({ id: "jira-remove", kind: "jira", siteUrl: fakeTracker.jiraSiteUrl, email: fakeTracker.jira.email, secret: fakeTracker.jira.token, workspace, home });
    const r = await run(["tracker", "remove", "jira-remove"]);
    expect(r.exitCode, r.stdout + r.stderr).toBe(0);
    expect(r.stdout).toContain("Removed jira-remove and its token");
    expect(readTrackersFile(workspace, home).some((e) => e.id === "jira-remove")).toBe(false);
    const after = await run(["tracker", "test", "jira-remove"]);
    expect(after.exitCode).toBe(1);
    expect(after.stdout + after.stderr).toContain("No tracker connection 'jira-remove'");
  });
});
