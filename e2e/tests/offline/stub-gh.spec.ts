import fs from "node:fs";
import path from "node:path";
import { spawn } from "node:child_process";
import { test, expect } from "../../src/fixtures/test.js";
import type { StubGh } from "../../src/fakes/stub-gh.js";

/**
 * The stub `gh` answers the exact command lines the runner's ForgeCli runs
 * (orkka-api/src/Orkka.LocalRunner/ForgeCli.cs) and prints what ForgeCli parses. It is started
 * the way the runner starts it — by bare name, no shell, PATH lookup (on Windows that only finds
 * an .exe, hence the compiled gh.exe shim). @offline
 */
function gh(stub: StubGh, args: string[], extraEnv: Record<string, string> = {}): Promise<{ code: number; stdout: string; stderr: string }> {
  return new Promise((resolve, reject) => {
    const pathKey = Object.keys(process.env).find((k) => k.toLowerCase() === "path") ?? "PATH";
    const child = spawn("gh", args, {
      env: { ...process.env, [pathKey]: stub.pathWithStub(process.env[pathKey]), GH_PROMPT_DISABLED: "1", ...extraEnv },
      stdio: ["pipe", "pipe", "pipe"],
      windowsHide: true,
    });
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (d) => (stdout += d));
    child.stderr.on("data", (d) => (stderr += d));
    child.on("error", reject);
    child.on("close", (code) => resolve({ code: code ?? -1, stdout, stderr }));
    child.stdin.end();
  });
}

/** ForgeCli.FirstUrl: the first stdout line starting with https://. */
const firstUrl = (out: string) => out.split("\n").map((l) => l.trim()).find((l) => l.toLowerCase().startsWith("https://"));

test.describe("stub gh @offline", () => {
  test("auth status reports the signed-in accounts the way ForgeCli.SignedInAccounts parses them", async ({ stubGh }) => {
    const r = await gh(stubGh, ["auth", "status"]);
    expect(r.code).toBe(0);
    const accounts = [...(r.stdout + "\n" + r.stderr).matchAll(/Logged in to (\S+) (?:account|as) (\S+)/g)].map((m) => `${m[2]}@${m[1]}`);
    expect(accounts).toEqual(["e2e-dev@github.com"]);
    const host = await gh(stubGh, ["auth", "status", "--hostname", "github.com"]);
    expect(host.code).toBe(0);
    stubGh.update((s) => (s.signedIn = false));
    expect((await gh(stubGh, ["auth", "status", "--hostname", "github.com"])).code).not.toBe(0);
  });

  test("publish: pr list finds nothing → pr create prints the URL → a retry finds the open PR (idempotent)", async ({ stubGh }) => {
    const list = ["pr", "list", "--repo", "e2e-local/app", "--head", "orkka/E2E-1", "--state", "open", "--json", "url", "--jq", ".[0].url"];
    const none = await gh(stubGh, list);
    expect(none.code).toBe(0);
    expect(firstUrl(none.stdout)).toBeUndefined();

    const bodyFile = path.join(stubGh.binDir, "body.md");
    fs.writeFileSync(bodyFile, "Line 1\n\n\"quoted\" & <html> — ünïcode");
    const create = await gh(stubGh, ["pr", "create", "--repo", "e2e-local/app", "--head", "orkka/E2E-1", "--base", "main", "--title", 'Add "coupon" field', "--body-file", bodyFile]);
    expect(create.code, create.stderr).toBe(0);
    expect(firstUrl(create.stdout)).toBe("https://github.com/e2e-local/app/pull/1");
    const pr = stubGh.state().prs[0];
    expect(pr).toMatchObject({ repo: "e2e-local/app", head: "orkka/E2E-1", base: "main", title: 'Add "coupon" field', state: "OPEN" });
    expect(pr.body).toBe("Line 1\n\n\"quoted\" & <html> — ünïcode");

    const again = await gh(stubGh, list);
    expect(firstUrl(again.stdout)).toBe("https://github.com/e2e-local/app/pull/1");
    const dup = await gh(stubGh, ["pr", "create", "--repo", "e2e-local/app", "--head", "orkka/E2E-1", "--base", "main", "--title", "x", "--body-file", bodyFile]);
    expect(dup.code).not.toBe(0);
    expect(stubGh.callsOf("pr", "create")).toHaveLength(2);
  });

  test("state: pr view --json state --jq .state follows OPEN → MERGED (ForgeCli.NormalizeState → merged)", async ({ stubGh }) => {
    const bodyFile = path.join(stubGh.binDir, "body.md");
    fs.writeFileSync(bodyFile, "b");
    const url = firstUrl((await gh(stubGh, ["pr", "create", "--repo", "e2e-local/app", "--head", "h", "--base", "main", "--title", "t", "--body-file", bodyFile])).stdout)!;
    const view = () => gh(stubGh, ["pr", "view", url, "--json", "state", "--jq", ".state"]);
    expect((await view()).stdout.trim()).toBe("OPEN");
    stubGh.merge(url);
    expect((await view()).stdout.trim()).toBe("MERGED");
    expect((await gh(stubGh, ["pr", "view", "https://github.com/e2e-local/app/pull/999", "--json", "state", "--jq", ".state"])).code).not.toBe(0);
  });

  test("a Story's pull request: create --draft, edit the checklist, ready, ready --undo (ForgeCli draft/ready/edit)", async ({ stubGh }) => {
    const bodyFile = path.join(stubGh.binDir, "body.md");
    fs.writeFileSync(bodyFile, "- [x] SHOP-21\n- [ ] SHOP-22");
    const create = await gh(stubGh, ["pr", "create", "--repo", "e2e-local/app", "--head", "feature/SHOP-20-story", "--base", "main", "--title", "SHOP-20", "--body-file", bodyFile, "--draft"]);
    expect(create.code, create.stderr).toBe(0);
    const url = firstUrl(create.stdout)!;
    expect(stubGh.state().prs[0].isDraft).toBe(true);
    fs.writeFileSync(bodyFile, "- [x] SHOP-21\n- [x] SHOP-22");
    expect((await gh(stubGh, ["pr", "edit", url, "--repo", "e2e-local/app", "--body-file", bodyFile])).code).toBe(0);
    expect(stubGh.state().prs[0].body).toContain("- [x] SHOP-22");
    expect((await gh(stubGh, ["pr", "ready", url, "--repo", "e2e-local/app"])).code).toBe(0);
    expect(stubGh.state().prs[0].isDraft).toBe(false);
    expect((await gh(stubGh, ["pr", "ready", url, "--repo", "e2e-local/app", "--undo"])).code).toBe(0);
    expect(stubGh.state().prs[0].isDraft).toBe(true);
    expect(stubGh.state().prs[0].history).toEqual(["created-draft", "edited", "ready", "draft"]);
    expect((await gh(stubGh, ["pr", "view", url, "--json", "isDraft", "--jq", ".isDraft"])).stdout.trim()).toBe("true");
    expect((await gh(stubGh, ["pr", "ready", url, "--repo", "e2e-local/other"])).code).not.toBe(0);
  });

  test("account scoping: auth token --user prints that account's token; GH_TOKEN reaches the call record", async ({ stubGh }) => {
    stubGh.update((s) => (s.accounts = ["personal", "work-acct"]));
    const token = await gh(stubGh, ["auth", "token", "--hostname", "github.com", "--user", "work-acct"]);
    expect(token.stdout.trim()).toBe("gho_stub_work-acct");
    expect((await gh(stubGh, ["auth", "token", "--hostname", "github.com", "--user", "nobody"])).code).not.toBe(0);
    const bodyFile = path.join(stubGh.binDir, "body.md");
    fs.writeFileSync(bodyFile, "b");
    await gh(stubGh, ["pr", "create", "--repo", "e2e-local/app", "--head", "h2", "--base", "main", "--title", "t", "--body-file", bodyFile], { GH_TOKEN: token.stdout.trim() });
    expect(stubGh.state().prs[0].author).toBe("work-acct");
    expect(stubGh.callsOf("pr", "create")[0].ghToken).toBe("gho_stub_work-acct");
  });

  test("a configured failure and a signed-out CLI fail like gh does", async ({ stubGh }) => {
    stubGh.update((s) => (s.failCreate = "GraphQL: Resource not accessible by integration (createPullRequest)"));
    const bodyFile = path.join(stubGh.binDir, "body.md");
    fs.writeFileSync(bodyFile, "b");
    const r = await gh(stubGh, ["pr", "create", "--repo", "e2e-local/app", "--head", "h", "--base", "main", "--title", "t", "--body-file", bodyFile]);
    expect(r.code).toBe(1);
    expect(r.stderr).toMatch(/Resource not accessible/);
    stubGh.update((s) => (s.signedIn = false));
    expect((await gh(stubGh, ["pr", "list", "--repo", "e2e-local/app", "--head", "h", "--state", "open", "--json", "url", "--jq", ".[0].url"])).code).toBe(4);
  });
});
