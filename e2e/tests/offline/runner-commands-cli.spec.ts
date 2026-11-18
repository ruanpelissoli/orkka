import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execa } from "execa";
import { test, expect, caseIds } from "../../src/fixtures/test.js";
import { env } from "../../config/index.js";
import { orkkaDllIfBuilt, RUNNER_TEST_OPT_INS } from "../../src/runner/process.js";
import { parseCommandListing, seedPendingCommand } from "../../src/runner/commands.js";

/**
 * The REAL runner's `./orkka commands` CLI (command trust) in a throwaway ORKKA_HOME. With no
 * runner daemon to refuse a command, the pending entry is seeded in the runner's own file format
 * (src/runner/commands.ts) — everything after that is the CLI: list, the approval question,
 * --yes, revoke. Needs an `./orkka` that has `commands` (a current build of
 * orkka-api/src/Orkka.LocalRunner); skips otherwise. No Orkka deployment needed. @offline
 */
const BUILT = env.E2E_ORKKA_DLL;

test.describe("./orkka commands (command trust) @offline", () => {
  test.describe.configure({ mode: "serial" });
  let exe = "";
  let home = "";

  test.beforeAll(async () => {
    exe = orkkaDllIfBuilt() ?? "";
    home = fs.mkdtempSync(path.join(os.tmpdir(), "orkka-e2e-cmd-home-"));
  });

  test.afterAll(() => {
    if (home) fs.rmSync(home, { recursive: true, force: true });
  });

  const run = (args: string[], input = "") =>
    execa("dotnet", [exe, ...args], {
      env: { ...process.env, ORKKA_HOME: home, ORKKA_NO_UPDATE_CHECK: "1", ...RUNNER_TEST_OPT_INS },
      input, reject: false, timeout: 60_000, cwd: os.homedir(),
    });

  test("list, approve (asked, then --yes) and revoke a refused repository command", caseIds("RUN-034"), async () => {
    test.skip(!exe, `no ./orkka with the commands verb (no build at ${BUILT})`);

    const empty = await run(["commands"]);
    expect(empty.exitCode, empty.stdout + empty.stderr).toBe(0);
    expect(empty.stdout).toContain("No repository commands have been asked for or approved on this machine.");
    expect(empty.stdout).toContain("Commands in a repository's orkka.json on its default branch run without approval.");

    const unknown = await run(["commands", "approve", "deadbeef", "--yes"]);
    expect(unknown.exitCode).toBe(1);
    expect(unknown.stderr).toContain("No pending command with id deadbeef");

    const id = "0badc0de";
    const command = "npm run e2e-not-declared";
    seedPendingCommand(home, { id, repo: "https://github.com/e2e-local/e2e-web.git", command, kind: "check", workspace: "e2e" });

    const listed = await run(["commands"]);
    expect(listed.exitCode).toBe(0);
    expect(parseCommandListing(listed.stdout)).toEqual({ pending: [id], approved: [] });
    expect(listed.stdout).toContain(`$ ${command}`);
    expect(listed.stdout).toContain("Approve one with `./orkka commands approve <id>`.");

    // Without --yes the CLI shows the command in full and asks; anything but y/yes approves nothing.
    const declined = await run(["commands", "approve", id], "n\n");
    expect(declined.exitCode).toBe(1);
    expect(declined.stdout).toContain(`Command:`);
    expect(declined.stdout).toContain(`$ ${command}`);
    expect(declined.stdout).toContain("Not approved.");
    expect(parseCommandListing((await run(["commands"])).stdout).pending).toEqual([id]);

    const approved = await run(["commands", "approve", "--yes", id]);
    expect(approved.exitCode, approved.stdout + approved.stderr).toBe(0);
    expect(approved.stdout).toContain(`Approved ${id}.`);
    expect(parseCommandListing((await run(["commands"])).stdout)).toEqual({ pending: [], approved: [id] });

    const again = await run(["commands", "approve", "--yes", id]);
    expect(again.exitCode).toBe(1);
    expect(again.stderr).toContain(`${id} is already approved.`);

    const revoked = await run(["commands", "revoke", id]);
    expect(revoked.exitCode).toBe(0);
    expect(revoked.stdout).toContain(`Forgot ${id}.`);
    expect(parseCommandListing((await run(["commands"])).stdout)).toEqual({ pending: [], approved: [] });

    const usage = await run(["commands", "frobnicate"]);
    expect(usage.exitCode).toBe(1);
    expect(usage.stdout).toContain("./orkka commands approve <id>");
  });
});
