/**
 * A developer's BASE FOLDER — what `POST /api/projects/from-folder` points at: one folder holding
 * several repositories.
 *
 *   <parent>/<name>/
 *     api/   a git checkout whose origin is a forge-looking alias
 *            (https://github.com/e2e-local/<name>-api.git) of a LOCAL BARE REMOTE — the stack's
 *            runner resolves it with the harness's git `insteadOf` rule and publishes with the stub gh
 *     web/   a git repo with NO origin — a local-folder repo Orkka squash-merges into
 *
 * Both are the dependency-free API fixture (node --test, no install), plus `src/math.js`, the file
 * the workflow's tiny tasks extend.
 */
import fs from "node:fs";
import path from "node:path";
import { execa } from "execa";
import { paths } from "../../config/index.js";
import { BareRemote } from "./bare-remote.js";
import { ensureDir } from "../util/fs.js";

const GIT_ID = ["-c", "user.name=orkka-e2e", "-c", "user.email=e2e@orkka.test", "-c", "commit.gpgsign=false"];
const MATH = `// Small arithmetic helpers.\nexport function sub(a, b) {\n  return a - b;\n}\n`;
const MATH_TEST = `import { test } from "node:test";\nimport assert from "node:assert/strict";\nimport { sub } from "../src/math.js";\n\ntest("sub", () => {\n  assert.equal(sub(5, 3), 2);\n});\n`;

export interface BaseFolder {
  root: string;
  api: { dir: string; remote: BareRemote };
  web: { dir: string };
}

function copyFixture(dst: string, packageName: string) {
  const src = path.join(paths.fixtures, "orkka-e2e-fixture-api");
  fs.cpSync(src, dst, { recursive: true, filter: (s) => !/node_modules/.test(s) });
  const pkgFile = path.join(dst, "package.json");
  const pkg = JSON.parse(fs.readFileSync(pkgFile, "utf8"));
  pkg.name = packageName;
  pkg.description = `The ${packageName} service.`;
  fs.writeFileSync(pkgFile, JSON.stringify(pkg, null, 2) + "\n");
  fs.writeFileSync(path.join(dst, "src", "math.js"), MATH);
  fs.writeFileSync(path.join(dst, "test", "math.test.js"), MATH_TEST);
  fs.writeFileSync(path.join(dst, "README.md"), `# ${packageName}\n\nTiny Node service of the e2e "${packageName}" shop. \`npm test\` runs the tests.\n`);
}

async function git(cwd: string, args: string[]) {
  return execa("git", [...GIT_ID, ...args], { cwd, env: { ...process.env, GIT_TERMINAL_PROMPT: "0" } });
}

export async function createBaseFolder(parent: string, name = "shop"): Promise<BaseFolder> {
  const root = path.join(parent, name);
  fs.rmSync(root, { recursive: true, force: true });
  ensureDir(root);

  // api/: seeded into a bare remote, checked out, origin = the forge-looking alias.
  const stage = path.join(parent, `.${name}-api-stage`);
  fs.rmSync(stage, { recursive: true, force: true });
  copyFixture(stage, `${name}-api`);
  const files: Record<string, string> = {};
  const walk = (d: string) => {
    for (const e of fs.readdirSync(d, { withFileTypes: true })) {
      const f = path.join(d, e.name);
      if (e.isDirectory()) walk(f);
      else files[path.relative(stage, f).replace(/\\/g, "/")] = fs.readFileSync(f, "utf8");
    }
  };
  walk(stage);
  fs.rmSync(stage, { recursive: true, force: true });
  const remote = await BareRemote.create(`${name}-api-${Date.now().toString(36)}`, { fixture: null, files });
  const apiDir = path.join(root, "api");
  await execa("git", ["clone", "-q", remote.dir, apiDir]);
  await git(apiDir, ["remote", "set-url", "origin", remote.aliasUrl]);

  // web/: a repository with commits and no origin.
  const webDir = path.join(root, "web");
  copyFixture(webDir, `${name}-web`);
  await git(webDir, ["init", "-q", "-b", "main"]);
  await git(webDir, ["add", "-A"]);
  await git(webDir, ["commit", "-q", "-m", "chore: initial web"]);

  return { root, api: { dir: apiDir, remote }, web: { dir: webDir } };
}
