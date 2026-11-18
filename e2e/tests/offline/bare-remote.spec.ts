import fs from "node:fs";
import path from "node:path";
import { execa } from "execa";
import { test, expect } from "../../src/fixtures/test.js";
import { env } from "../../config/index.js";
import { aliasGitEnv } from "../../src/testdata/bare-remote.js";
import { fixtureRepos } from "../../src/testdata/fixture-repos.js";

/**
 * Local bare remotes behind a forge-looking alias: a git configured like the sandboxed runner
 * (GIT_CONFIG_* insteadOf) clones `https://github.com/e2e-local/<name>.git`, pushes a task
 * branch, and the "team" merges it on the remote. @offline
 */
test.describe("bare remote fixture @offline", () => {
  test("seeded from the web fixture; the alias URL clones, a branch pushes, a merge lands on main", async ({ bareRemotes }) => {
    const remote = await bareRemotes.create("e2e-web", { fixture: "web" });
    expect(remote.aliasUrl).toBe(`https://github.com/e2e-local/${remote.name}.git`);
    expect(remote.slug).toBe(`e2e-local/${remote.name}`);
    expect(await remote.branches()).toEqual(["main"]);
    expect(await remote.readFile(fixtureRepos.web.targetFile)).toContain(fixtureRepos.web.featureListMarker);

    const work = path.join(env.E2E_SCRATCH_DIR, "clone-test", remote.name);
    fs.rmSync(work, { recursive: true, force: true });
    const gitEnv = { ...process.env, ...aliasGitEnv(), GIT_TERMINAL_PROMPT: "0" };
    const id = ["-c", "user.name=t", "-c", "user.email=t@e2e.test"];
    await execa("git", ["clone", "-q", remote.aliasUrl, work], { env: gitEnv });
    // The stored origin is the alias (what Orkka shows); `git remote get-url` would print it rewritten.
    const origin = (await execa("git", ["config", "--get", "remote.origin.url"], { cwd: work })).stdout.trim();
    expect(origin, "origin keeps the alias; only transport is rewritten").toBe(remote.aliasUrl);
    await execa("git", ["checkout", "-q", "-b", "orkka/E2E-1"], { cwd: work });
    fs.appendFileSync(path.join(work, fixtureRepos.web.targetFile), "\n// e2e-token\n");
    await execa("git", [...id, "commit", "-q", "-am", "feat: e2e"], { cwd: work });
    await execa("git", ["push", "-q", "origin", "orkka/E2E-1"], { cwd: work, env: gitEnv });
    expect((await remote.branches()).sort()).toEqual(["main", "orkka/E2E-1"]);
    expect(await remote.readFile(fixtureRepos.web.targetFile)).not.toContain("e2e-token");

    await remote.mergeBranch("orkka/E2E-1");
    expect(await remote.readFile(fixtureRepos.web.targetFile)).toContain("e2e-token");
    expect(await remote.log()).toMatch(/Merge orkka\/E2E-1/);
    fs.rmSync(work, { recursive: true, force: true });
  });

  test("custom files and default branch; a teammate's commit moves the head", async ({ bareRemotes }) => {
    const remote = await bareRemotes.create("e2e-mono", { fixture: null, defaultBranch: "develop", files: { "packages/api/README.md": "# api\n", "packages/web/README.md": "# web\n" } });
    expect(await remote.branches()).toEqual(["develop"]);
    const before = await remote.head();
    await remote.commitFile("packages/api/NOTES.md", "note\n", "docs: teammate note");
    expect(await remote.head()).not.toBe(before);
    expect(await remote.readFile("packages/api/NOTES.md")).toBe("note");
  });
});
