import fs from "node:fs";
import path from "node:path";
import type { FullConfig, FullResult, Reporter, Suite, TestCase, TestResult } from "@playwright/test/reporter";
import { cfg, env, paths } from "../../config/index.js";
import { readJson, writeJson, stamp, ensureDir } from "../util/fs.js";
import { renderMarkdown } from "./render-md.js";
import { appendIndexRow } from "./index-writer.js";
import { readCreatedProjects } from "../safety/sweep.js";

export interface CaseResult {
  caseIds: string[];
  testTitle: string;
  file: string;
  status: "passed" | "failed" | "timedOut" | "skipped" | "interrupted";
  durationMs: number;
  costUsd?: number;
  projects: string[];
  runner?: string;
  skipReason?: string;
  artifacts: { trace?: string; screenshots: string[] };
  failure?: { message: string; stackHead: string; logHint: string };
  startedAt: string;
}

export interface RunReport {
  runId: string;
  suite: string;
  target: string;
  startedAt: string;
  finishedAt: string;
  durationMs: number;
  operator: string;
  gitSha: string;
  env: { node: string; playwright: string };
  totals: { passed: number; failed: number; skipped: number; timedOut: number; interrupted: number };
  cases: CaseResult[];
  cleanup: { createdProjects: { id: string; name: string }[]; leftovers: string[] };
  notes: string[];
}

/** Writes runs/<stamp>-<suite>/report.{json,md} and appends a row to runs/INDEX.md. */
export default class RunReporter implements Reporter {
  private startedAt = new Date();
  private cases: CaseResult[] = [];
  private suite = env.E2E_SUITE ?? "adhoc";
  private runDir = "";
  private pwVersion = "";

  onBegin(config: FullConfig, _suite: Suite) {
    try {
      this.beginImpl(config);
    } catch (e) {
      console.error("[run-reporter] onBegin failed:", e);
    }
  }

  private beginImpl(config: FullConfig) {
    this.pwVersion = config.version;
    if (!env.E2E_SUITE && typeof config.grep === "object" && !Array.isArray(config.grep)) {
      const m = String((config.grep as RegExp).source).match(/@([a-z-]+)/);
      if (m) this.suite = m[1];
    }
    this.runDir = path.join(paths.runsDir, `${stamp(this.startedAt)}-${this.suite}`);
    ensureDir(this.runDir);
    ensureDir(paths.current);
    fs.writeFileSync(path.join(paths.current, "run-dir.txt"), this.runDir);
  }

  onTestEnd(test: TestCase, result: TestResult) {
    const ann = (type: string) => [...new Set(test.annotations.concat(result.annotations ?? []).filter((a) => a.type === type).map((a) => a.description ?? ""))];
    const caseIdsList = [...new Set(ann("case"))];
    const cost = ann("cost-usd")[0];
    const failure = result.error
      ? {
          message: (result.error.message ?? "").split("\n").slice(0, 12).join("\n"),
          stackHead: (result.error.stack ?? "").split("\n").slice(0, 8).join("\n"),
          logHint: this.logHint(ann("project"), ann("task")),
        }
      : undefined;
    const screenshots = result.attachments.filter((a) => a.contentType.startsWith("image/") && a.path).map((a) => path.relative(this.runDir, a.path!));
    const trace = result.attachments.find((a) => a.name === "trace" && a.path)?.path;
    this.cases.push({
      caseIds: caseIdsList,
      testTitle: test.title,
      file: path.relative(paths.root, test.location.file).replace(/\\/g, "/"),
      status: result.status,
      durationMs: result.duration,
      costUsd: cost ? Number(cost) : undefined,
      projects: ann("project"),
      runner: ann("runner")[0],
      skipReason: result.status === "skipped" ? ann("skip")[0] : undefined,
      artifacts: { trace: trace ? path.relative(this.runDir, trace) : undefined, screenshots },
      failure,
      startedAt: result.startTime.toISOString(),
    });
  }

  /** Where to look next: the API/Worker console of the local stack and the sandboxed runner's log. */
  private logHint(projects: string[], tasks: string[]): string {
    const ids = [...projects.map((p) => p.split(" ")[0]), ...tasks].filter(Boolean);
    const grep = ids.length ? ` (grep for ${ids.join(", ")})` : "";
    return `API + Worker console output of the stack at ${cfg.apiUrl}; runner log .runs/runner.log${grep}`;
  }

  async onEnd(result: FullResult) {
    try {
      await this.endImpl(result);
    } catch (e) {
      console.error("[run-reporter] onEnd failed:", e);
    }
  }

  private async endImpl(result: FullResult) {
    const finishedAt = new Date();
    const created = readCreatedProjects();
    const totals = { passed: 0, failed: 0, skipped: 0, timedOut: 0, interrupted: 0 };
    for (const c of this.cases) totals[c.status]++;
    const report: RunReport = {
      runId: path.basename(this.runDir),
      suite: this.suite,
      target: cfg.configured ? cfg.apiUrl : "offline",
      startedAt: this.startedAt.toISOString(),
      finishedAt: finishedAt.toISOString(),
      durationMs: finishedAt.getTime() - this.startedAt.getTime(),
      operator: "claude",
      gitSha: readGitSha(),
      env: { node: process.version, playwright: this.pwVersion },
      totals,
      cases: this.cases,
      cleanup: {
        createdProjects: created.map((c) => ({ id: c.id, name: c.name })),
        leftovers: readJson<string[]>(path.join(paths.current, "leftovers.json")) ?? [],
      },
      notes: readJson<string[]>(path.join(paths.current, "notes.json")) ?? [],
    };
    if (result.status !== "passed") report.notes.unshift(`Playwright overall status: ${result.status}`);
    writeJson(path.join(this.runDir, "report.json"), report);
    fs.writeFileSync(path.join(this.runDir, "report.md"), renderMarkdown(report), "utf8");
    appendIndexRow(report);
    console.log(`\nRun report: ${path.join(this.runDir, "report.md")}`);
  }

  printsToStdio() {
    return false;
  }
}

function readGitSha(): string {
  try {
    const head = fs.readFileSync(path.join(paths.root, ".git", "HEAD"), "utf8").trim();
    if (head.startsWith("ref:")) {
      const ref = head.slice(5).trim();
      return fs.readFileSync(path.join(paths.root, ".git", ref), "utf8").trim().slice(0, 12);
    }
    return head.slice(0, 12);
  } catch {
    return "uncommitted";
  }
}
