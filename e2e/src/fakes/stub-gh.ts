/**
 * Installs the stub `gh` (fixtures/stub-gh/gh.mjs) into a bin directory to put first on a
 * process's PATH — the harness's sandboxed runner, or a spec that runs it directly. On Windows a
 * tiny gh.exe shim is compiled once with the .NET Framework csc that ships with Windows, because
 * the runner's Process.Start resolves only `.exe` files.
 */
import fs from "node:fs";
import path from "node:path";
import { execa } from "execa";
import { paths } from "../../config/index.js";
import { ensureDir } from "../util/fs.js";

export interface StubPullRequest {
  number: number;
  url: string;
  repo: string;
  head: string;
  base: string;
  title: string;
  body: string;
  state: "OPEN" | "MERGED" | "CLOSED";
  /** Opened with --draft and not yet marked ready (or turned back with ready --undo). */
  isDraft?: boolean;
  /** created | created-draft | ready | draft | edited, in order. */
  history?: string[];
  author: string;
  createdAt: string;
}

export interface StubGhState {
  signedIn: boolean;
  host: string;
  accounts: string[];
  activeAccount: string;
  nextNumber: number;
  prs: StubPullRequest[];
  failCreate: string | null;
}

export interface StubGhCall {
  at: string;
  argv: string[];
  cwd: string;
  ghToken: string | null;
  promptDisabled: boolean;
}

const CSC_CANDIDATES = [
  "C:\\Windows\\Microsoft.NET\\Framework64\\v4.0.30319\\csc.exe",
  "C:\\Windows\\Microsoft.NET\\Framework\\v4.0.30319\\csc.exe",
];

export class StubGh {
  private constructor(readonly binDir: string) {}

  get statePath() {
    return path.join(this.binDir, "state.json");
  }
  get callsPath() {
    return path.join(this.binDir, "calls.jsonl");
  }
  /** The executable the runner will find on PATH. */
  get executable() {
    return path.join(this.binDir, process.platform === "win32" ? "gh.exe" : "gh");
  }

  /** Copy the stub into `binDir` (fresh state) and build the platform launcher. */
  static async install(binDir: string, initial: Partial<StubGhState> = {}, opts: { keepState?: boolean } = {}): Promise<StubGh> {
    ensureDir(binDir);
    const script = path.join(binDir, "gh.mjs");
    fs.copyFileSync(path.join(paths.stubGhSource, "gh.mjs"), script);
    if (process.platform === "win32") {
      fs.writeFileSync(path.join(binDir, "gh.shim.txt"), `${process.execPath}\n${script}\n`);
      const exe = path.join(binDir, "gh.exe");
      if (!fs.existsSync(exe)) {
        const csc = CSC_CANDIDATES.find((c) => fs.existsSync(c));
        if (!csc) throw new Error("stub gh: no csc.exe (.NET Framework 4) found to build gh.exe — Windows ships one under C:\\Windows\\Microsoft.NET");
        await execa(csc, ["/nologo", "/target:exe", `/out:${exe}`, path.join(paths.stubGhSource, "GhShim.cs")]);
      }
    } else {
      const launcher = path.join(binDir, "gh");
      fs.writeFileSync(launcher, `#!/bin/sh\nexec "${process.execPath}" "${script}" "$@"\n`);
      fs.chmodSync(launcher, 0o755);
    }
    const stub = new StubGh(binDir);
    // A restarted stack keeps the forge it had: its pull requests must survive.
    if (!(opts.keepState && fs.existsSync(stub.statePath))) stub.reset(initial);
    return stub;
  }

  reset(initial: Partial<StubGhState> = {}) {
    const state: StubGhState = { signedIn: true, host: "github.com", accounts: ["e2e-dev"], activeAccount: "e2e-dev", nextNumber: 1, prs: [], failCreate: null, ...initial };
    fs.writeFileSync(this.statePath, JSON.stringify(state, null, 2));
    fs.rmSync(this.callsPath, { force: true });
  }

  state(): StubGhState {
    return JSON.parse(fs.readFileSync(this.statePath, "utf8")) as StubGhState;
  }

  update(mutate: (s: StubGhState) => void) {
    const s = this.state();
    mutate(s);
    fs.writeFileSync(this.statePath, JSON.stringify(s, null, 2));
  }

  calls(): StubGhCall[] {
    if (!fs.existsSync(this.callsPath)) return [];
    return fs
      .readFileSync(this.callsPath, "utf8")
      .split(/\r?\n/)
      .filter(Boolean)
      .map((l) => JSON.parse(l) as StubGhCall);
  }

  /** Calls whose argv starts with the given words, e.g. `callsOf("pr", "create")`. */
  callsOf(...words: string[]): StubGhCall[] {
    return this.calls().filter((c) => words.every((w, i) => c.argv[i] === w));
  }

  /** What the developer's team does on the forge: merge the published PR. */
  merge(url: string) {
    this.update((s) => {
      const pr = s.prs.find((p) => p.url === url);
      if (!pr) throw new Error(`stub gh: no PR ${url}`);
      pr.state = "MERGED";
    });
  }

  /** PATH with this stub first, for a child process environment. */
  pathWithStub(base = process.env.PATH ?? process.env.Path ?? ""): string {
    return `${this.binDir}${path.delimiter}${base}`;
  }
}
