import fs from "node:fs";
import path from "node:path";
import { execa } from "execa";
import { env, paths } from "../../config/index.js";
import { ensureDir } from "../util/fs.js";
import { log } from "../util/log.js";

const SKIP = new Set(["node_modules", ".next", ".git", "coverage"]);

function copyDir(src: string, dst: string) {
  ensureDir(dst);
  for (const entry of fs.readdirSync(src, { withFileTypes: true })) {
    if (SKIP.has(entry.name)) continue;
    const s = path.join(src, entry.name);
    const d = path.join(dst, entry.name);
    if (entry.isDirectory()) copyDir(s, d);
    else fs.copyFileSync(s, d);
  }
}

/**
 * Materialise a fresh copy of a fixture repo as a plain git repository on this PC (the runner's
 * machine). Returns the absolute Windows path to hand to `POST /api/projects/{id}/repos/local`.
 */
export async function materializeLocalFixture(name: string, fixture: "web" | "api" = "web", opts: { gitInit?: boolean } = {}): Promise<string> {
  const src = path.join(paths.fixtures, fixture === "web" ? "orkka-e2e-fixture-web" : "orkka-e2e-fixture-api");
  if (!fs.existsSync(src)) throw new Error(`fixture source missing: ${src}`);
  const dst = path.join(env.E2E_LOCAL_FIXTURE_PARENT, name);
  fs.rmSync(dst, { recursive: true, force: true });
  copyDir(src, dst);
  if (opts.gitInit ?? true) {
    await execa("git", ["init", "-b", "main"], { cwd: dst });
    await execa("git", ["-c", "user.name=orkka-e2e", "-c", "user.email=e2e@orkka.test", "add", "-A"], { cwd: dst });
    await execa("git", ["-c", "user.name=orkka-e2e", "-c", "user.email=e2e@orkka.test", "commit", "-q", "-m", "chore: e2e fixture baseline"], { cwd: dst });
  }
  log.info(`Local fixture ready at ${dst}`);
  return dst;
}

export async function disposeLocalFixture(dir: string, attempts = 6): Promise<boolean> {
  for (let i = 0; i < attempts; i++) {
    try {
      fs.rmSync(dir, { recursive: true, force: true });
      const sibling = `${dir}-worktrees`;
      if (fs.existsSync(sibling)) fs.rmSync(sibling, { recursive: true, force: true });
      return true;
    } catch (e) {
      await new Promise((r) => setTimeout(r, 10_000));
      if (i === attempts - 1) log.warn(`Could not remove ${dir}: ${(e as Error).message}`);
    }
  }
  return false;
}

export function readLocalFile(dir: string, rel: string): string | undefined {
  try {
    return fs.readFileSync(path.join(dir, rel), "utf8");
  } catch {
    return undefined;
  }
}

export async function gitStatusShort(dir: string): Promise<string> {
  const { stdout } = await execa("git", ["status", "--short", "--branch"], { cwd: dir });
  return stdout;
}

export async function gitLogOneline(dir: string, n = 5): Promise<string> {
  const { stdout } = await execa("git", ["log", `-${n}`, "--oneline"], { cwd: dir, reject: false });
  return stdout;
}
