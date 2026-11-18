/**
 * Local bare git remotes standing in for a developer's forge repository. Each lives under
 * `<scratch>/remotes/<name>.git`, seeded from a fixture in fixtures/repos, and is registered with
 * Orkka under a FORGE-LOOKING alias (`https://github.com/e2e-local/<name>.git`) so the repo
 * classifies as GitHub and publishing goes through the (stub) `gh`. The runner the harness starts
 * gets one git `insteadOf` rule (GIT_CONFIG_* env) that rewrites the alias prefix to the local
 * folder, so every clone/fetch/push really happens — on this machine, offline.
 */
import fs from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";
import { execa } from "execa";
import { env, paths } from "../../config/index.js";
import { ensureDir } from "../util/fs.js";

export const ALIAS_HOST_PREFIX = "https://github.com/e2e-local/";
const GIT_ID = ["-c", "user.name=orkka-e2e", "-c", "user.email=e2e@orkka.test", "-c", "commit.gpgsign=false"];
const SKIP = new Set(["node_modules", ".next", ".git", "coverage"]);

export function remotesDir() {
  return path.join(env.E2E_SCRATCH_DIR, "remotes");
}

/** file:// URL of the remotes folder, with a trailing slash (git's insteadOf is a prefix rewrite). */
export function remotesFileUrlPrefix() {
  return pathToFileURL(remotesDir()).href.replace(/\/?$/, "/");
}

/**
 * Env for any git process (the runner) that must resolve the alias URLs to the local remotes.
 * Uses GIT_CONFIG_COUNT pairs — the runner's own GitProcess appends after existing pairs.
 */
export function aliasGitEnv(): Record<string, string> {
  return {
    GIT_CONFIG_COUNT: "2",
    GIT_CONFIG_KEY_0: `url.${remotesFileUrlPrefix()}.insteadOf`,
    GIT_CONFIG_VALUE_0: ALIAS_HOST_PREFIX,
    // Pushes to a non-bare checkout are never used here; file transport is allowed explicitly.
    GIT_CONFIG_KEY_1: "protocol.file.allow",
    GIT_CONFIG_VALUE_1: "always",
  };
}

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

async function git(args: string[], cwd?: string) {
  return execa("git", args, { cwd, env: { ...process.env, GIT_TERMINAL_PROMPT: "0" } });
}

export class BareRemote {
  constructor(
    readonly name: string,
    readonly dir: string,
    readonly defaultBranch: string,
  ) {}

  /** The URL Orkka stores (classifies as GitHub; `gh --repo e2e-local/<name>`). */
  get aliasUrl() {
    return `${ALIAS_HOST_PREFIX}${this.name}.git`;
  }
  /** `owner/name` as `gh --repo` receives it. */
  get slug() {
    return `e2e-local/${this.name}`;
  }
  get fileUrl() {
    return pathToFileURL(this.dir).href;
  }

  /**
   * A bare repo seeded with a fixture's content as one commit on `defaultBranch`. Names must be
   * unique per run; an existing directory is replaced.
   */
  static async create(name: string, opts: { fixture?: "web" | "api" | null; defaultBranch?: string; files?: Record<string, string>; empty?: boolean } = {}): Promise<BareRemote> {
    if (!/^[a-z0-9][a-z0-9._-]*$/i.test(name)) throw new Error(`bare remote name ${name} must be a simple slug`);
    const branch = opts.defaultBranch ?? "main";
    const dir = path.join(remotesDir(), `${name}.git`);
    fs.rmSync(dir, { recursive: true, force: true });
    ensureDir(remotesDir());
    await git(["init", "--bare", "-q", "-b", branch, dir]);
    // An EMPTY remote, as a developer creates one on their forge before connecting a folder to it.
    if (opts.empty) return new BareRemote(name, dir, branch);

    const work = path.join(env.E2E_SCRATCH_DIR, "seed", name);
    fs.rmSync(work, { recursive: true, force: true });
    ensureDir(work);
    const fixture = opts.fixture === undefined ? "web" : opts.fixture;
    if (fixture) copyDir(path.join(paths.fixtures, fixture === "web" ? "orkka-e2e-fixture-web" : "orkka-e2e-fixture-api"), work);
    for (const [rel, content] of Object.entries(opts.files ?? {})) {
      ensureDir(path.dirname(path.join(work, rel)));
      fs.writeFileSync(path.join(work, rel), content);
    }
    if (fs.readdirSync(work).length === 0) fs.writeFileSync(path.join(work, "README.md"), `# ${name}\n`);
    await git(["init", "-q", "-b", branch], work);
    await git([...GIT_ID, "add", "-A"], work);
    await git([...GIT_ID, "commit", "-q", "-m", "chore: e2e fixture baseline"], work);
    await git(["push", "-q", dir, `${branch}:${branch}`], work);
    fs.rmSync(work, { recursive: true, force: true });
    return new BareRemote(name, dir, branch);
  }

  async branches(): Promise<string[]> {
    const { stdout } = await git(["--git-dir", this.dir, "for-each-ref", "--format=%(refname:short)", "refs/heads"]);
    return stdout.split(/\r?\n/).filter(Boolean);
  }

  async head(branch = this.defaultBranch): Promise<string | undefined> {
    const r = await execa("git", ["--git-dir", this.dir, "rev-parse", "--verify", "-q", `refs/heads/${branch}`], { reject: false });
    return r.exitCode === 0 ? r.stdout.trim() : undefined;
  }

  async readFile(rel: string, branch = this.defaultBranch): Promise<string | undefined> {
    const r = await execa("git", ["--git-dir", this.dir, "show", `${branch}:${rel.replace(/\\/g, "/")}`], { reject: false });
    return r.exitCode === 0 ? r.stdout : undefined;
  }

  async log(branch = this.defaultBranch, n = 5): Promise<string> {
    const r = await execa("git", ["--git-dir", this.dir, "log", `-${n}`, "--oneline", branch], { reject: false });
    return r.stdout;
  }

  /** Commit a change straight to a branch, as a teammate pushing to the forge would. */
  async commitFile(rel: string, content: string, message: string, branch = this.defaultBranch) {
    const work = path.join(env.E2E_SCRATCH_DIR, "edit", `${this.name}-${Date.now()}`);
    await git(["clone", "-q", "-b", branch, this.dir, work]);
    ensureDir(path.dirname(path.join(work, rel)));
    fs.writeFileSync(path.join(work, rel), content);
    await git([...GIT_ID, "add", "-A"], work);
    await git([...GIT_ID, "commit", "-q", "-m", message], work);
    await git(["push", "-q", "origin", branch], work);
    fs.rmSync(work, { recursive: true, force: true });
  }

  /** What the team does after reviewing the published PR: merge `head` into `base` on the remote. */
  async mergeBranch(head: string, base = this.defaultBranch, message = `Merge ${head}`) {
    const work = path.join(env.E2E_SCRATCH_DIR, "merge", `${this.name}-${Date.now()}`);
    await git(["clone", "-q", "-b", base, this.dir, work]);
    await git(["fetch", "-q", "origin", `${head}:refs/remotes/origin/${head}`], work);
    await git([...GIT_ID, "merge", "--no-ff", "-q", "-m", message, `origin/${head}`], work);
    await git(["push", "-q", "origin", base], work);
    fs.rmSync(work, { recursive: true, force: true });
  }

  dispose() {
    fs.rmSync(this.dir, { recursive: true, force: true });
  }
}
