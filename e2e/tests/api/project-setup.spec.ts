import { test, expect, caseIds } from "../../src/fixtures/test.js";
import { ApiError } from "../../src/api/http.js";
import { needsEnvironment } from "../../config/index.js";

/**
 * The guided project setup's endpoints on the sandbox, without a tracker: a new project starts
 * with its setup open, steps are confirmed and taken back, Start is idempotent, the command check
 * answers for repositories without commands, and the dry run needs a saved ticket source. The dry
 * run against the fake tracker and the Start gate itself are workflow step 7 (PSU-009/010).
 */
async function status(p: Promise<unknown>): Promise<number> {
  const err = await p.then(() => undefined).catch((e) => e as ApiError);
  expect(err, "expected an error").toBeInstanceOf(ApiError);
  return err!.status;
}

test.describe("project setup @api", () => {
  test.skip(Boolean(needsEnvironment()), needsEnvironment() ?? "");

  test("PSU-002 a new project starts with its setup open", caseIds("PSU-002"), async ({ api, projects }) => {
    const p = await projects.create("psu");
    const read = await api.projects.get(p.id);
    expect(read.setupCompletedAt).toBeNull();
    expect(read.setupSteps).toEqual([]);
  });

  test("PSU-003 steps are confirmed and taken back; an unknown step is refused", caseIds("PSU-003"), async ({ api, projects }) => {
    const p = await projects.create("psu");
    await api.projects.setup.step(p.id, "source");
    expect((await api.projects.setup.step(p.slug, "folder")).setupSteps).toEqual(["folder", "source"]);
    expect((await api.projects.setup.step(p.id, "source", false)).setupSteps).toEqual(["folder"]);
    expect(await status(api.projects.setup.step(p.id, "review"))).toBe(400);
    expect(await status(api.projects.setup.step(p.id, "deploy"))).toBe(400);
  });

  test("PSU-011 Start records the start once and confirms every step", caseIds("PSU-011"), async ({ api, projects }) => {
    const p = await projects.create("psu");
    const first = await api.projects.setup.start(p.id, ["Claude is not signed in"]);
    expect(first.setupCompletedAt).toBeTruthy();
    expect(first.setupSteps).toEqual(["folder", "machine", "source", "team"]);
    const again = await api.projects.setup.start(p.id);
    expect(again.setupCompletedAt).toBe(first.setupCompletedAt);
  });

  test("PSU-006 the command check: nothing to vet without repository commands", caseIds("PSU-006"), async ({ api, projects }) => {
    const p = await projects.create("psu");
    await api.repos.add(p.id, { name: "app", gitUrl: "https://github.com/e2e-local/app.git", defaultBranch: "main" });
    const check = await api.projects.setup.commands(p.id);
    expect(check.supported).toBe(true);
    expect(check.repos).toEqual([expect.objectContaining({ name: "app", commands: [] })]);
  });

  test("PSU-014 the dry run needs a saved ticket source", caseIds("PSU-014"), async ({ api, projects }) => {
    const p = await projects.create("psu");
    expect(await status(api.projects.tracker.preview(p.id))).toBe(404);
  });
});
