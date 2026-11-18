import type { RunReport, CaseResult } from "./run-reporter.js";

const icon: Record<CaseResult["status"], string> = { passed: "✅", failed: "❌", timedOut: "⏱️", skipped: "⏭️", interrupted: "🛑" };

function ms(n: number) {
  if (n < 1000) return `${n} ms`;
  if (n < 60_000) return `${(n / 1000).toFixed(1)} s`;
  return `${Math.floor(n / 60_000)}m ${Math.round((n % 60_000) / 1000)}s`;
}

export function renderMarkdown(r: RunReport): string {
  const lines: string[] = [];
  lines.push(`# Run ${r.runId}`);
  lines.push("");
  lines.push(`| | |`);
  lines.push(`|---|---|`);
  lines.push(`| Suite | \`${r.suite}\` |`);
  lines.push(`| Target | ${r.target} |`);
  lines.push(`| Started | ${r.startedAt} |`);
  lines.push(`| Duration | ${ms(r.durationMs)} |`);
  lines.push(`| Result | ${r.totals.passed} passed · ${r.totals.failed} failed · ${r.totals.timedOut} timed out · ${r.totals.skipped} skipped |`);
  lines.push(`| Operator | ${r.operator} · harness ${r.gitSha} · node ${r.env.node} · playwright ${r.env.playwright} |`);
  lines.push("");
  if (r.notes.length) {
    lines.push("## Notes");
    for (const n of r.notes) lines.push(`- ${n}`);
    lines.push("");
  }
  lines.push("## Cases");
  lines.push("");
  lines.push("| Result | Case IDs | Test | Duration | Cost | Project(s) |");
  lines.push("|---|---|---|---|---|---|");
  for (const c of r.cases) {
    const projects = c.projects.map((p) => p.split(" ")[1] ?? p).join("<br>");
    lines.push(`| ${icon[c.status]} ${c.status} | ${c.caseIds.map((id) => `\`${id}\``).join(" ") || "—"} | ${escapePipes(c.testTitle)} | ${ms(c.durationMs)} | ${c.costUsd !== undefined ? `$${c.costUsd.toFixed(3)}` : "—"} | ${projects || "—"} |`);
  }
  lines.push("");
  const failures = r.cases.filter((c) => c.status === "failed" || c.status === "timedOut" || c.status === "interrupted");
  if (failures.length) {
    lines.push("## Failures");
    lines.push("");
    for (const c of failures) {
      lines.push(`### ${icon[c.status]} ${escapePipes(c.testTitle)}`);
      lines.push("");
      lines.push(`- File: \`${c.file}\``);
      if (c.caseIds.length) lines.push(`- Cases: ${c.caseIds.map((id) => `\`${id}\``).join(", ")}`);
      if (c.projects.length) lines.push(`- Projects: ${c.projects.map((p) => `\`${p}\``).join(", ")}`);
      if (c.runner) lines.push(`- Runner: ${c.runner}`);
      if (c.artifacts.trace) lines.push(`- Trace: \`${c.artifacts.trace}\` (open with \`npx playwright show-trace <path>\`)`);
      for (const s of c.artifacts.screenshots) lines.push(`- Screenshot: \`${s}\``);
      if (c.failure) {
        lines.push("");
        lines.push("```");
        lines.push(c.failure.message);
        lines.push("```");
        lines.push(`Logs: ${c.failure.logHint}`);
      }
      lines.push("");
    }
  }
  const skipped = r.cases.filter((c) => c.status === "skipped");
  if (skipped.length) {
    lines.push("## Skipped");
    lines.push("");
    for (const c of skipped) lines.push(`- ${escapePipes(c.testTitle)}${c.skipReason ? ` — ${c.skipReason}` : ""}`);
    lines.push("");
  }
  lines.push("## Cleanup");
  lines.push("");
  lines.push(`- Projects created this run: ${r.cleanup.createdProjects.length}`);
  for (const p of r.cleanup.createdProjects) lines.push(`  - \`${p.name}\` (${p.id})`);
  lines.push(`- Leftovers after teardown: ${r.cleanup.leftovers.length ? r.cleanup.leftovers.join(", ") : "none"}`);
  lines.push("");
  return lines.join("\n");
}

function escapePipes(s: string) {
  return s.replace(/\|/g, "\\|");
}
